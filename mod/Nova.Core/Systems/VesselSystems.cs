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
}
