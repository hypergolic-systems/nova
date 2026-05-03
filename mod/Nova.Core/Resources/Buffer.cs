using Nova.Core.Systems;

namespace Nova.Core.Resources;

// Resource buffer with lerp-based state. Instead of integrating
// Contents per physics tick (rate × dt every frame), Buffer stores
// (BaselineContents, BaselineUT, Rate) and computes Contents lazily
// when read:
//
//   Contents(t) = clamp(BaselineContents + Rate × (t - BaselineUT), 0, Capacity)
//
// "t" is read from the shared SimClock the owning system installed.
// Callers don't pass time explicitly — the lerp is invisible from
// the outside, `b.Contents` just always returns the right value.
//
// The win is asymptotic: simulation cost scales with the number of
// rate-change events, not with the number of physics ticks.
//
//   • At 1× warp: events arrive faster than ticks anyway, lerp is at
//     worst the same as integration plus a tiny per-read cost.
//   • At 1000× warp: integrating 50 Hz × 1000 = 50 000 ticks/sec of
//     buffer mutation is wasted work — between rate changes nothing
//     interesting happens. Lerp evaluates Contents only when the
//     solver re-bases (at MaxTickDt boundaries) or when a reader
//     asks (telemetry, save, UI).
//   • Background vessels share the same model — same SimClock cadence,
//     same advance, just no per-tick integration loop hammering buffers
//     between events.
//   • DeltaVSimulation runs on a cloned vessel with its own clock,
//     ticking forward at simulator pace independent of the game clock.
//   • The "over-drain at boundary" pathology fundamentally goes
//     away: clamp is applied at read time, so reading Contents past
//     the empty point returns 0 — you can't double-drain a buffer
//     that's already at the floor.
//
// Maintenance contract:
//   • Solver-driven owners (StagingFlowSystem / ProcessFlowSystem)
//     call `Refresh(ut)` before changing Rate — captures the lerped
//     Contents-at-now and resets baseline. The setter on Rate does
//     this automatically too, but explicit Refresh is clearer when
//     batching multiple buffers.
//   • Direct mutation of Contents (loading a tank, restoring a save)
//     goes through the Contents setter, which rebaselines.
//   • Tests that pre-Clock construct a Buffer get a null Clock —
//     the lerp falls back to BaselineUT (i.e., static value). They
//     can opt in to lerp by attaching to a SimClock.
public class Buffer {
  public Resource Resource;
  public double Capacity;
  public double MaxRateIn;
  public double MaxRateOut;

  // Baseline state. Direct field access for owners (e.g. snapshot /
  // restore code paths). Most callers should go through the property
  // surface below.
  public double BaselineContents;
  public double BaselineUT;

  // Shared clock — installed by the owning system at construction.
  // Tests can leave this null; ContentsAt then collapses to a
  // static-value lookup at BaselineUT.
  internal SimClock Clock;

  private double _rate;

  public double Rate {
    get => _rate;
    set {
      // Rebaseline before applying the new rate. The OLD rate was
      // valid from BaselineUT up to "now"; capture the resulting
      // Contents as the new baseline so the new rate applies forward
      // from now, not retroactively.
      var t = Clock?.UT ?? BaselineUT;
      BaselineContents = ContentsAt(t);
      BaselineUT = t;
      _rate = value;
    }
  }

  // Current Contents, lerped from baseline to the shared clock's UT
  // and clamped to [0, Capacity]. Setter rebaselines: new
  // BaselineContents = `value`, BaselineUT = clock UT, Rate is left
  // alone (caller mutates separately if needed).
  public double Contents {
    get => ContentsAt(Clock?.UT ?? BaselineUT);
    set {
      BaselineContents = value;
      BaselineUT = Clock?.UT ?? BaselineUT;
    }
  }

  // Contents at an arbitrary UT — useful when callers want to
  // forecast or read a different time than "now".
  public double ContentsAt(double ut) {
    var projected = BaselineContents + _rate * (ut - BaselineUT);
    if (projected < 0) return 0;
    if (projected > Capacity) return Capacity;
    return projected;
  }

  public void FlowLimits(double rateIn, double rateOut) {
    MaxRateIn = rateIn;
    MaxRateOut = rateOut;
  }

  // Capture Contents at the given UT as the new baseline. Rate
  // unchanged. Callers (solvers) typically follow with `Rate = …`
  // on the same buffer; the Rate setter would auto-Refresh as well,
  // so explicit Refresh is mostly for batching readability.
  public void Refresh(double ut) {
    BaselineContents = ContentsAt(ut);
    BaselineUT = ut;
  }
}
