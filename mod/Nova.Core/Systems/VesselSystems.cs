using System.Collections.Generic;

namespace Nova.Core.Systems;

// Container for the per-vessel simulation systems. Owned by
// VirtualVessel; passed to each component at OnBuildSystems time so
// components can register their domain-specific state with the right
// system (Topological flow → Staging, Uniform flow → Process).
//
// Adding a system later (e.g. ControlSystem for hysteresis +
// reactive logic, ThermalSystem for heat) means a new field here and
// inclusion in `All`.
public class VesselSystems {
  public StagingFlowSystem Staging { get; }
  public ProcessFlowSystem Process { get; }

  // Iteration order matters for the runner: Staging produces buffer
  // rates and demand satisfactions that Process never reads (the two
  // domains are disjoint). Order chosen for readability.
  public IEnumerable<BackgroundSystem> All { get; }

  public VesselSystems() {
    Staging = new StagingFlowSystem();
    Process = new ProcessFlowSystem();
    All = new BackgroundSystem[] { Staging, Process };
  }
}
