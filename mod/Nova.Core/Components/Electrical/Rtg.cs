using System;
using Nova.Core.Persistence.Protos;
using Nova.Core.Resources;
using Nova.Core.Systems;

namespace Nova.Core.Components.Electrical;

// Pu-238 radioisotope thermoelectric generator. Output decays
// exponentially with the isotope half-life, but quantized to discrete
// steps so the LP sees a piecewise-constant supply. The step duration
// is derived from a fixed fractional drop per step:
//
//   stepDur = halfLife × log₂(1 / (1 − stepDrop))
//
// At StepDropFraction = 0.001 and Pu-238's 87.7-year half-life, that's
// ~46.2 days per step. Demand is set each PreSolve to the current
// decay factor, so the device's effective supply is
// ReferencePower × (1 − stepDrop)^stepIndex.
public class Rtg : VirtualComponent {
  private const double StepDropFraction = 0.001;
  private const double SecondsPerDay = 86400.0;

  // Config (from prefab MODULE — populated by ComponentFactory.CreateRtg).
  public double ReferencePower;   // W (EC/s) at t = ReferenceUT
  public double HalfLifeDays;     // T½ in Earth days

  // Persisted state. Sentinel 0 means "not yet anchored" — first
  // OnBuildSystems in flight pins it to current UT.
  public double ReferenceUT;

  internal Device device;

  public double StepDurationSeconds =>
    HalfLifeDays * SecondsPerDay
      * Math.Log(1.0 / (1.0 - StepDropFraction)) / Math.Log(2.0);

  public int StepIndex(double ut) =>
    Math.Max(0, (int)Math.Floor((ut - ReferenceUT) / StepDurationSeconds));

  public double DecayFactor(double ut) =>
    Math.Pow(1.0 - StepDropFraction, StepIndex(ut));

  public double CurrentPower =>
    Vessel == null ? ReferencePower
                   : ReferencePower * DecayFactor(Vessel.Systems.Clock.UT);

  public double CurrentRate =>
    device == null ? 0.0 : device.Activity * ReferencePower;

  public override VirtualComponent Clone() {
    return (Rtg)MemberwiseClone();
  }

  public override void OnBuildSystems(VesselSystems systems, StagingFlowSystem.Node node) {
    double now = Vessel.Systems.Clock.UT;
    if (ReferenceUT == 0)
      ReferenceUT = now;

    device = systems.AddDevice(node,
        outputs: new[] { (Resource.ElectricCharge, ReferencePower) });
    device.Demand = DecayFactor(now);
  }

  public override void OnPreSolve() {
    if (device == null) return;
    device.Demand = DecayFactor(Vessel.Systems.Clock.UT);
  }

  public override void OnPostSolve() {
    if (device == null) return;
    double now = Vessel.Systems.Clock.UT;
    double nextBoundary =
      ReferenceUT + (StepIndex(now) + 1) * StepDurationSeconds;
    device.ValidUntil = nextBoundary;
    ValidUntil = nextBoundary;
  }

  public override void Save(PartState state) {
    state.Rtg = new RtgState { ReferenceUt = ReferenceUT };
  }

  public override void Load(PartState state) {
    if (state.Rtg == null) return;
    ReferenceUT = state.Rtg.ReferenceUt;
  }
}
