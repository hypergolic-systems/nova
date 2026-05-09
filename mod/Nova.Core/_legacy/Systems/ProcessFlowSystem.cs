using System;
using System.Collections.Generic;
using System.Linq;
using Google.OrTools.LinearSolver;
using Nova.Core.Resources;
using Buffer = Nova.Core.Resources.Buffer;

namespace Nova.Core.Systems;

// LP solver for Uniform resources (ElectricCharge today; O₂, CO₂,
// H₂O, food, waste, heat tomorrow). Single vessel-wide pool per
// resource — no topology, no drain priority, no flow vars.
//
// Why LP for Uniform: producers/consumers/buffers can be many-to-many
// with simultaneous balance, possibly cyclic (closed-loop life support
// device A consumes O₂ produces CO₂; device B reverses). Water-fill
// doesn't fit; LP does.
//
// Algorithm — device-priority max-min fairness loop:
//
//   for each priority P in [Critical, High, Low]:
//     repeat:
//       max α + ε × Σ device.var
//       s.t. device.var ≥ α × Demand   (active P devices)
//            conservation per resource: Σ (output_p − input_d) × var
//                                       + supply_r − fill_r = 0
//            0 ≤ supply_r ≤ Σ (Contents>0) MaxRateOut
//            0 ≤ fill_r   ≤ Σ (Contents<Cap) MaxRateIn
//            0 ≤ device.var ≤ min(Demand, MaxActivity)
//       if α* ≥ 1: pin all at LP values, advance to next priority.
//       else: pin bottleneck devices (at their UB), recurse residual.
//
// The ε-weighted Σ activity term in the objective fills slack devices
// up to their UB when one device starves at α=0 — without it, those
// devices would sit at 0 (basis-arbitrary) instead of running.
//
// Buffer rate distribution: after solving, supply_r and fill_r are
// distributed across the buffers of resource r in proportion to
// current Contents (for supply: drain proportional to amount; for
// fill: fill proportional to remaining capacity).
public class ProcessFlowSystem : BackgroundSystem {

  // GLOP basis-tolerance bound; values below count as zero.
  private const double Epsilon = 1e-9;

  public enum Priority { Critical, High, Low }

  public class Device {
    internal Priority priority;
    internal List<(Resource Resource, double MaxRate)> inputs = new();
    internal List<(Resource Resource, double MaxRate)> outputs = new();
    internal Variable Var;

    public double Demand;
    public double Activity;
    public double MaxActivity = 1;
    public double ValidUntil = double.PositiveInfinity;
    public double Satisfaction => Demand > Epsilon ? Activity / Demand : 0;

    public void AddInput(Resource resource, double maxRate) {
      inputs.Add((resource, maxRate));
    }

    public void AddOutput(Resource resource, double maxRate) {
      outputs.Add((resource, maxRate));
    }
  }

  // ── State ──────────────────────────────────────────────────────────

  public Action<string> Log;

  private List<Device> devices = new();
  // Per-resource buffer aggregation. Buffers are added by callers
  // (e.g. NovaVesselModule registering Battery buffers); flow-wise
  // they merge into one vessel-wide pool per resource.
  private Dictionary<Resource, List<Buffer>> buffersByResource = new();

  // Snapshots of supply/fill values, captured during each LP solve
  // before any bound mutation invalidates the basis. The most recent
  // solve's values are cumulative over all priorities processed so far,
  // so DistributeBufferRates can read them straight off.
  private Dictionary<Resource, double> supplySnapshot = new();
  private Dictionary<Resource, double> fillSnapshot = new();

  // Persistent LP — built once when topology is finalized, mutated per Solve.
  private bool topologyFinalized;
  private Solver lpSolver;
  private Variable alphaVar;
  private Dictionary<Resource, Variable> supplyByResource = new();
  private Dictionary<Resource, Variable> fillByResource = new();
  private Dictionary<Resource, Constraint> conservationByResource = new();
  private Dictionary<Device, Constraint> deviceAlpha = new();

  private readonly SimClock clock;

  public SimClock Clock => clock;

  public ProcessFlowSystem(SimClock clock = null) {
    this.clock = clock ?? new SimClock();
  }

  // ── Public construction ────────────────────────────────────────────

  public Device AddDevice(Priority priority) {
    if (topologyFinalized) throw new InvalidOperationException(
        "Cannot add device after first Solve; rebuild the system instead.");
    var d = new Device { priority = priority };
    devices.Add(d);
    return d;
  }

