using Nova.Core.Samples;

namespace Nova.Core.Science;

// Mystery Goo — "expose a sample at a place, watch what happens." One
// observation per (body, situation, sample type), produced when an
// exposure in a goo chamber runs uninterrupted for the sample type's
// ExposureDurationSec. Files are complete on creation (no fidelity
// ramp); per-type subject ids let the goo flavors archive separately.
public static class MysteryGooExperiment {
  public const string ExperimentId  = "mystery-goo";
  public const long   FileSizeBytes = 2_000;

  public static SubjectKey SubjectFor(SampleType type, string bodyName, Situation situation) {
    // SubjectKey forbids ':' in variant (it's the slice-index separator
    // for time-sliced experiments). Combine situation + type id with
    // '.' as an in-variant separator.
    var variant = $"{situation}.{type.Id}";
    return new SubjectKey(ExperimentId, bodyName, variant);
  }
}
