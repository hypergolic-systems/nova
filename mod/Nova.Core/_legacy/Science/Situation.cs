namespace Nova.Core.Science;

// Mirror of stock KSP's ExperimentSituations. Values match exactly so
// (Situation)((int)vessel.situation) is a no-op cast on the mod side.
// Stock retains this enum verbatim — KSP 1.x is dead, no upstream drift.
public enum Situation {
  None        = 0,
  SrfLanded   = 1,
  SrfSplashed = 2,
  FlyingLow   = 4,
  FlyingHigh  = 8,
  InSpaceLow  = 16,
  InSpaceHigh = 32,
}
