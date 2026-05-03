using System;
using System.Collections.Generic;
using System.Linq;
using Google.OrTools.LinearSolver;

namespace Nova.Core.Resources;

// Iterative max-min fair LP. Per device priority, repeatedly solve
//
//     max α  subject to:
//         activity[d] ≥ α × Demand[d]   (for active devices at this priority)
//         supply[p]   ≥ α × amount_n[p] (for active pools at this priority)
//         conservation, edge bounds, parent constraints, pool/device caps
//         fixed flows from previous iterations (other priorities, prior bottlenecks)
//
// On each iteration: identify the binding entity (a device, pool, or
// edge that's at its physical limit), pin its flow at the α*-value,
// remove it from the active set, and re-solve. Loop ends when α* ≥ 1
// (all demand met for this priority) or the active set empties (no
// further progress possible). Then move to the next priority.
//
// Why this shape:
//   - Symmetric same-priority pools drain in lockstep — α forces
//     supply / amount to be equal across active pools.
//   - Disconnected sub-networks decouple: each component's α-LP is
//     independent because conservation only spans flow-connected nodes.
//   - Conversion chains (A → B → C via converter devices) are LP-native
//     and the α-LP doesn't disturb them.
//   - Closed cycles (e.g. life-support O₂ ⇄ CO₂) work — LP is steady
//     state; cyclic flow vars are just constraints that close.
//   - Edge rate limits, when added, slot in as additional linear
//     constraints; iteration's bottleneck identification handles them
//     by construction.
//
// The pool-α coefficient is amount[p] / max_amount[r] within its
// resource — keeps the LP coefficient in [0, 1], avoiding the dynamic-
// range issues that would come from putting raw Buffer.Contents in
// (see docs/lp_hygiene.md). A 0.1 floor avoids tiny pools producing
// near-zero coefficients; pools below 10% of max still participate
// in fairness, just at a slightly looser bound.
public class ResourceSolver {

  // ── Public API types ─────────────────────────────────────────────────

  public enum Priority { Critical, High, Low }

  public class Node {
    internal long id;
    public long Id => id;
    internal List<Device> devices = new();
    internal List<Buffer> buffers = new();

    public double DryMass;
    public bool Jettisoned;
    public int DrainPriority;

    public IReadOnlyList<Device> Devices => devices;
    public IReadOnlyList<Buffer> Buffers => buffers;

    public Device AddDevice(Priority priority) {
      var device = new Device { node = this, priority = priority };
      devices.Add(device);
      return device;
    }

    public Buffer AddBuffer(Resource resource, double capacity) {
      var buffer = new Buffer {
        Resource = resource,
        Capacity = capacity,
        Contents = capacity,
        MaxRateIn = 0,
        MaxRateOut = 0,
      };
      buffers.Add(buffer);
      return buffer;
    }

    public void IntegrateBuffers(double dt) {
      foreach (var buffer in buffers)
        buffer.Integrate(dt);
    }

    public double TimeToNextExpiry() {
      var earliest = double.PositiveInfinity;
      foreach (var buffer in buffers) {
        if (buffer.Rate < 0 && buffer.Contents > 1e-9)
          earliest = Math.Min(earliest, buffer.Contents / (-buffer.Rate));
        else if (buffer.Rate > 0 && buffer.Contents < buffer.Capacity - 1e-9)
          earliest = Math.Min(earliest, (buffer.Capacity - buffer.Contents) / buffer.Rate);
      }
      return earliest;
    }

    public double Mass() {
      double mass = DryMass;
      foreach (var buffer in buffers)
        mass += buffer.Contents * buffer.Resource.Density;
      return mass;
    }

    internal List<Node> children = new();

    public IEnumerable<Node> AllNodes() {
      yield return this;
      foreach (var child in children)
        foreach (var node in child.AllNodes())
          yield return node;
    }
  }

  public class Device {
    internal Node node;
    internal Priority priority;
    internal List<(Resource Resource, double MaxRate)> inputs = new();
    internal List<(Resource Resource, double MaxRate)> outputs = new();
    internal Device parent;
    internal Variable Var;

    public double Demand;
    public double Activity;
    public double Satisfaction => Demand > 1e-9 ? Activity / Demand : 0;
    public double MaxActivity = 1;
    public double ValidUntil = double.PositiveInfinity;

