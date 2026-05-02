using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nova.Core.Components;
using Nova.Core.Components.Electrical;
using Nova.Core.Components.Science;
using Nova.Core.Persistence.Protos;
using Nova.Core.Resources;
using Nova.Core.Science;
using Nova.Tests.TestHelpers;
using Buffer = Nova.Core.Resources.Buffer;

namespace Nova.Tests.Science;

[TestClass]
public class LongTermStudyTests {

  private const double BodyYearSeconds = 12_000;
  private const double SliceDuration   = BodyYearSeconds / 12;

  private static (VirtualVessel vessel, Thermometer therm, DataStorage storage) Build(
      double batteryContents = 1_000_000) {
    var battery = new Battery {
      Buffer = new Buffer {
        Resource = Resource.ElectricCharge,
        Capacity = 1_000_000, Contents = batteryContents,
        MaxRateIn = 100, MaxRateOut = 100,
      },
    };
    var therm = new Thermometer { EcRate = 0.0075 };
    var storage = new DataStorage { CapacityBytes = 1_000_000 };
    var vessel = new VirtualVessel {
      Context = new StubVesselContext { BodyYearSeconds = BodyYearSeconds },
    };
    vessel.AddPart(1, "pod", 0, new List<VirtualComponent> { battery, therm, storage });
    vessel.UpdatePartTree(new Dictionary<uint, uint?> { { 1u, null } });
    vessel.InitializeSolver(0);
    return (vessel, therm, storage);
  }

  // Mimic NovaThermometerModule starting an LTS run. Enable the
  // experiment, drop a file for the current slice, prime ValidUntil
  // so the M1 scheduler advances to the next slice boundary on Tick.
  private static void StartLts(Thermometer t, double nowUT) {
    t.LtsEnabled = true;
    var c = t.Vessel.Context;
    var subject = LongTermStudyExperiment.SubjectFor(c.BodyName, c.Situation, nowUT, c.BodyYearSeconds);
    var sliceEnd = LongTermStudyExperiment.NextSliceBoundary(nowUT, c.BodyYearSeconds);
    var sliceDur = LongTermStudyExperiment.SliceDurationFor(c.BodyYearSeconds);
    t.EnsureLtsFile(subject, nowUT, sliceEnd, sliceDur);
    t.LtsCurrentSubjectId = subject.ToString();
    t.LtsActive = true;
    t.ValidUntil = sliceEnd;
  }

  // Recompute fidelity for an interpolated file at a given UT (the
  // canonical formula, mirrored from the wire emit path). The file's
  // slice_duration drives the denominator so a mid-slice start tops
  // out at less than 1.0 even when the observation window completes.
  private static double InterpolateFidelity(ScienceFile f, double nowUT) {
    if (f.SliceDurationSeconds <= 0) return 0;
    double covered = System.Math.Min(nowUT, f.EndUt) - f.StartUt;
    return System.Math.Min(1.0, System.Math.Max(0.0, covered / f.SliceDurationSeconds));
  }

  [TestMethod]
  public void FullYear_TwelveSlicesAtFullFidelity() {
    var (vessel, therm, storage) = Build();
    StartLts(therm, 0);

    vessel.Tick(BodyYearSeconds);

    var ltsFiles = storage.Files.Where(f => f.ExperimentId == "lts").ToList();
    Assert.AreEqual(12, ltsFiles.Count);
    foreach (var f in ltsFiles) {
      double fid = InterpolateFidelity(f, BodyYearSeconds);
      Assert.AreEqual(1.0, fid, 1e-3,
          $"Slice {f.SubjectId} expected full fidelity at year-end, got {fid}");
    }
  }

  [TestMethod]
  public void Files_AreUniqueSlices_ZeroThroughEleven() {
    var (vessel, therm, storage) = Build();
    StartLts(therm, 0);
    vessel.Tick(BodyYearSeconds);

    var sliceIndices = storage.Files
        .Where(f => f.ExperimentId == "lts")
        .Select(f => SubjectKey.TryParse(f.SubjectId, out var k) ? k.SliceIndex ?? -1 : -1)
        .OrderBy(i => i)
        .ToList();
    CollectionAssert.AreEqual(Enumerable.Range(0, 12).ToList(), sliceIndices);
  }

  [TestMethod]
  public void MidSliceStart_PartialFidelityAtSliceEnd() {
    // Start at the half-way point of slice 0. The file's start_ut =
    // SliceDuration/2; end_ut = SliceDuration. Max reachable fidelity
    // = (end - start) / sliceDuration = 0.5.
    var (vessel, therm, storage) = Build();
    double startAt = SliceDuration / 2;
    StartLts(therm, startAt);
    vessel.Tick(SliceDuration);    // slice 0 boundary

    var slice0 = storage.Files.FirstOrDefault(f => f.SubjectId == "lts@Kerbin:SrfLanded:0");
    Assert.IsNotNull(slice0);
    double sliceEnd = SliceDuration;
    double fid = InterpolateFidelity(slice0, sliceEnd);
    Assert.AreEqual(0.5, fid, 1e-3,
        "Mid-slice start: fidelity at slice end should be 0.5");
  }

  [TestMethod]
  public void Idle_NoFiles() {
    var (vessel, _, storage) = Build();
    vessel.Tick(BodyYearSeconds);
    Assert.AreEqual(0, storage.Files.Count);
  }

  [TestMethod]
  public void TickJumpsDirectlyToSliceBoundaries() {
    var (vessel, therm, _) = Build();
    StartLts(therm, 0);
    int before = vessel.SolveCount;
    vessel.Tick(BodyYearSeconds);
    int solves = vessel.SolveCount - before;
    Assert.IsTrue(solves < 100,
        $"Expected O(slices) solves over a body-year, got {solves}");
  }
}
