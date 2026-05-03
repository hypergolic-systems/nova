using System;
using Nova.Core.Persistence.Protos;
using Nova.Core.Resources;
using Nova.Core.Systems;

namespace Nova.Core.Components.Propulsion;

// Reaction wheel, buffer-pattern. Each wheel owns a small off-LP
// `Accumulator` (2.5 kJ for a 250 W wheel ≈ 10 s of single-axis-full).
// The Accumulator owns the refill side (Process Device pulling EC at
// the rated wattage when hysteresis says so) and the lerp; the LP only
// re-solves on hysteresis flips, not on every per-frame intensity
// change.
//
// The wheel updates `Buffer.TapRate = intensity × ElectricRate` at:
//   • OnTickBegin — intensity may have changed externally between
//                   ticks (NovaVesselModule.SolveAttitude).
//   • OnPostSolve — refill.Activity changed; the Accumulator's net
//                   Rate (refill·activity − tap) needs the latest
//                   intensity captured too (it's stable within a Tick
//                   so this is mostly defensive).
//
// See `docs/lp_hygiene.md` §1 for the buffer-pattern rationale.
public class ReactionWheel : VirtualComponent {
  // Config (from prefab MODULE).
  public double PitchTorque;   // kN·m
  public double YawTorque;
  public double RollTorque;
  public double ElectricRate;  // W per unit intensity (single-axis-full)

  // Set externally each frame by NovaVesselModule.SolveAttitude. Range
  // [-1, 1] per axis; intensity = sum of |throttle| ∈ [0, 3].
  public double ThrottlePitch;
  public double ThrottleYaw;
  public double ThrottleRoll;

  // Buffer sizing — derived from ElectricRate. Lives on the component
  // (per project policy: ComponentFactory is a thin parser, no formulas
  // there). Gameplay tuning happens here, not in cfg or factory.
  //   Capacity   = 10 s × single-axis-full draw → absorbs short
  //                bursts; sustained max-axis (3×ER) drains it.
  //   RefillRate = 1× ER, "burst-only" model. Sustained intensity > 1
  //                eats into the buffer; sustained < 1 refills it.
  public double BufferCapacityJoules => ElectricRate * 10;
  public double RefillRateWatts      => ElectricRate;

  // Persisted off-LP state.
  public Accumulator Buffer;     // null until OnBuildSystems / Load
  public bool RefillActive;      // mirrored to/from Buffer.RefillActive

  // Live derived telemetry. Computed on read from Buffer state +
  // last-solved refill activity + current intensity, so reads always
  // reflect "right now" — no per-tick mutation. SolveAttitude reads
  // Satisfaction each frame to scale applied torque.
  //
  //   Satisfaction = 1 while the buffer has juice OR fill ≥ drain;
  //                  drops to fill/drain when the buffer empties and
  //                  the bus can't sustain demand on its own.
  //   CurrentDrain  = drain actually being supplied (= desiredDrain
  //                  while buffer non-empty; = fillRate when starved).
  //   CurrentRefill = fill rate from the EC bus (= refill activity ×
  //                  RefillRateWatts).
  public double Satisfaction {
    get {
      if (Buffer == null) return 1.0;
      double intensity = Math.Abs(ThrottlePitch)
                       + Math.Abs(ThrottleRoll)
                       + Math.Abs(ThrottleYaw);
      if (intensity < 1e-9) return 1.0;
      double desiredDrain = intensity * ElectricRate;
      double fillRate = Buffer.RefillActivity * RefillRateWatts;
      if (Buffer.Contents > 1e-9 || fillRate >= desiredDrain) return 1.0;
      return fillRate / desiredDrain;
    }
  }
  public double CurrentDrain {
    get {
      if (Buffer == null) return 0;
      double intensity = Math.Abs(ThrottlePitch)
                       + Math.Abs(ThrottleRoll)
                       + Math.Abs(ThrottleYaw);
      if (intensity < 1e-9) return 0;
      double desiredDrain = intensity * ElectricRate;
      double fillRate = Buffer.RefillActivity * RefillRateWatts;
      if (Buffer.Contents > 1e-9) return desiredDrain;
      return Math.Min(desiredDrain, fillRate);
    }
  }
  public double CurrentRefill => (Buffer?.RefillActivity ?? 0) * RefillRateWatts;

