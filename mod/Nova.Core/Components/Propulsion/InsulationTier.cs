using System;

namespace Nova.Core.Components.Propulsion;

// Per-slice cryogenic insulation/cooling tier. Int values match the
// generated `Nova.Core.Persistence.Protos.InsulationTier` exactly so
// the persistence boundary is a direct cast in both directions.
//
//   MLI       — Multi-Layer Insulation. Baseline; parts are sized for
//               MLI so no volume penalty. Passive only.
//   HeavyMLI  — Thicker MLI blanket. Smaller passive loss, surface-area
//               volume penalty.
//   BAC       — Broad Area Cooling. HeavyMLI passive insulation plus a
//               cryocooler stage drawing EC and emitting waste Heat.
//   ZBO       — Zero Boil-Off. BAC plus a deeper cold-finger stage.
//               Additional volume + EC + heat cost; fully closed.
public enum InsulationTier {
  MLI       = 0,
  HeavyMLI  = 1,
  BAC       = 2,
  ZBO       = 3,
}

// Tier physical parameters. PassiveFraction / ActiveFraction multiply
// the resource's MliBoiloffFractionPerDay. PassiveFraction applies
// when the cooler is off (or always for passive tiers); ActiveFraction
// applies at full cooler Activity. The runtime net fraction lerps
// between them by Activity, giving graceful degradation when the LP
// cuts cooler Activity for EC starvation or heat-bus saturation.
//
// EC draw and heat output are NOT direct knobs here — they're derived
// from a simple physical model in TankVolume, so a cooler on LH₂
// (20 K) pays much more per watt of cooling than the same tier on
// LOX (90 K). CarnotEfficiency is the only cooler-quality knob:
// it's the fraction of the Carnot COP the cooler actually achieves
// (real space-rated cryocoolers sit in 5-30% of Carnot depending on
// stage count and target temperature).
//
//   Q_baseline(slice) = capacity × baseline_frac/day × density × Lv / 86400
//   Q_remove          = Q_baseline × (passive − active)
//   COP_real          = CarnotEfficiency × T_cold / (AmbientK − T_cold)
//   maxEcW            = Q_remove / COP_real
//   maxHeatW          = maxEcW × (1 + COP_real)         (= ec + Q_remove)
//
// VolumePenalty is the fraction of slice capacity converted to hardware
// overhead. Build-time invariant: Σ capacity × (1 + VolumePenalty) ≤
// TankVolume.Volume.
public readonly struct InsulationTierData {
  public readonly double PassiveFraction;
  public readonly double ActiveFraction;
  // Fraction of Carnot COP the cooler actually achieves.
  // BAC = single-stage Stirling/pulse-tube class (~20% of Carnot).
  // ZBO = two-stage with cold-finger (~10% — the second stage is
  //       deep + inherently less efficient).
  public readonly double CarnotEfficiency;
  public readonly double VolumePenalty;
  public readonly bool   IsActive;

  public InsulationTierData(double passive, double active, double carnotEfficiency,
                            double volumePenalty, bool isActive) {
    PassiveFraction = passive;
    ActiveFraction = active;
    CarnotEfficiency = carnotEfficiency;
    VolumePenalty = volumePenalty;
    IsActive = isActive;
  }
}

public static class InsulationTierTable {
  // Hot-side temperature the cooler rejects against. 280 K is the
  // standard equilibrium for a moderately sun-lit, moderately
  // shadowed orbital surface near 1 AU (LEO / GTO range). For MVP
  // it's a flat constant; future work could read from
  // IVesselContext (solar distance / atm temperature) so coolers
  // run cheaper near Pluto and more expensive near Eve.
  public const double AmbientK = 280.0;

  // Tier table. Numbers are anchored to:
  //   - HeavyMLI passive at ~10% of MLI baseline (40+ layers vs 10).
  //   - BAC active ≈ 1% of baseline at full cooling — net ~0.01-0.05%/day
  //     depending on resource baseline.
  //   - ZBO active = 0 — fully closed when LP-supplied.
  //   - BAC single-stage cryocooler ≈ 20% of Carnot; ZBO two-stage ≈ 10%
  //     (the second stage reaches the deeper cold tap but the first stage
  //     is loaded twice, so end-to-end efficiency drops).
  //   - Surface penalty 5% covers thicker blanket + cooling sleeve
  //     plumbing; ZBO cold-finger adds another 5%.
  public static readonly InsulationTierData MLI =
      new(passive: 1.00, active: 1.00, carnotEfficiency: 0.0,  volumePenalty: 0.00, isActive: false);
  public static readonly InsulationTierData HeavyMLI =
      new(passive: 0.10, active: 0.10, carnotEfficiency: 0.0,  volumePenalty: 0.05, isActive: false);
  public static readonly InsulationTierData BAC =
      new(passive: 0.10, active: 0.01, carnotEfficiency: 0.20, volumePenalty: 0.05, isActive: true);
  public static readonly InsulationTierData ZBO =
      new(passive: 0.10, active: 0.00, carnotEfficiency: 0.10, volumePenalty: 0.10, isActive: true);

  public static InsulationTierData Get(InsulationTier tier) => tier switch {
    InsulationTier.MLI      => MLI,
    InsulationTier.HeavyMLI => HeavyMLI,
    InsulationTier.BAC      => BAC,
    InsulationTier.ZBO      => ZBO,
    _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, "Unknown insulation tier."),
  };

  public static double VolumePenalty(InsulationTier tier) => Get(tier).VolumePenalty;

  // Maximum runtime cooler stage the tier supports.
  //   MLI / HeavyMLI: 0 — passive only.
  //   BAC:            1 — single-stage cryocooler, on/off.
  //   ZBO:            2 — stage 1 runs BAC-equivalent, stage 2 runs full ZBO.
  public static int MaxStage(InsulationTier tier) => tier switch {
    InsulationTier.BAC => 1,
    InsulationTier.ZBO => 2,
    _ => 0,
  };

  // Cryocooler profile in effect for (tier, stage). Returns the BAC
  // profile for the "1st-stage" ZBO mode so the in-game tradeoff lines
  // up with the in-fiction picture: ZBO's first cooling stage IS a BAC
  // — cheaper EC, more residual boiloff than full ZBO.
  //   (BAC, 1)        → BAC
  //   (ZBO, 1)        → BAC
  //   (ZBO, 2)        → ZBO
  //   anything else   → null (no cooler active)
  public static InsulationTierData? ActiveProfile(InsulationTier tier, int stage) {
    if (stage <= 0) return null;
    if (stage > MaxStage(tier)) return null;
    if (tier == InsulationTier.BAC) return BAC;
    if (tier == InsulationTier.ZBO) return stage == 1 ? BAC : ZBO;
    return null;
  }
}
