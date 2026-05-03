using System;
using Nova.Core.Systems;

namespace Nova.Core.Components;

// Off-LP storage cell with lerp-based state. Mirrors `Resource.Buffer`
// but for component-internal accumulators that don't participate in
// solver conservation rows:
//
//   • FuelCell  — LH₂ + LOx manifold (µL/s reactant draw kept off-LP).
//   • ReactionWheel — energy reserve (per-frame intensity changes kept
//                     off-LP so SAS jiggling doesn't re-solve the LP).
//
// Same lerp model as Buffer: store (BaselineContents, BaselineUT, Rate)
// and compute Contents on read.
//
//   Contents(t) = clamp(BaselineContents + Rate × (t - BaselineUT), 0, Capacity)
//
// The owning component (FuelCell, ReactionWheel) installs `Clock` at
// `OnBuildSystems` time and updates `Rate` whenever the underlying flow
// changes (post-solve, on intensity-change). No per-tick integration.
//
// "Accumulator" is the mechanical-engineering term for an off-board
// reservoir that absorbs short-term flow imbalances (hydraulic,
// pneumatic, electrical). Distinct from `Resource.Buffer`, which is
// the LP-visible storage primitive that participates in conservation
// rows.
//
// See `mod/Nova.Core/Resources/Buffer.cs` for the full design rationale.
public class Accumulator {
  public double Capacity;

  // Baseline state. Direct field access for owners (e.g. snapshot /
  // restore code paths). Most callers should go through the property
  // surface below.
  public double BaselineContents;
  public double BaselineUT;

  // Shared clock — installed by the owning component at OnBuildSystems
  // time. Tests can leave this null; ContentsAt then collapses to a
  // static-value lookup at BaselineUT.
  internal SimClock Clock;

  private double _rate;

  public double Rate {
    get => _rate;
    set {
      // Rebaseline before applying the new rate. The OLD rate was valid
      // from BaselineUT up to "now"; capture the resulting Contents as
      // the new baseline so the new rate applies forward from now.
      var t = Clock?.UT ?? BaselineUT;
      BaselineContents = ContentsAt(t);
      BaselineUT = t;
      _rate = value;
    }
  }

  // Current Contents, lerped to the shared clock's UT and clamped to
  // [0, Capacity]. Setter rebaselines.
  public double Contents {
    get => ContentsAt(Clock?.UT ?? BaselineUT);
    set {
      BaselineContents = value;
      BaselineUT = Clock?.UT ?? BaselineUT;
    }
  }

  public double ContentsAt(double ut) {
    var projected = BaselineContents + _rate * (ut - BaselineUT);
    if (projected < 0) return 0;
    if (projected > Capacity) return Capacity;
    return projected;
  }

  // Capture Contents at the given UT as the new baseline. Rate
  // unchanged. Owners typically follow with `Rate = …` on the same
  // accumulator; the Rate setter would auto-rebaseline as well, so
  // explicit Refresh is mostly for batching readability.
  public void Refresh(double ut) {
    BaselineContents = ContentsAt(ut);
    BaselineUT = ut;
  }

  public double FillFraction => Capacity > 1e-9 ? Contents / Capacity : 1.0;
  public bool IsEmpty => Contents <= 1e-9;
  public bool IsFull  => Contents >= Capacity - 1e-9;

  // Time for Contents to reach `targetFrac × Capacity` given a signed
  // netRate. +∞ when the rate doesn't move toward the target — the
  // caller usually pairs that with PositiveInfinity ValidUntil.
  //
  // Edge case worth its own line: when Contents is already AT the
  // target and netRate keeps pushing past, we return 0 (not +∞). The
  // 0 forecast forces an immediate re-solve so OnPreSolve gets to
  // flip the hysteresis flag — without it, a buffer pinned at a
  // threshold by a clamp would stay there forever.
  public double TimeToFraction(double targetFrac, double netRate) {
    if (Capacity <= 0) return double.PositiveInfinity;
    double target = targetFrac * Capacity;
    double slack = target - Contents;
    if (slack > 0 && netRate > 1e-12) return slack / netRate;
    if (slack < 0 && netRate < -1e-12) return slack / netRate;
    if (Math.Abs(slack) < 1e-12 && Math.Abs(netRate) > 1e-12) return 0;
    return double.PositiveInfinity;
  }
}
