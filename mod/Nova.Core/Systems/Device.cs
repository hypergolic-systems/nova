using Nova.Core.Resources;

namespace Nova.Core.Systems;

// Unified resource-flow declaration. Components see one API
// regardless of whether the resources flow through Staging
// (Topological — water-fill on vessel topology) or Process (Uniform —
// LP on a single vessel-wide pool). The whole Device routes to one
// solver — the domain of its inputs/outputs picks which.
//
// A Device's `Activity` is managed by exactly one solver, so all of
// its inputs and outputs must share a single resource domain. Mixed-
// domain flow (e.g. an ISRU consuming water and producing O₂/H₂
// across the two systems) is modelled with two devices coupled via
// an Accumulator — the same way FuelCell already bridges Staging
// refill and Process EC production today.
//
// Inputs/outputs are immutable after construction. They're declared
// up-front at `VesselSystems.AddDevice(...)` time as arrays; same-
// domain validation runs there.
public class Device {
  // Internally backed by exactly one of these — picked at
  // construction based on the inputs/outputs' domain.
  internal StagingFlowSystem.Consumer staging;
  internal ProcessFlowSystem.Device   process;

  internal Device(StagingFlowSystem.Consumer staging) {
    this.staging = staging;
  }

  internal Device(ProcessFlowSystem.Device process) {
    this.process = process;
  }

  // Which solver owns this device — derived from the inputs' resource
  // domain at AddDevice time.
  public ResourceDomain Domain =>
    staging != null ? ResourceDomain.Topological : ResourceDomain.Uniform;

  // 0..1, what fraction of the declared full rate this device wants
  // this tick. Set pre-Solve.
  public double Demand {
    get => staging != null ? staging.Throttle : process.Demand;
    set {
      if (staging != null) staging.Throttle = value;
      else process.Demand = value;
    }
  }

  // 0..1, achieved fraction post-Solve. Read after Solve.
  public double Activity =>
    staging != null ? staging.Activity : process.Activity;

  // Activity / Demand, with a small zero-guard. 1.0 = fully supplied;
  // 0 = fully starved (or no demand requested).
  public double Satisfaction =>
    Demand > 1e-9 ? Activity / Demand : 0;

  // Absolute UT of the next forecasted state-change event. +∞ when
  // none. Honoured by Process (read by ProcessFlowSystem.MaxTickDt
  // to schedule Tick events); staging-bound devices don't carry
  // per-device forecasts so the setter is a no-op there.
  public double ValidUntil {
    get => process != null ? process.ValidUntil : double.PositiveInfinity;
    set {
      if (process != null) process.ValidUntil = value;
    }
  }
}