    public void AddInput(Resource resource, double maxRate) {
      inputs.Add((resource, maxRate));
    }

    public void AddOutput(Resource resource, double maxRate) {
      outputs.Add((resource, maxRate));
    }

    public void AddParent(Device device) {
      parent = device;
    }
  }

  // ── Internal types ───────────────────────────────────────────────────

  private class Edge {
    public Node Parent;
    public Node Child;
    public HashSet<Resource> AllowedResources;
    public HashSet<Resource> UpOnlyResources = new();
  }

  private class Pool {
    public Node Node;
    public Resource Resource;
    public int DrainPriority;
    public List<Buffer> Tanks = new();
    public Variable SupplyVar;
    public Variable FillVar;
    // Pre-allocated α-fairness constraint:
    //   SupplyVar - α × normalized_amount ≥ 0
    // Coefficient on α set per-iteration; bounds toggled to enable/disable.
    public Constraint AlphaConstraint;
    public double TotalAmount => Tanks.Sum(t => t.Contents > 1e-9 ? t.Contents : 0);
    public double MaxSupplyRate => Tanks.Sum(t => t.Contents > 1e-9 ? t.MaxRateOut : 0);
    public double MaxFillRate => Tanks.Sum(t => t.Contents < t.Capacity - 1e-9 ? t.MaxRateIn : 0);
  }

  private class FlowVar {
    public Node Parent;
    public Node Child;
    public Resource Resource;
    public bool UpOnly;
    public Variable Var;
  }

  private class ConservationEntry {
    public Node Node;
    public Resource Resource;
    public Constraint Eq;
  }

  // ── State ────────────────────────────────────────────────────────────

  // Optional diagnostic logger. NovaVesselModule wires this through
  // VirtualVessel — same sink as the rest of the engine. Used only on
  // anomalous events (non-OPTIMAL LP solves, suspicious α/β magnitudes,
  // iteration caps reached, slow solves) so it doesn't flood under
  // normal operation.
  public Action<string> Log;

  private List<Node> nodes = new();
  private List<Edge> edges = new();
  private Dictionary<Resource, double> drainCosts = new();
  private long nextNodeId;

  // Persistent LP — built once when topology is finalized, mutated per Solve.
  private bool topologyFinalized;
  private List<Pool> pools = new();
  private List<FlowVar> flowVars = new();
  private List<ConservationEntry> conservationEntries = new();
  private Dictionary<(Node, Resource), ConservationEntry> conservationByNodeRes = new();
  private Solver lpSolver;

  // α-LP apparatus.
  private Variable alphaVar;
  private Dictionary<Device, Constraint> deviceAlpha = new();
  // poolsByResource lets us compute per-resource max amount each tick
  // (for normalizing pool α-coefficients).
  private Dictionary<Resource, List<Pool>> poolsByResource = new();

  // Coefficient floor for pool α-fairness — keeps LP coefficients away
  // from GLOP's tolerance floor (~1e-6) when pool amounts span widely
  // within a resource.
  private const double FairnessFloor = 0.1;

  // Bound below which a value counts as "zero" for tightness checks.
  private const double Epsilon = 1e-9;

  // ── Public methods ───────────────────────────────────────────────────

  public Node AddNode() {
    var node = new Node { id = nextNodeId++ };
    nodes.Add(node);
    return node;
  }

  public void AddEdge(Node parent, Node child,
      HashSet<Resource> allowedResources = null,
      HashSet<Resource> upOnlyResources = null) {
    var edge = new Edge {
      Parent = parent,
      Child = child,
      AllowedResources = allowedResources,
      UpOnlyResources = upOnlyResources ?? new HashSet<Resource>(),
    };
    edges.Add(edge);
    parent.children.Add(child);
  }

  // Per-resource staging modifier. Nonzero costs push the corresponding
  // resource into a later DrainPriority class than its node would
  // otherwise belong to. Currently no callers set non-zero costs; kept
  // for API compatibility.
  public void SetDrainCost(Resource resource, double cost) {
    drainCosts[resource] = cost;
  }

  public IEnumerable<Node> AllNodes() => nodes;

  public IEnumerable<Node> ActiveNodes() => nodes.Where(n => !n.Jettisoned);

  // ── Reach ────────────────────────────────────────────────────────────

