using System;

namespace Nova.Core.Science;

// "Sit and observe over time." Applicable in any situation. Subject =
// (body, situation, sliceIndex). The body's solar year is divided into
// 12 equal slices; each slice is its own subject so partial-fidelity
// files for slice 7 don't merge with slice 8's data.
public class LongTermStudyExperiment : ExperimentDefinition {
  public const string ExperimentId = "lts";
  public const int    SlicesPerYear = 12;

  public override string Id => ExperimentId;

  public override bool IsApplicable(SubjectContext ctx) =>
      ctx.Situation != Situation.None && ctx.BodyYearSeconds > 0;

  public override SubjectKey? ResolveSubject(SubjectContext ctx) {
    if (!IsApplicable(ctx)) return null;
    int slice = SliceIndexAt(ctx.UT, ctx.BodyYearSeconds);
    return new SubjectKey(ExperimentId, ctx.BodyName, ctx.Situation.ToString(), slice);
  }

  // 0..SlicesPerYear-1. Wraps every body-year. Negative UT is well-defined
  // via the modulo: slice index for ut=−1 on a 12 Ms year is 11.
  public static int SliceIndexAt(double ut, double bodyYearSeconds) {
    double sliceDuration = bodyYearSeconds / SlicesPerYear;
    double phase = ut - Math.Floor(ut / bodyYearSeconds) * bodyYearSeconds;
    int slice = (int)Math.Floor(phase / sliceDuration);
    if (slice < 0) slice += SlicesPerYear;
    if (slice >= SlicesPerYear) slice -= SlicesPerYear;
    return slice;
  }

  // UT of the next slice boundary strictly greater than `now`. Used by the
  // Thermometer (M3) to set its component ValidUntil.
  public static double NextSliceBoundary(double now, double bodyYearSeconds) {
    double sliceDuration = bodyYearSeconds / SlicesPerYear;
    double yearStart = Math.Floor(now / bodyYearSeconds) * bodyYearSeconds;
    double phase = now - yearStart;
    int slice = (int)Math.Floor(phase / sliceDuration);
    return yearStart + (slice + 1) * sliceDuration;
  }
}
