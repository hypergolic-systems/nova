using System;
using Nova.Core.Resources;

namespace Nova.Core.Systems;

// Container + orchestrator for the per-vessel simulation systems.
// Owned by VirtualVessel. Components register their domain-specific
// state with the right system (Topological flow → Staging, Uniform
// flow → Process) at OnBuildSystems time. The runner (VirtualVessel
// or DeltaVSimulation) drives the systems via Solve / MaxTickDt /
// AdvanceClock; it doesn't touch individual systems for ticking.
//
// Adding a system later (e.g. ControlSystem for hysteresis +
// reactive logic, ThermalSystem for heat) means a new field here +
// updates to Solve / MaxTickDt — the runner stays the same.
public class VesselSystems {
  // Per-vessel simulation clock. Shared by every Buffer the systems
  // own — lerp-based Contents reads from here. VirtualVessel.Tick
  // advances this; DeltaVSimulation drives a cloned vessel's clock
  // independently of the game UT.
  public SimClock Clock { get; }

  public StagingFlowSystem Staging { get; }
  public ProcessFlowSystem Process { get; }

  public VesselSystems() {
    Clock = new SimClock();
    Staging = new StagingFlowSystem(Clock);
    Process = new ProcessFlowSystem(Clock);
  }

  // Run all systems in order. Staging produces buffer rates and
  // demand satisfactions; Process never reads them (the two domains
  // are disjoint), so order is just for readability — Process could
  // run first too. Components call OnPreSolve before this; runner
  // does that since it owns the component list.
  public void Solve() {
    Staging.Solve();
    Process.Solve();
  }

  // Soonest forecasted state-change across all systems, as relative
  // dt from the current clock UT. Each system encapsulates its own
  // internal events (buffer empty/fill, device ValidUntils, etc.);
  // we just take the minimum.
  public double MaxTickDt() {
    var s = Staging.MaxTickDt();
    var p = Process.MaxTickDt();
    return s < p ? s : p;
  }

  // Advance simulation time by dt. Buffers lerp Contents forward
  // against the shared clock automatically; nothing system-internal
  // needs poking.
  public void AdvanceClock(double dt) {
    if (dt > 0) Clock.UT += dt;
  }

  // ── Unified device factory ───────────────────────────────────────
  // Construct a Device with all inputs/outputs declared up-front. The
  // resources' domain picks the underlying solver:
  //   Topological (RP-1, LOX, LH₂, …) → Staging.Consumer.
  //   Uniform     (ElectricCharge, …)  → Process.Device.
  // Validation throws on mixed domains, on Topological outputs, and
  // on devices with no inputs and no outputs.
  //
  // `node` is required (Staging-bound devices need it; Process-bound
  // devices treat it as harmless metadata). `priority` only matters
  // on the Process side.
  public Device AddDevice(
      StagingFlowSystem.Node node,
      (Resource resource, double rate)[] inputs = null,
      (Resource resource, double rate)[] outputs = null,
      ProcessFlowSystem.Priority priority = ProcessFlowSystem.Priority.Low) {

    inputs  = inputs  ?? Array.Empty<(Resource, double)>();
    outputs = outputs ?? Array.Empty<(Resource, double)>();

    if (inputs.Length == 0 && outputs.Length == 0)
      throw new ArgumentException(
          "Device must declare at least one input or output.");

    ResourceDomain? domain = null;
    foreach (var (r, _) in inputs)  domain = MergeDomain(domain, r);
    foreach (var (r, _) in outputs) domain = MergeDomain(domain, r);

    if (domain == ResourceDomain.Topological && outputs.Length > 0)
      throw new ArgumentException(
          "Topological devices cannot declare outputs — only tanks store " +
          "topological resources.");

    if (domain == ResourceDomain.Topological) {
      var c = Staging.RegisterConsumer(node);
      foreach (var (r, rate) in inputs) c.AddInput(r, rate);
      return new Device(c);
    } else {
      var d = Process.AddDevice(priority);
      foreach (var (r, rate) in inputs)  d.AddInput(r, rate);
      foreach (var (r, rate) in outputs) d.AddOutput(r, rate);
      return new Device(d);
    }
  }

  private static ResourceDomain? MergeDomain(ResourceDomain? acc, Resource r) {
    if (acc == null) return r.Domain;
    if (acc != r.Domain) throw new ArgumentException(
        $"Device cannot mix resource domains: {r.Name} is {r.Domain}, " +
        $"prior endpoints were {acc}. A Device's Activity is managed by " +
        $"exactly one solver, so all of its endpoints must share a domain.");
    return acc;
  }
}