  public HashSet<Node> ReachableNodes(Node from, Resource resource) {
    var visited = new HashSet<Node>();
    if (from == null || from.Jettisoned) return visited;
    visited.Add(from);
    var stack = new Stack<Node>();
    stack.Push(from);
    while (stack.Count > 0) {
      var n = stack.Pop();
      foreach (var e in edges) {
        if (e.AllowedResources != null && !e.AllowedResources.Contains(resource)) continue;
        Node other;
        if (e.Parent == n) {
          other = e.Child;
        } else if (e.Child == n) {
          if (e.UpOnlyResources.Contains(resource)) continue;
          other = e.Parent;
        } else continue;
        if (other.Jettisoned) continue;
        if (visited.Add(other)) stack.Push(other);
      }
    }
    return visited;
  }

  public List<Buffer> ReachableBuffers(Node from, Resource resource) {
    var buffers = new List<Buffer>();
    foreach (var node in ReachableNodes(from, resource))
      foreach (var buf in node.buffers)
        if (ReferenceEquals(buf.Resource, resource)) buffers.Add(buf);
    return buffers;
  }

  // ── Solve ────────────────────────────────────────────────────────────

  public void Solve() {
    var solveStart = System.Diagnostics.Stopwatch.GetTimestamp();
    var solveTickFreq = System.Diagnostics.Stopwatch.Frequency;

    if (!topologyFinalized) {
      FinalizeTopology();
      BuildLP();
      topologyFinalized = true;
      Log?.Invoke($"[Solver] BuildLP: nodes={nodes.Count} pools={pools.Count} flowVars={flowVars.Count} conservationEntries={conservationEntries.Count}");
    }

    ResetPerTickBounds();

    // Pinned-entity tracking. Pinning sets a Variable's bounds to a
    // single value; once pinned, the entity's flow stays fixed through
    // the rest of this Solve.
    var pinnedDevices = new HashSet<Device>();
    var pinnedPools = new HashSet<Pool>();

    // Outer loop: device priorities, descending (Critical first).
    var devicePriorities = nodes.SelectMany(n => n.devices)
                                .Select(d => d.priority)
                                .Distinct()
                                .OrderBy(p => (int)p)
                                .ToList();

    foreach (var devPri in devicePriorities) {
      var devicesAtPri = nodes.SelectMany(n => n.devices)
        .Where(d => d.priority == devPri
                 && !pinnedDevices.Contains(d)
                 && !d.node.Jettisoned
                 && d.Demand > Epsilon)
        .ToList();

      // Phase A: max α for device satisfaction at this priority. Only
      // device-α-fairness is active (pool fairness is Phase B's job).
      // Pool supplies are NOT pinned here — Phase B redistributes them
      // fairly subject to the activities pinned by Phase A.
      if (devicesAtPri.Count > 0) {
        IterateDeviceAlpha(devicesAtPri, pinnedDevices);
      }

      // Phase B: max β for pool drain fairness, sub-stepped by pool
      // DrainPriority. Devices are now pinned for this priority; pool
      // drains follow conservation, but among pools at the same DP that
      // share a resource, β-maximin enforces proportional drain.
      //
      // High-DP pools drain first; if they satisfy demand, low-DP pools
      // get pinned at zero on a subsequent round (their β-LP returns
      // β=0 with no slack).
      var drainPriorities = pools
        .Where(p => !pinnedPools.Contains(p) && !p.Node.Jettisoned)
        .Select(p => p.DrainPriority)
        .Distinct()
        .OrderByDescending(dp => dp)
        .ToList();

      foreach (var dp in drainPriorities) {
        var poolsAtDp = pools
          .Where(p => p.DrainPriority == dp
                   && !pinnedPools.Contains(p)
                   && !p.Node.Jettisoned
                   && p.MaxSupplyRate > Epsilon)
          .ToList();

        if (poolsAtDp.Count == 0) continue;

        // Per-resource β. Conservation is per-(node, resource) — each
        // resource's flow network is independent (no cross-resource
        // coupling except through converter devices, whose activities
        // were already pinned in Phase A). Solving each resource's β-LP
        // independently means the binding resource (e.g. RP-1 with the
        // tightest demand/supply ratio) doesn't cap fairness for slack-
        // ier resources (e.g. LOX with more spare capacity).
        var byResource = new Dictionary<Resource, List<Pool>>();
        foreach (var p in poolsAtDp) {
          if (!byResource.TryGetValue(p.Resource, out var list))
            byResource[p.Resource] = list = new List<Pool>();
          list.Add(p);
        }
        foreach (var group in byResource.Values) {
          IteratePoolBeta(group, pinnedPools);
        }
      }
    }

    var solveMs = (System.Diagnostics.Stopwatch.GetTimestamp() - solveStart) * 1000.0 / solveTickFreq;
    if (solveMs > 50) {
      Log?.Invoke($"[Solver] slow Solve: {solveMs:F1}ms (nodes={nodes.Count} pools={pools.Count})");
    }
  }

