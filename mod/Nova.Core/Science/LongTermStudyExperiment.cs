using System;

namespace Nova.Core.Science;

// "Sit and observe over time." Body-year is divided into 12 slices;
// each slice is its own subject so partial-fidelity files for slice 7
// don't merge with slice 8's data.
public static class LongTermStudyExperiment {
  public const string ExperimentId   = "lts";
  public const long   FileSizeBytes  = 5_000;
  public const int    SlicesPerYear  = 12;

  public static SubjectKey SubjectFor(
      string bodyName, Situation situation, double ut, double bodyYearSeconds) {
    int slice = SliceIndexAt(ut, bodyYearSeconds);
    return new SubjectKey(ExperimentId, bodyName, situation.ToString(), slice);
  }

  public static int SliceIndexAt(double ut, double bodyYearSeconds) {
    double sliceDuration = bodyYearSeconds / SlicesPerYear;
    double phase = ut - Math.Floor(ut / bodyYearSeconds) * bodyYearSeconds;
    int slice = (int)Math.Floor(phase / sliceDuration);
    if (slice < 0) slice += SlicesPerYear;
    if (slice >= SlicesPerYear) slice -= SlicesPerYear;
    return slice;
  }

  public static double NextSliceBoundary(double now, double bodyYearSeconds) {
    double sliceDuration = bodyYearSeconds / SlicesPerYear;
    double yearStart = Math.Floor(now / bodyYearSeconds) * bodyYearSeconds;
    double phase = now - yearStart;
    int slice = (int)Math.Floor(phase / sliceDuration);
    return yearStart + (slice + 1) * sliceDuration;
  }

  public static double SliceDurationFor(double bodyYearSeconds) =>
      bodyYearSeconds / SlicesPerYear;
}