  // Public read-only access to the Accumulator's flip forecast. Handy
  // for tests asserting forecast behaviour and for telemetry.
  public double RefillValidUntil => Buffer?.ValidUntil ?? double.PositiveInfinity;

  public override VirtualComponent Clone() {
    return new ReactionWheel {
      PitchTorque = PitchTorque,
      YawTorque = YawTorque,
      RollTorque = RollTorque,
      ElectricRate = ElectricRate,
      ThrottlePitch = ThrottlePitch,
      ThrottleYaw = ThrottleYaw,
      ThrottleRoll = ThrottleRoll,
      RefillActive = RefillActive,
      Buffer = Buffer == null ? null : new Accumulator {
        Capacity = Buffer.Capacity, Contents = Buffer.Contents,
      },
    };
  }

  public override void OnBuildSystems(VesselSystems systems, StagingFlowSystem.Node node) {
    Buffer ??= new Accumulator {
      Capacity = BufferCapacityJoules,
      Contents = BufferCapacityJoules,  // fresh spawn primes to full
    };
    // Resync capacity in case the cfg's electricRate changed since the
    // save — Contents is preserved (clipped down to new capacity).
    Buffer.Capacity = BufferCapacityJoules;
    if (Buffer.Contents > Buffer.Capacity) Buffer.Contents = Buffer.Capacity;

    // Push the persisted RefillActive into the Accumulator runtime
    // state. ConfigureProcessRefill reads it to set initial Demand.
    Buffer.RefillActive = RefillActive;
    Buffer.ConfigureProcessRefill(systems, Resource.ElectricCharge,
        RefillRateWatts, ProcessFlowSystem.Priority.Low);
  }

  public override void OnPreSolve() {
    if (Buffer == null) return;
    Buffer.OnPreSolve();
    // Hysteresis flip happened inside Buffer.OnPreSolve — sync the
    // persisted handle so external reads + Save see runtime state.
    RefillActive = Buffer.RefillActive;
  }

  public override void OnTickBegin() {
    PushTapRate();
    if (Buffer != null) ValidUntil = Buffer.ValidUntil;
  }

  public override void OnPostSolve() {
    PushTapRate();
    Buffer.OnPostSolve();
    ValidUntil = Buffer.ValidUntil;
  }

  // intensity × ElectricRate = continuous drain rate. Setter on
  // Accumulator.TapRate rebaselines at "now" and recomputes net Rate
  // (refill·activity − tap). Net rate stays correct even if intensity
  // changes between Ticks (OnTickBegin captures the new value before
  // ComputeNextExpiry consumes the forecast).
  private void PushTapRate() {
    if (Buffer == null) return;
    double intensity = Math.Abs(ThrottlePitch)
                     + Math.Abs(ThrottleRoll)
                     + Math.Abs(ThrottleYaw);
    Buffer.TapRate = intensity * ElectricRate;
  }

  public override void Save(PartState state) {
    state.ReactionWheel = new ReactionWheelState {
      RefillActive = RefillActive,
      Buffer = new AccumulatorState { Contents = Buffer?.Contents ?? 0 },
    };
  }

  public override void Load(PartState state) {
    if (state.ReactionWheel == null) return;
    RefillActive = state.ReactionWheel.RefillActive;
    // Capacity is derived from ElectricRate (cfg). Contents from save.
    Buffer = new Accumulator {
      Capacity = BufferCapacityJoules,
      Contents = state.ReactionWheel.Buffer?.Contents ?? BufferCapacityJoules,
    };
  }
}
