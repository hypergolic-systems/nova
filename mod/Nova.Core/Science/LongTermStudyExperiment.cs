using System;
using System.Collections.Generic;

namespace Nova.Core.Science;

// "Sit and observe over time." Body-year is divided into 12 slices;
// each slice is its own subject so partial-fidelity files for slice 7
// don't merge with slice 8's data.
public static class LongTermStudyExperiment {
  public const string ExperimentId   = "lts";
  public const long   FileSizeBytes  = 5_000;
  public const int    SlicesPerYear  = 12;

  // Situations the experiment recognises. `Situation.None` is excluded
  // — vessels with no situation can't host an LTS file. The rendering
  // order here is the canonical UI order (surface → flying → space).
  public static readonly Situation[] SupportedSituations = new[] {
    Situation.SrfLanded,
    Situation.SrfSplashed,
    Situation.FlyingLow,
    Situation.FlyingHigh,
    Situation.InSpaceLow,
    Situation.InSpaceHigh,
  };

  // Enumerate every (situation, sliceIndex) pair the experiment can
  // produce a file for at `bodyName`. Body-independent today (every
  // body supports every situation × every slice); kept body-keyed so a
  // future per-body filter (e.g. atmospheric-only situations on
  // airless bodies) has a place to land.
  public static IEnumerable<(Situation situation, int sliceIndex)>
      AllSubjectsFor(string bodyName) {
    foreach (var s in SupportedSituations) {
      for (int i = 0; i < SlicesPerYear; i++) yield return (s, i);
    }
  }

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