  // Phase A: maximize device-side α. Iteratively pin devices that hit
  // their demand cap and re-solve for the residual. Pool variables are
  // free during this phase — supply gets distributed however the LP
  // basis picks. Phase B re-distributes pools fairly afterward.
  //
  // Solution capture pattern: GLOP invalidates its basis on any bound
  // mutation, after which Variable.SolutionValue() returns 0 until the
  // next Solve. We snapshot values immediately after each Solve and
  // call ExtractResults while the basis is fresh, then mutate.
  private void IterateDeviceAlpha(List<Device> activeDevices,
      HashSet<Device> pinnedDevices) {

    var devs = new List<Device>(activeDevices);
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

      // Objective: max α + ε × Σ activity. The α term is the primary
      // max-min fairness target. The small ε-weighted sum fills
      // activities up to their physical UBs in the slack region — so
      // when one device is supply-blocked (forces α* = 0), the others
      // still get pushed to their demand cap rather than left at the
      // basis-arbitrary lower bound.
      var obj = new LinearExpr() + alphaVar;
      foreach (var d in devs) obj += d.Var * 1e-3;
      lpSolver.Maximize(obj);
      var status = lpSolver.Solve();

      if (status != Solver.ResultStatus.OPTIMAL) {
        Log?.Invoke($"[Solver] Phase A non-OPTIMAL: status={status} iter={iter} active devices={devs.Count}");
        foreach (var d in devs) {
          d.Var.SetBounds(0, 0);
          d.Activity = 0;
          pinnedDevices.Add(d);
        }
        DeactivateAllAlphaConstraints();
        return;
      }

      var alphaStar = alphaVar.SolutionValue();
      if (alphaStar > 1e5) {
        Log?.Invoke($"[Solver] Phase A α high: α*={alphaStar:E2} iter={iter} (suspect unbounded)");
      }
      var devValues = new Dictionary<Device, double>(devs.Count);
      foreach (var d in devs) devValues[d] = d.Var.SolutionValue();

      // Capture LP solution into Device.Activity / Buffer.Rate before
      // mutating. (Buffer.Rate captured here is provisional; Phase B
      // will overwrite with the fair distribution.)
      ExtractResults();

      if (alphaStar >= 1.0 - Epsilon) {
        foreach (var d in devs) {
          d.Var.SetBounds(devValues[d], devValues[d]);
          pinnedDevices.Add(d);
        }
        DeactivateAllAlphaConstraints();
        return;
      }

      // α < 1: find devices at their physical UB. Pin those, recurse on
      // the rest.
      var bottlenecks = new List<Device>();
      foreach (var d in devs) {
        var ub = Math.Min(d.Demand, d.MaxActivity);
        if (devValues[d] >= ub - Epsilon) bottlenecks.Add(d);
      }

      if (bottlenecks.Count == 0) {
        // Conservation-bound — supply can't satisfy demand. Pin all at
        // current LP values.
        foreach (var d in devs) {
          d.Var.SetBounds(devValues[d], devValues[d]);
          pinnedDevices.Add(d);
        }
        DeactivateAllAlphaConstraints();
        return;
      }

      foreach (var d in bottlenecks) {
        d.Var.SetBounds(devValues[d], devValues[d]);
        pinnedDevices.Add(d);
      }
      devs.RemoveAll(d => bottlenecks.Contains(d));
    }