  public void AddBuffer(Buffer buffer) {
    if (topologyFinalized) throw new InvalidOperationException(
        "Cannot add buffer after first Solve; rebuild the system instead.");
    if (buffer.Resource.Domain != ResourceDomain.Uniform) throw new ArgumentException(
        $"ProcessFlowSystem only accepts Uniform resources; got {buffer.Resource.Name} " +
        $"({buffer.Resource.Domain}). Topological resources belong on StagingFlowSystem.");
    // Adopt this system's clock and re-baseline at "now". Whatever
    // the buffer's prior BaselineUT was (default 0 from fresh
    // construction, or save-load left-over), it should be reset on
    // adoption so the lerp evaluates against the live clock from
    // here on. Rate stays at 0 — the next Solve will compute a real
    // rate at this baseline.
    buffer.Clock = clock;
    buffer.BaselineUT = clock.UT;
    if (!buffersByResource.TryGetValue(buffer.Resource, out var list))
      buffersByResource[buffer.Resource] = list = new List<Buffer>();
    list.Add(buffer);
  }

  public IReadOnlyList<Device> Devices => devices;

  // ── BackgroundSystem implementation ────────────────────────────────

  public override void Solve() {
    if (!topologyFinalized) {
      BuildLP();
      topologyFinalized = true;
    }

    // Re-baseline every buffer at the current clock UT before
    // mutating rates. The Rate setter would auto-rebaseline too,
    // but doing it here once means subsequent reads in the LP
    // (Contents > Epsilon checks, etc.) see a stable BaselineContents
    // matching the LP's "now".
    foreach (var pair in buffersByResource)
      foreach (var b in pair.Value) b.Refresh(clock.UT);

    ResetPerTickBounds();

    // Stale-Activity prophylactic. The priority loop only touches
    // devices with Demand > Epsilon; a device that just flipped from
    // active → inactive (Demand 1 → 0) would otherwise keep its
    // last-solve Activity from a prior cycle. Reaction-wheel hysteresis
    // hit this in flight: refill device flipped off (Demand=0) but
    // Activity stayed at 1.0, IntegrateReactionWheelBuffers read 1.0 ×
    // RefillRateWatts as a phantom bus draw forever, draining the
    // battery for no torque.
    foreach (var d in devices) d.Activity = 0;

    supplySnapshot.Clear();
    fillSnapshot.Clear();

    var pinned = new HashSet<Device>();
    var priorities = devices.Select(d => d.priority).Distinct().OrderBy(p => (int)p).ToList();

    foreach (var pri in priorities) {
      var active = devices.Where(d => d.priority == pri && !pinned.Contains(d) && d.Demand > Epsilon).ToList();
      if (active.Count > 0) IterateDeviceAlpha(active, pinned);
    }

    // Lex-2 cleanup: with every device var pinned, the LP still has
    // slack to circulate supply + fill (e.g. supply = 1212, fill =
    // 1202, both feasible, both yielding the same α). Minimize their
    // sum once to collapse to the no-cycling solution before reading.
    CleanupSupplyFill();

    DistributeBufferRates();
    needsSolve = false;
  }

  private void CleanupSupplyFill() {
    if (supplyByResource.Count == 0) return;
    DeactivateAllAlphaConstraints();
    alphaVar.SetBounds(0, 1e6);
    var obj = new LinearExpr();
    foreach (var pair in supplyByResource) obj += pair.Value;
    foreach (var pair in fillByResource) obj += pair.Value;
    lpSolver.Minimize(obj);
    var status = lpSolver.Solve();
    if (status != Solver.ResultStatus.OPTIMAL) {
      Log?.Invoke($"[ProcessFlow] cleanup non-OPTIMAL: status={status}");
      // Fall through; snapshots already populated from the priority loop.
      return;
    }
    foreach (var pair in supplyByResource)
      supplySnapshot[pair.Key] = pair.Value.SolutionValue();
    foreach (var pair in fillByResource)
      fillSnapshot[pair.Key] = pair.Value.SolutionValue();
  }

