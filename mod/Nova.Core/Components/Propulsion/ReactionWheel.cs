using System;
using Nova.Core.Persistence.Protos;
using Nova.Core.Resources;
using Nova.Core.Systems;

namespace Nova.Core.Components.Propulsion;

// Reaction wheel, buffer-pattern. Each wheel owns a small off-LP
// `Accumulator` (2.5 kJ for a 250 W wheel ≈ 10 s of single-axis-full).
// Player input drains it directly each tick; the LP only sees the
// *refill* device pulling EC at the rated wattage when hysteresis says
// so. The LP re-solves only on hysteresis flips, not on every per-frame
// intensity change.
//
// Scheduling mirrors `FuelCell`:
//   • OnPreSolve flips RefillActive based on current FillFraction.
//   • `refill.ValidUntil` forecasts the time-to-next-flip and is
//     refreshed at three points:
//       OnTickBegin  — intensity may have changed externally since
//                      the last solve (NovaVesselModule.SolveAttitude).
//       OnAdvance    — buffer state advances during clock integration.
//       OnPostSolve  — refill.Activity changes when the LP re-solves
//                      with a flipped Demand.
//     The Tick scheduler then advances simulationTime to the forecasted
//     event time and triggers a re-solve when it's reached.
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

  // Hysteresis matches FuelCell manifold: refill ON when buffer drains
  // to 10%, refill OFF when buffer fills to 100%.
  public const double RefillOnFraction  = 0.10;
  public const double RefillOffFraction = 1.00;

  // Persisted off-LP state.
  public Accumulator Buffer;     // null until OnBuildSolver / Load
  public bool RefillActive;

  // Live derived telemetry. Computed on read from Buffer state +
  // last-solved refill.Activity + current intensity, so reads always
  // reflect "right now" — no per-tick mutation. SolveAttitude reads
  // Satisfaction each frame to scale applied torque.
  //
  //   Satisfaction = 1 while the buffer has juice OR fill ≥ drain;
  //                  drops to fill/drain when the buffer empties and
  //                  the bus can't sustain demand on its own.
  //   CurrentDrain  = drain actually being supplied (= desiredDrain
  //                  while buffer non-empty; = fillRate when starved).
  //   CurrentRefill = fill rate from the EC bus (= refill.Activity ×
  //                  RefillRateWatts).
  public double Satisfaction {
    get {
      if (refill == null || Buffer == null) return 1.0;
      double intensity = Math.Abs(ThrottlePitch)
                       + Math.Abs(ThrottleRoll)
                       + Math.Abs(ThrottleYaw);
      if (intensity < 1e-9) return 1.0;
      double desiredDrain = intensity * ElectricRate;
      double fillRate = refill.Activity * RefillRateWatts;
      if (Buffer.Contents > 1e-9 || fillRate >= desiredDrain) return 1.0;
      return fillRate / desiredDrain;
    }
  }
  public double CurrentDrain {
    get {
      if (refill == null || Buffer == null) return 0;
      double intensity = Math.Abs(ThrottlePitch)
                       + Math.Abs(ThrottleRoll)
                       + Math.Abs(ThrottleYaw);
      if (intensity < 1e-9) return 0;
      double desiredDrain = intensity * ElectricRate;
      double fillRate = refill.Activity * RefillRateWatts;
      if (Buffer.Contents > 1e-9) return desiredDrain;
      return Math.Min(desiredDrain, fillRate);
    }
  }
  public double CurrentRefill => refill?.Activity * RefillRateWatts ?? 0;

  internal ProcessFlowSystem.Device refill;

  // Public read-only access to the refill device's ValidUntil — handy
  // for tests asserting forecast behaviour and for telemetry that wants
  // to surface "next refill flip" without taking an internal dep on the
  // device shape.
  public double RefillValidUntil => refill?.ValidUntil ?? double.PositiveInfinity;

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

    // Wire the buffer's clock + rebaseline to "now". From here on, the
    // lerp evaluates against the live SimClock; OnTickBegin/OnPostSolve
    // update Rate when intensity or refill.Activity changes.
    Buffer.Clock = systems.Clock;
    Buffer.BaselineUT = systems.Clock.UT;

    // Priority.Low — matches FuelCell.refill. Wheel refill is
    // opportunistic top-up of an off-system buffer; if the EC bus is
    // bound we'd rather throttle refill and keep avionics/lights
    // powered than the other way round. Buffer covers wheel work in
    // the meantime.
    refill = systems.Process.AddDevice(ProcessFlowSystem.Priority.Low);
    refill.AddInput(Resource.ElectricCharge, RefillRateWatts);
    refill.Demand = RefillActive ? 1.0 : 0.0;
  }

  public override void OnPreSolve() {
    if (refill == null) return;
    // Hysteresis flip: refill ON when buffer drains to ≤ 10%, OFF when
    // it fills to ≥ 100%. Mirrors FuelCell's manifold band. Both bounds
    // are inclusive so a forecast that lands the buffer exactly at the
    // threshold (TimeToFraction's intent) flips reliably.
    double frac = Buffer.FillFraction;
    if (RefillActive && frac >= RefillOffFraction) RefillActive = false;
    else if (!RefillActive && frac <= RefillOnFraction) RefillActive = true;
    refill.Demand = RefillActive ? 1.0 : 0.0;
  }

  public override void OnTickBegin() => RefreshBufferState();
  public override void OnPostSolve() => RefreshBufferState();

  // Push the current intensity + last-solve refill.Activity into
  // Buffer.Rate (lerp takes it forward) and refresh refill.ValidUntil
  // for the soonest hysteresis flip. Idempotent; called from both
  // OnTickBegin (intensity may have changed externally) and OnPostSolve
  // (refill.Activity changed). The Rate setter rebaselines at clock.UT,
  // capturing the previous lerp's Contents-at-now into the new
  // baseline.
  private void RefreshBufferState() {
    if (refill == null || Buffer == null) return;

    double intensity = Math.Abs(ThrottlePitch)
                     + Math.Abs(ThrottleRoll)
                     + Math.Abs(ThrottleYaw);
    double drain = intensity * ElectricRate;
    double fill  = refill.Activity * RefillRateWatts;
    double net   = fill - drain;

    Buffer.Rate = net;

    // Next flip is at the OPPOSITE threshold from the current state.
    double dt = RefillActive
      ? Buffer.TimeToFraction(RefillOffFraction, net)
      : Buffer.TimeToFraction(RefillOnFraction,  net);

    refill.ValidUntil = double.IsPositiveInfinity(dt)
      ? double.PositiveInfinity
      : Vessel.Systems.Clock.UT + dt;
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
