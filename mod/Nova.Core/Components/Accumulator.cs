using System;

namespace Nova.Core.Components;

// Off-LP storage cell. Used by buffered consumers/producers to decouple
// component-internal flow rates from the LP cadence:
//
//   • FuelCell  — LH₂ + LOx manifolds (µL/s reactant draw kept off-LP).
//   • ReactionWheel — energy reserve (per-frame intensity changes kept
//                     off-LP so SAS jiggling doesn't re-solve the LP).
//
// "Accumulator" is the mechanical-engineering term for an off-board
// reservoir that absorbs short-term flow imbalances (hydraulic,
// pneumatic, electrical). Distinct from `Resource.Buffer`, which is
// the LP-visible storage primitive that participates in conservation
// rows.
//
// See `docs/lp_hygiene.md` §1 for the design rationale. This class is
// pure storage + integration math: capacity, contents, integrate, and
// time-to-fraction forecasts. Hysteresis flags live on the owning
// component (different components have different coupling — FuelCell
// shares one bool across two coupled manifolds, ReactionWheel has one
// accumulator with one bool — not worth modelling generically here).
public class Accumulator {
  public double Capacity;
  public double Contents;

  public double FillFraction => Capacity > 1e-9 ? Contents / Capacity : 1.0;
  public bool IsEmpty => Contents <= 1e-9;
  public bool IsFull  => Contents >= Capacity - 1e-9;

  // Integrate signed netRate over deltaT, clamping to [0, Capacity].
  // Positive rate fills, negative drains.
  public void Integrate(double netRate, double deltaT) {
    Contents = Math.Max(0, Math.Min(Capacity, Contents + netRate * deltaT));
  }

  // Time for Contents to reach `targetFrac × Capacity` given a signed
  // netRate. +∞ when the rate doesn't move toward the target — the
  // caller usually pairs that with PositiveInfinity ValidUntil.
  //
  // Edge case worth its own line: when Contents is already AT the
  // target and netRate keeps pushing past, we return 0 (not +∞).
  // This happens whenever Buffer.Integrate clamps at capacity (or
  // floor) — the clamp absorbs the over-fill silently, so without
  // the 0-return the runner sees no upcoming event, never re-solves,
  // and the hysteresis flip in OnPreSolve never fires. The 0 forecast
  // forces an immediate re-solve so OnPreSolve gets to do its job.
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