  // Complete relative-dt horizon for this system: the soonest of
  // every event that would invalidate the current solve. Includes
  // both buffer empty/fill times and any per-device ValidUntil
  // forecasts (solar shadow transition, fuel-cell SoC threshold,
  // reaction-wheel refill flip, etc.) — absolute UTs from the
  // device side get translated to relative dt against the shared
  // clock so the runner sees a single self-contained answer.
  public override double MaxTickDt() {
    double earliest = double.PositiveInfinity;
    foreach (var pair in buffersByResource) {
      foreach (var b in pair.Value) {
        var contents = b.Contents;
        if (b.Rate < -Epsilon && contents > Epsilon) {
          var t = contents / -b.Rate;
          if (t < earliest) earliest = t;
        } else if (b.Rate > Epsilon && contents < b.Capacity - Epsilon) {
          var t = (b.Capacity - contents) / b.Rate;
          if (t < earliest) earliest = t;
        }
      }
    }
    foreach (var d in devices) {
      if (double.IsPositiveInfinity(d.ValidUntil)) continue;
      var dt = d.ValidUntil - clock.UT;
      if (dt < 0) dt = 0;
      if (dt < earliest) earliest = dt;
    }
    return earliest;
  }


  // ── LP construction (one-shot, on first Solve) ─────────────────────

  private void BuildLP() {
    lpSolver = Solver.CreateSolver("GLOP");
    alphaVar = lpSolver.MakeNumVar(0, 1e6, "alpha");

    // Device variables.
    int di = 0;
    foreach (var d in devices)
      d.Var = lpSolver.MakeNumVar(0, double.PositiveInfinity, $"d_{di++}");

    // Supply / fill vars per resource.
    foreach (var pair in buffersByResource) {
      var r = pair.Key;
      supplyByResource[r] = lpSolver.MakeNumVar(0, double.PositiveInfinity, $"s_{r.Abbreviation}");
      fillByResource[r] = lpSolver.MakeNumVar(0, double.PositiveInfinity, $"f_{r.Abbreviation}");
    }

    // Resource set: union of all device input/output resources + buffer resources.
    var allResources = new HashSet<Resource>();
    foreach (var d in devices) {
      foreach (var (r, _) in d.inputs) allResources.Add(r);
      foreach (var (r, _) in d.outputs) allResources.Add(r);
    }
    foreach (var pair in buffersByResource) allResources.Add(pair.Key);

    // Conservation: Σ outputs − Σ inputs + supply − fill = 0 per resource.
    foreach (var r in allResources) {
      if (r.Domain != ResourceDomain.Uniform) continue;
      var c = lpSolver.MakeConstraint(0, 0, $"Cons_{r.Abbreviation}");
      foreach (var d in devices) {
        foreach (var (res, maxRate) in d.inputs)
          if (ReferenceEquals(res, r)) c.SetCoefficient(d.Var, -maxRate);
        foreach (var (res, maxRate) in d.outputs)
          if (ReferenceEquals(res, r)) c.SetCoefficient(d.Var, maxRate);
      }
      if (supplyByResource.TryGetValue(r, out var s)) c.SetCoefficient(s, 1);
      if (fillByResource.TryGetValue(r, out var f)) c.SetCoefficient(f, -1);
      conservationByResource[r] = c;
    }

    // Pre-allocate device α-fairness constraints. Inactive when bound
    // wide; coefficient on α is set per-iteration.
    foreach (var d in devices) {
      var c = lpSolver.MakeConstraint(double.NegativeInfinity, double.PositiveInfinity,
          $"DevAlpha_{d.Var.Name()}");
      c.SetCoefficient(d.Var, 1);
      deviceAlpha[d] = c;
    }
  }

  private void ResetPerTickBounds() {
    foreach (var d in devices) {
      var ub = Math.Min(d.Demand, d.MaxActivity);
      if (ub < 0) ub = 0;
      d.Var.SetBounds(0, ub);
    }
    foreach (var pair in buffersByResource) {
      var bufs = pair.Value;
      double maxOut = bufs.Sum(b => b.Contents > Epsilon ? b.MaxRateOut : 0);
      double maxIn = bufs.Sum(b => b.Contents < b.Capacity - Epsilon ? b.MaxRateIn : 0);
      supplyByResource[pair.Key].SetBounds(0, maxOut);
      fillByResource[pair.Key].SetBounds(0, maxIn);
    }
    foreach (var c in deviceAlpha.Values)
      c.SetBounds(double.NegativeInfinity, double.PositiveInfinity);
  }

  // ── Device priority α-iteration ────────────────────────────────────

