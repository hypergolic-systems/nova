using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nova.Core.Components;
using Nova.Core.Components.Electrical;
using Nova.Core.Components.Science;
using Nova.Core.Resources;
using Nova.Core.Science;
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
      BodyName = "Kerbin",
      BodyId = 1,
      Situation = Situation.SrfLanded,
      BodyYearSeconds = BodyYearSeconds,
    };
    vessel.AddPart(1, "pod", 0, new List<VirtualComponent> { battery, therm, storage });
    vessel.UpdatePartTree(new Dictionary<uint, uint?> { { 1u, null } });
    vessel.InitializeSolver(0);
    return (vessel, therm, storage);
  }

  // Mimic NovaThermometerModule starting an LTS run. After flipping
  // LtsActive, prime the LP at this UT so the device.Demand /
  // .Satisfaction reflect the new active state before any slice
  // rollover fires.
  private static void StartLts(Thermometer t, double nowUT) {
    var v = t.Vessel;
    var subject = LongTermStudyExperiment.SubjectFor(v.BodyName, v.Situation, nowUT, v.BodyYearSeconds);
    t.StartOrSwitchLts(subject, nowUT);
    v.Invalidate();
    v.Tick(nowUT);  // forces DoSolve with the new active state
  }

  [TestMethod]
  public void FullYear_TwelveSlicesAtFullFidelity() {
    var (vessel, therm, storage) = Build();
    StartLts(therm, 0);

    // Sanity: after StartLts the thermometer should be drawing EC at
    // full satisfaction. If this is 0 then everything downstream
    // accrues at 0 and we'd diagnose endlessly.
    Assert.AreEqual(1.0, therm.Satisfaction, 1e-6,
        "Thermometer should be at full sat after StartLts primes the solve");

    vessel.Tick(BodyYearSeconds);

    var ltsFiles = storage.Files.Where(f => f.ExperimentId == "lts").ToList();
    Assert.AreEqual(12, ltsFiles.Count);
    foreach (var f in ltsFiles)
      Assert.AreEqual(1.0, f.Fidelity, 1e-3,
          $"Slice {f.SubjectId} expected full fidelity, got {f.Fidelity}");
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
  public void StarvedInstrument_PartialThenZero() {
    // 27 EC at 0.0075/s = 3600 s of active draw before depletion.
    // Without satisfaction tracking we sample sat at slice end:
    // - slices 0..2 (ending at 1000, 2000, 3000): battery still has
    //   plenty, sat=1.0, fidelity=1.0
    // - slice 3 (ending at 4000): by then battery's empty (depleted
    //   at ~3600), sat=0, fidelity=0
    // - slices 4..11: sat=0, fidelity=0
    var (vessel, therm, storage) = Build(batteryContents: 27);
    StartLts(therm, 0);
    vessel.Tick(BodyYearSeconds);

    var ltsFiles = storage.Files.Where(f => f.ExperimentId == "lts").ToList();
    Assert.AreEqual(12, ltsFiles.Count);

    int fullCount = ltsFiles.Count(f => f.Fidelity > 0.99);
    int zeroCount = ltsFiles.Count(f => f.Fidelity < 0.01);
    Assert.IsTrue(fullCount >= 2 && fullCount <= 4,
        $"Expected ~3 full-fidelity slices before starvation, got {fullCount}");
    Assert.IsTrue(zeroCount >= 8,
        $"Expected ≥ 8 zero-fidelity slices after starvation, got {zeroCount}");
  }

  [TestMethod]
  public void MidSliceSubjectChange_FinalisesPartialFile() {
    var (vessel, therm, storage) = Build();
    StartLts(therm, 0);                            // slice 0, Kerbin/SrfLanded
    vessel.Tick(SliceDuration / 2);                // half a slice elapses

    // Liftoff: situation flips to FlyingLow.
    therm.Vessel.Situation = Situation.FlyingLow;
    var newSubject = LongTermStudyExperiment.SubjectFor(
        "Kerbin", Situation.FlyingLow, SliceDuration / 2, BodyYearSeconds);
    therm.StartOrSwitchLts(newSubject, SliceDuration / 2);

    var oldFile = storage.Files.FirstOrDefault(f => f.SubjectId == "lts@Kerbin:SrfLanded:0");
    Assert.IsNotNull(oldFile);
    Assert.AreEqual(0.5, oldFile.Fidelity, 0.05,
        "Half a slice elapsed → fidelity ≈ 0.5");
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
