using Nova.Core.Persistence.Protos;
using Nova.Core.Resources;
using Nova.Core.Systems;

namespace Nova.Core.Components.Structural;

// Landing-leg deployment-and-power component. Owns:
//   • Editor-tunable shape: RequiresStaging (gear key is gated until
//     the part has been staged) and StartsDeployed (whether the leg
//     spawns extended on a fresh launch).
//   • Runtime state: Activated (the staging gate), Position
//     (0=stowed, 1=extended), TargetPosition (the direction the leg
//     is moving toward, or equal to Position at rest).
//   • Off-LP EC consumer: while moving, a single-input High-priority
//     PFS Device demanding MotorPowerW. Stationary legs demand 0.
//
// EC-scaled motion: when only a fraction α of MotorPowerW is granted,
// the KSP-side adapter steps Position by α × FullSpeedPerSecond × dt
// each FixedUpdate — so α=0 freezes the leg in place until the bus
// recovers, and α=0.5 deploys at half speed. The component holds the
// numeric state; the adapter integrates it (Position has to march in
// lockstep with the animation clip's normalizedTime, which lives on
// the Unity model, so integration is module-side).
//
// No Accumulator/buffer. Deploy power is consumed live; there's no
// reservoir to recharge between motions. A starved bus = a frozen
// leg, immediately. Matches the user-facing spec.
public class LandingLeg : VirtualComponent {

  // Config (from prefab MODULE).
  public double MotorPowerW;
  public double DeploySeconds;

  // Editor-tunable, persisted.
  public bool RequiresStaging;
  public bool StartsDeployed;

  // Runtime state, persisted.
  public bool   Activated;
  public double Position;
  public double TargetPosition;

  // True iff Load() consumed a LandingLegState. The KSP-side wrapper
  // uses this to choose between "snap to persisted pose" (matched
  // save) and "apply cfg/StartsDeployed default" (fresh spawn).
  public bool LoadedFromSave;

  internal Device device;

  public bool   IsMoving => Position != TargetPosition;
  public double Satisfaction => device?.Satisfaction ?? 0;
  public double Activity => device?.Activity ?? 0;
  public double CurrentEcW => IsMoving ? Activity * MotorPowerW : 0;
  public double MaxEcW => MotorPowerW;
  public double FullSpeedPerSecond => DeploySeconds > 0 ? 1.0 / DeploySeconds : 0;

  public override VirtualComponent Clone() {
    return (LandingLeg)MemberwiseClone();
  }

  public override void OnBuildSystems(VesselSystems systems, StagingFlowSystem.Node node) {
    if (MotorPowerW <= 0) return;
    device = systems.AddDevice(node,
        inputs: new[] { (Resource.ElectricCharge, MotorPowerW) },
        priority: ProcessFlowSystem.Priority.High);
    device.Demand = IsMoving ? 1.0 : 0.0;
  }

  public override void OnPreSolve() {
    if (device == null) return;
    device.Demand = IsMoving ? 1.0 : 0.0;
  }

  public override void Save(PartState state) {
    state.LandingLeg = new LandingLegState {
      RequiresStaging = RequiresStaging,
      StartsDeployed  = StartsDeployed,
      Activated       = Activated,
      Position        = Position,
      TargetPosition  = TargetPosition,
    };
  }

  public override void Load(PartState state) {
    if (state.LandingLeg == null) return;
    RequiresStaging = state.LandingLeg.RequiresStaging;
    StartsDeployed  = state.LandingLeg.StartsDeployed;
    Activated       = state.LandingLeg.Activated;
    Position        = state.LandingLeg.Position;
    TargetPosition  = state.LandingLeg.TargetPosition;
    LoadedFromSave  = true;
  }
}
