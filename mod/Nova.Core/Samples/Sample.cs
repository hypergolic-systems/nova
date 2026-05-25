namespace Nova.Core.Samples;

// State of one Sample over its lifetime. State changes are mass-
// neutral — Invalidated samples still take up a chamber slot and
// contribute mass; only a future eject/transfer would remove one.
public enum SampleCondition {
  Pristine    = 0,   // never exposed; eligible for the next cover-open
  Exposed     = 1,   // exposure ran to completion; produced ScienceFile
  Invalidated = 2,   // chamber was cycled mid-exposure; useless
}

// One discrete sample held by a sample-carrying component. Identity
// is positional (index in the owner's list). Mass is read from Type;
// container state lookups (next-pristine cursor, mass total) iterate.
public sealed class Sample {
  public SampleType      Type;
  public SampleCondition Condition;
  public double          ExposedAtUt;        // 0 when Pristine
  public string          ExposedSubjectId;   // empty until Exposed

  public Sample(SampleType type) {
    Type = type;
    Condition = SampleCondition.Pristine;
    ExposedSubjectId = "";
  }
}