    // Iteration cap fallback.
    Log?.Invoke($"[Solver] Phase A iteration cap reached: maxIter={maxIter} remaining devices={devs.Count}");
    foreach (var d in devs) {
      d.Var.SetBounds(d.Activity, d.Activity);
      pinnedDevices.Add(d);
    }
    DeactivateAllAlphaConstraints();
  }

  // Phase B: maximize pool-side β at one DrainPriority class. Active
  // devices are already pinned by Phase A; pool drains are determined
  // up to the slack region inside conservation. β-maximin enforces the
  // most-balanced distribution: supply_p ≥ β × normalized_amount_p,
  // max β. Iterates with bottleneck handling — pools at MaxSupplyRate
  // get pinned and the residual demand is shared among the rest.
  //
  // Each iteration is a lexicographic two-step solve:
  //   1. max β (the fairness target).
  //   2. pin β at β*, min Σ supply (cycle suppression / anti-sloshing).
  //
  // Step 2 is what prevents the LP from picking a `supply=X, fill=X`
  // solution that satisfies conservation through pure cycling — a
  // pathology that surfaces in any flow LP without flow-cost
  // penalization. The old solver's drain-min phase did the same job
  // by costing supply asymmetrically; we now do it cleanly via
  // lex-max-min.
  private void IteratePoolBeta(List<Pool> activePools,
      HashSet<Pool> pinnedPools) {

    var pls = new List<Pool>(activePools);
    int maxIter = pls.Count + 1;

    for (int iter = 0; iter < maxIter; iter++) {
      if (pls.Count == 0) return;

      DeactivateAllAlphaConstraints();

      // Per-resource max among active pools, for normalization.
      var maxByResource = new Dictionary<Resource, double>();
      foreach (var p in pls) {
        var amt = p.TotalAmount;
        if (amt > Epsilon) {
          if (!maxByResource.TryGetValue(p.Resource, out var ex) || amt > ex)
            maxByResource[p.Resource] = amt;
        }
      }

      foreach (var p in pls) {
        var amt = p.TotalAmount;
        if (amt < Epsilon) continue;
        if (!maxByResource.TryGetValue(p.Resource, out var max) || max < Epsilon) continue;
        var norm = Math.Max(amt / max, FairnessFloor);
        var c = p.AlphaConstraint;
        c.SetCoefficient(alphaVar, -norm);
        c.SetBounds(0, double.PositiveInfinity);
      }

      // Step 1: max β.
      alphaVar.SetBounds(0, 1e6);
      lpSolver.Maximize(alphaVar);
      var status = lpSolver.Solve();

      if (status != Solver.ResultStatus.OPTIMAL) {
        Log?.Invoke($"[Solver] Phase B step 1 non-OPTIMAL: status={status} iter={iter} active pools={pls.Count}");
        foreach (var p in pls) {
          p.SupplyVar.SetBounds(0, 0);
          foreach (var t in p.Tanks) t.Rate = 0;
          pinnedPools.Add(p);
        }
        DeactivateAllAlphaConstraints();
        alphaVar.SetBounds(0, 1e6);
        return;
      }

      var betaStar = alphaVar.SolutionValue();
      if (betaStar > 1e5) {
        Log?.Invoke($"[Solver] Phase B β high: β*={betaStar:E2} iter={iter} (suspect unbounded; check tank MaxRate)");
      }

      // Step 2: pin β at β*, min Σ supply over all pools. The sum
      // includes pinned pools too — they appear as constants and don't
      // change the optimization but it keeps the objective expression
      // stable. This step picks the no-sloshing solution at β*.
      alphaVar.SetBounds(betaStar, betaStar);
      var minSupplyObj = new LinearExpr();
      foreach (var p in pools) minSupplyObj += p.SupplyVar;
      lpSolver.Minimize(minSupplyObj);
      var status2 = lpSolver.Solve();

      if (status2 != Solver.ResultStatus.OPTIMAL) {
        // Anti-sloshing infeasible (shouldn't happen if max β was
        // OPTIMAL — same feasible region with one variable pinned).
        // Fail-safe: pin everyone at zero.
        Log?.Invoke($"[Solver] Phase B step 2 non-OPTIMAL: status={status2} iter={iter} β*={betaStar:F3}");
        foreach (var p in pls) {
          p.SupplyVar.SetBounds(0, 0);
          foreach (var t in p.Tanks) t.Rate = 0;
          pinnedPools.Add(p);
        }
        DeactivateAllAlphaConstraints();
        alphaVar.SetBounds(0, 1e6);
        return;
      }

      var poolValues = new Dictionary<Pool, double>(pls.Count);
      foreach (var p in pls) poolValues[p] = p.SupplyVar.SolutionValue();

      ExtractResults();

      if (betaStar < Epsilon) {
        foreach (var p in pls) {
          p.SupplyVar.SetBounds(poolValues[p], poolValues[p]);
          pinnedPools.Add(p);
        }
        DeactivateAllAlphaConstraints();
        alphaVar.SetBounds(0, 1e6);
        return;
      }

      var bottlenecks = new List<Pool>();
      foreach (var p in pls) {
        if (poolValues[p] >= p.MaxSupplyRate - Epsilon && p.MaxSupplyRate > Epsilon)
          bottlenecks.Add(p);
      }

      if (bottlenecks.Count == 0) {
        foreach (var p in pls) {
          p.SupplyVar.SetBounds(poolValues[p], poolValues[p]);
          pinnedPools.Add(p);
        }
        DeactivateAllAlphaConstraints();
        alphaVar.SetBounds(0, 1e6);
        return;
      }

      foreach (var p in bottlenecks) {
        p.SupplyVar.SetBounds(poolValues[p], poolValues[p]);
        pinnedPools.Add(p);
      }
      pls.RemoveAll(p => bottlenecks.Contains(p));
    }

    // Iteration cap fallback.
    Log?.Invoke($"[Solver] Phase B iteration cap reached: maxIter={maxIter} remaining pools={pls.Count}");
    foreach (var p in pls) {
      double sup = 0;
      foreach (var t in p.Tanks) sup -= t.Rate;
      if (sup < 0) sup = 0;
      p.SupplyVar.SetBounds(sup, sup);
      pinnedPools.Add(p);
    }
    DeactivateAllAlphaConstraints();
    alphaVar.SetBounds(0, 1e6);
  }

  // Wide-bound all α-fairness constraints (devices + pools). Called at
  // the start of each phase iteration to guarantee a clean slate.
  private void DeactivateAllAlphaConstraints() {
    foreach (var c in deviceAlpha.Values)
      c.SetBounds(double.NegativeInfinity, double.PositiveInfinity);
    foreach (var p in pools)
      if (p.AlphaConstraint != null)
        p.AlphaConstraint.SetBounds(double.NegativeInfinity, double.PositiveInfinity);
  }

  // ── Topology finalization ────────────────────────────────────────────

  private void FinalizeTopology() {
    var nodeResources = BuildNodeResources();
    BuildPools();
    BuildFlowVars(nodeResources);
    BuildConservationEntries(nodeResources);
  }

  private Dictionary<Node, HashSet<Resource>> BuildNodeResources() {
    var result = new Dictionary<Node, HashSet<Resource>>();
    foreach (var node in nodes) {
      var resources = new HashSet<Resource>();
      foreach (var d in node.devices) {
        foreach (var (res, _) in d.inputs) resources.Add(res);
        foreach (var (res, _) in d.outputs) resources.Add(res);
      }
      foreach (var b in node.buffers) resources.Add(b.Resource);
      result[node] = resources;
    }

    // Propagate: parent nodes get resources reachable through edges.
    bool changed = true;
    while (changed) {
      changed = false;
      foreach (var edge in edges) {
        if (!result.TryGetValue(edge.Child, out var childRes)) continue;
        if (!result.TryGetValue(edge.Parent, out var parentRes)) {
          parentRes = new HashSet<Resource>();
          result[edge.Parent] = parentRes;
        }
        foreach (var res in childRes) {
          if (edge.AllowedResources != null && !edge.AllowedResources.Contains(res)) continue;
          if (parentRes.Add(res)) changed = true;
        }
      }
    }

    return result;
  }

  private void BuildPools() {
    pools.Clear();
    poolsByResource.Clear();
    foreach (var node in nodes) {
      var byResource = new Dictionary<Resource, Pool>();
      foreach (var buffer in node.buffers) {
        if (!byResource.TryGetValue(buffer.Resource, out var pool)) {
          pool = new Pool {
            Node = node,
            Resource = buffer.Resource,
            DrainPriority = node.DrainPriority,
          };
          byResource[buffer.Resource] = pool;
          pools.Add(pool);
          if (!poolsByResource.TryGetValue(pool.Resource, out var list))
            poolsByResource[pool.Resource] = list = new List<Pool>();
          list.Add(pool);
        }
        pool.Tanks.Add(buffer);
      }
    }
  }

  private void BuildFlowVars(Dictionary<Node, HashSet<Resource>> nodeResources) {
    flowVars.Clear();
    foreach (var edge in edges) {
      if (!nodeResources.TryGetValue(edge.Child, out var childRes)) continue;
      foreach (var res in childRes) {
        if (edge.AllowedResources != null && !edge.AllowedResources.Contains(res)) continue;
        flowVars.Add(new FlowVar {
          Parent = edge.Parent,
          Child = edge.Child,
          Resource = res,
          UpOnly = edge.UpOnlyResources.Contains(res),
        });
      }
    }
  }

  private void BuildConservationEntries(Dictionary<Node, HashSet<Resource>> nodeResources) {
    conservationEntries.Clear();
    conservationByNodeRes.Clear();
    foreach (var kvp in nodeResources) {
      foreach (var res in kvp.Value) {
        var entry = new ConservationEntry { Node = kvp.Key, Resource = res };
        conservationEntries.Add(entry);
        conservationByNodeRes[(kvp.Key, res)] = entry;
      }
    }
  }

  private ConservationEntry ConservationFor(Node node, Resource res) {
    return conservationByNodeRes.TryGetValue((node, res), out var entry) ? entry : null;
  }

  // ── One-shot LP construction ─────────────────────────────────────────

  private void BuildLP() {
    lpSolver = Solver.CreateSolver("GLOP");

    // The α scalar. Bounded above by a generous finite cap to keep
    // GLOP's pivot numerics happy; α should always settle in [0, ~few]
    // in normal operation.
    alphaVar = lpSolver.MakeNumVar(0, 1e6, "alpha");

    // Device variables.
    int di = 0;
    foreach (var node in nodes)
      foreach (var device in node.devices)
        device.Var = lpSolver.MakeNumVar(0, double.PositiveInfinity, $"d_{di++}");

    // Pool supply + fill variables.
    foreach (var pool in pools) {
      pool.SupplyVar = lpSolver.MakeNumVar(0, double.PositiveInfinity,
        $"s_{pool.Node.id}_{pool.Resource.Abbreviation}");
      pool.FillVar = lpSolver.MakeNumVar(0, double.PositiveInfinity,
        $"f_{pool.Node.id}_{pool.Resource.Abbreviation}");
    }

    // Flow variables.
    int fi = 0;
    foreach (var fv in flowVars) {
      double lb = double.NegativeInfinity;
      double ub = fv.UpOnly ? 0 : double.PositiveInfinity;
      fv.Var = lpSolver.MakeNumVar(lb, ub,
        $"flow_{fv.Parent.id}_{fv.Child.id}_{fv.Resource.Abbreviation}_{fi++}");
    }

    // Conservation equality constraints.
    foreach (var entry in conservationEntries)
      entry.Eq = lpSolver.MakeConstraint(0, 0,
        $"Cons_{entry.Node.id}_{entry.Resource.Abbreviation}");

    // Wire device input (consumption, -maxRate) and output (production,
    // +maxRate) into conservation rows. Topology-constant.
    foreach (var node in nodes)
      foreach (var device in node.devices) {
        foreach (var (res, maxRate) in device.inputs) {
          var entry = ConservationFor(node, res);
          if (entry != null)
            entry.Eq.SetCoefficient(device.Var, -maxRate);
        }
        foreach (var (res, maxRate) in device.outputs) {
          var entry = ConservationFor(node, res);
          if (entry != null)
            entry.Eq.SetCoefficient(device.Var, maxRate);
        }
      }

    // Wire pool supply (+1) and fill (-1) into conservation.
    foreach (var pool in pools) {
      var entry = ConservationFor(pool.Node, pool.Resource);
      if (entry != null) {
        entry.Eq.SetCoefficient(pool.SupplyVar, 1);
        entry.Eq.SetCoefficient(pool.FillVar, -1);
      }
    }

    // Wire flow variables (positive flow = parent→child: parent loses, child gains).
    foreach (var fv in flowVars) {
      var parentEntry = ConservationFor(fv.Parent, fv.Resource);
      var childEntry = ConservationFor(fv.Child, fv.Resource);
      if (parentEntry != null)
        parentEntry.Eq.SetCoefficient(fv.Var, -1);
      if (childEntry != null)
        childEntry.Eq.SetCoefficient(fv.Var, 1);
    }

    // Parent constraints: device.activity ≤ parent.activity. One-way.
    foreach (var node in nodes)
      foreach (var device in node.devices) {
        if (device.parent == null) continue;
        var c = lpSolver.MakeConstraint(double.NegativeInfinity, 0,
          $"Parent_{device.Var.Name()}");
        c.SetCoefficient(device.Var, 1);
        c.SetCoefficient(device.parent.Var, -1);
      }

    // Pre-allocate device α-fairness constraints. One per device:
    //   activity[d] - α × Demand[d] ≥ 0   (when active)
    // Coefficient on activity is fixed at 1; coefficient on α and the
    // bound are toggled per-iteration.
    foreach (var node in nodes)
      foreach (var device in node.devices) {
        var c = lpSolver.MakeConstraint(double.NegativeInfinity, double.PositiveInfinity,
          $"DevAlpha_{device.Var.Name()}");
        c.SetCoefficient(device.Var, 1);
        deviceAlpha[device] = c;
      }

    // Pre-allocate pool α-fairness constraints. One per pool:
    //   supply[p] - α × normalized_amount[p] ≥ 0   (when active)
    foreach (var pool in pools) {
      var c = lpSolver.MakeConstraint(double.NegativeInfinity, double.PositiveInfinity,
        $"PoolAlpha_{pool.Node.id}_{pool.Resource.Abbreviation}");
      c.SetCoefficient(pool.SupplyVar, 1);
      pool.AlphaConstraint = c;
    }
  }

  // ── Per-tick reset ──────────────────────────────────────────────────

  private void ResetPerTickBounds() {
    // Device UBs: clamped to min(Demand, MaxActivity), 0 if jettisoned.
    foreach (var node in nodes) {
      bool jett = node.Jettisoned;
      foreach (var device in node.devices) {
        var ub = jett ? 0 : Math.Min(device.Demand, device.MaxActivity);
        if (ub < 0) ub = 0;
        device.Var.SetBounds(0, ub);
      }
    }

    // Pool supply + fill UBs from buffer state.
    foreach (var pool in pools) {
      pool.SupplyVar.SetBounds(0, pool.MaxSupplyRate);
      pool.FillVar.SetBounds(0, pool.MaxFillRate);
    }

    // Conservation RHS resets.
    foreach (var entry in conservationEntries)
      entry.Eq.SetBounds(0, 0);

    // α-fairness constraints inactive — Solve activates per round.
    foreach (var c in deviceAlpha.Values)
      c.SetBounds(double.NegativeInfinity, double.PositiveInfinity);
    foreach (var pool in pools)
      if (pool.AlphaConstraint != null)
        pool.AlphaConstraint.SetBounds(double.NegativeInfinity, double.PositiveInfinity);
  }

  // ── Result extraction ────────────────────────────────────────────────

  private void ExtractResults() {
    foreach (var node in nodes) {
      foreach (var device in node.devices)
        device.Activity = node.Jettisoned ? 0 : device.Var.SolutionValue();
    }

    foreach (var pool in pools) {
      if (pool.Node.Jettisoned) {
        foreach (var tank in pool.Tanks) tank.Rate = 0;
        continue;
      }
      // Net rate from buffer's perspective: SupplyVar drains, FillVar fills.
      // Positive netRate = drain (buffer loses); negative = fill.
      var supply = pool.SupplyVar.SolutionValue();
      var fill = pool.FillVar.SolutionValue();
      var netRate = supply - fill;
      var totalAmount = pool.TotalAmount;
      foreach (var tank in pool.Tanks) {
        if (netRate > 0) {
          var share = totalAmount > Epsilon ? tank.Contents / totalAmount : 0;
          tank.Rate = -netRate * share;
        } else if (netRate < 0) {
          var totalSpace = pool.Tanks.Sum(t => t.Capacity - t.Contents);
          var share = totalSpace > Epsilon ? (tank.Capacity - tank.Contents) / totalSpace : 0;
          tank.Rate = -netRate * share;
        } else {
          tank.Rate = 0;
        }
      }
    }
  }
}
