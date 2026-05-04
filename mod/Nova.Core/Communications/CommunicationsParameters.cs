namespace Nova.Core.Communications;

// System-wide tunables for the comms subsystem.
public static class CommunicationsParameters {

  // Noise floor used as the SNR denominator. The absolute value is
  // gameplay-irrelevant — only the ratio between SNR(A→B) and
  // SNR_ref(A) drives the Shannon rate scaling. Antenna specs in cfg
  // are calibrated against NoiseFloor = 1.
  public const double NoiseFloor = 1.0;

  // Number of equal-width buckets the Shannon factor in [0, 1] is
  // discretised into. Each link's reported rate snaps to the bucket
  // floor, so allocator and routing decisions stay constant inside a
  // bucket. Higher = more events per knee descent; lower = coarser
  // routing.
  public const int BucketCount = 10;

  // Cap on the per-link bucket-crossing horizon search, in seconds. If
  // the coarse sweep finds no crossing within this window, NextEventUT
  // returns currentUT + MaxHorizonSeconds — re-checked one horizon out.
  // Two-craft relative geometry isn't bounded by either's orbital
  // period (resonant pairs, hyperbolic flybys, …), so this is a flat
  // configurable, not a derived quantity.
  public const double MaxHorizonSeconds = 86400;

  // Coarse-sweep step count across MaxHorizonSeconds. ~200 mirrors
  // ShadowCalculator. Bisection refines from there.
  public const int HorizonSearchSteps = 200;

  // Number of distance samples used by the cheap pre-screen that
  // skips full bucket-bisection for pairs always out of range. Coarser
  // than HorizonSearchSteps deliberately — pre-screen only needs to
  // detect "this pair stays beyond the bucket-1 threshold across the
  // window." Brief encounters shorter than horizon/PrescreenSamples
  // can be missed, but on KSP timescales (1 day default horizon, ~4300s
  // sample spacing) that's a tolerable approximation.
  public const int PrescreenSamples = 20;
}