  private void IterateDeviceAlpha(List<Device> active, HashSet<Device> pinned) {
    var devs = new List<Device>(active);
    int maxIter = devs.Count + 1;

    for (int iter = 0; iter < maxIter; iter++) {
      if (devs.Count == 0) return;

      DeactivateAllAlphaConstraints();
      alphaVar.SetBounds(0, 1e6);
      foreach (var d in devs) {
        var c = deviceAlpha[d];
        c.SetCoefficient(alphaVar, -d.Demand);
        c.SetBounds(0, double.PositiveInfinity);
      }

      var obj = new LinearExpr() + alphaVar;
      foreach (var d in devs) obj += d.Var * 1e-3;
      lpSolver.Maximize(obj);
      var status = lpSolver.Solve();

      if (status != Solver.ResultStatus.OPTIMAL) {
        Log?.Invoke($"[ProcessFlow] non-OPTIMAL: status={status} iter={iter} active={devs.Count}");
        foreach (var d in devs) {
          d.Var.SetBounds(0, 0);
          d.Activity = 0;
          pinned.Add(d);
        }
        DeactivateAllAlphaConstraints();
        return;
      }

      var alphaStar = alphaVar.SolutionValue();
      var devValues = new Dictionary<Device, double>(devs.Count);
      foreach (var d in devs) devValues[d] = d.Var.SolutionValue();

      // Capture activities + supply/fill snapshots now — once we mutate
      // bounds, GLOP invalidates the basis and SolutionValue() returns 0.
      foreach (var d in devs) d.Activity = devValues[d];
      foreach (var pair in supplyByResource)
        supplySnapshot[pair.Key] = pair.Value.SolutionValue();
      foreach (var pair in fillByResource)
        fillSnapshot[pair.Key] = pair.Value.SolutionValue();

      if (alphaStar >= 1.0 - Epsilon) {
        foreach (var d in devs) {
          d.Var.SetBounds(devValues[d], devValues[d]);
          pinned.Add(d);
        }
        DeactivateAllAlphaConstraints();
        return;
      }

      // α < 1: pin devices at physical UB, recurse on the rest.
      var bottlenecks = new List<Device>();
      foreach (var d in devs) {
        var ub = Math.Min(d.Demand, d.MaxActivity);
        if (devValues[d] >= ub - Epsilon) bottlenecks.Add(d);
      }

      if (bottlenecks.Count == 0) {
        // Conservation-bound at α < 1 — supply can't satisfy demand.
        // Pin everything at current LP values.
        foreach (var d in devs) {
          d.Var.SetBounds(devValues[d], devValues[d]);
          pinned.Add(d);
        }
        DeactivateAllAlphaConstraints();
        return;
      }

      foreach (var d in bottlenecks) {
        d.Var.SetBounds(devValues[d], devValues[d]);
        pinned.Add(d);
      }
      devs.RemoveAll(d => bottlenecks.Contains(d));
    }

    Log?.Invoke($"[ProcessFlow] iteration cap: maxIter={maxIter} remaining={devs.Count}");
    foreach (var d in devs) {
      d.Var.SetBounds(d.Activity, d.Activity);
      pinned.Add(d);
    }
    DeactivateAllAlphaConstraints();
  }

  private void DeactivateAllAlphaConstraints() {
    foreach (var c in deviceAlpha.Values)
      c.SetBounds(double.NegativeInfinity, double.PositiveInfinity);
  }

  // ── Buffer rate distribution ───────────────────────────────────────

  // After Solve, supply and fill values per resource are known. Spread
  // them across the resource's buffers proportional to current state:
  // drain proportional to Contents; fill proportional to remaining capacity.
  // Net buffer rate is (fill share − supply share); positive = filling.
  private void DistributeBufferRates() {
    // Reset all buffer rates first.
    foreach (var pair in buffersByResource)
      foreach (var b in pair.Value) b.Rate = 0;

    foreach (var pair in buffersByResource) {
      var r = pair.Key;
      var bufs = pair.Value;
      supplySnapshot.TryGetValue(r, out var supply);
      fillSnapshot.TryGetValue(r, out var fill);

      double totalContents = bufs.Sum(b => b.Contents > Epsilon ? b.Contents : 0);
      double totalSpace = bufs.Sum(b => b.Capacity - b.Contents > Epsilon ? b.Capacity - b.Contents : 0);

      foreach (var b in bufs) {
        double drainShare = totalContents > Epsilon && b.Contents > Epsilon
            ? supply * (b.Contents / totalContents) : 0;
        double fillShare = totalSpace > Epsilon && b.Capacity - b.Contents > Epsilon
            ? fill * ((b.Capacity - b.Contents) / totalSpace) : 0;
        // Buffer.Rate convention: positive = filling, negative = draining.
        b.Rate = fillShare - drainShare;
      }
    }
  }
}
