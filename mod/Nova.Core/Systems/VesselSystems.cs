namespace Nova.Core.Systems;

// Container for the per-vessel simulation systems. Owned by
// VirtualVessel; passed to each component at OnBuildSystems time so
// components can register their domain-specific state with the right
// system (Topological flow → Staging, Uniform flow → Process).
//
// Adding a system later (e.g. ControlSystem for hysteresis +
// reactive logic, ThermalSystem for heat) means a new field here.
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
}
