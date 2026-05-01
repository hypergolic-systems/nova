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

  // Use a 12 ks year for testable arithmetic — slice = 1 ks each.
  private const double BodyYearSeconds = 12_000;
  private const double SliceDuration   = BodyYearSeconds / 12;

  // Build a vessel with a thermometer + a battery sized to feed it for
  // the full study duration. The battery's buffer is sized so EC never
  // runs out during the test (1.0 satisfaction throughout).
  private static (VirtualVessel vessel, Thermometer therm, DataStorage storage) Build(
      double batteryContents = 1_000_000) {
    var battery = new Battery {
      Buffer = new Buffer {
        Resource   = Resource.ElectricCharge,
        Capacity   = 1_000_000,
        Contents   = batteryContents,
        MaxRateIn  = 100,
        MaxRateOut = 100,
      },
    };
    var therm = new Thermometer { EcRate = 0.0075 };
    var storage = new DataStorage { CapacityBytes = 1_000_000 };
    var vessel = new VirtualVessel();
    vessel.AddPart(1, "pod", 0, new List<VirtualComponent> { battery, therm, storage });
    vessel.UpdatePartTree(new Dictionary<uint, uint?> { { 1u, null } });
    vessel.InitializeSolver(0);

    // Registry needed for FileSizeBytes lookup at deposit time.
    var reg = new ExperimentRegistry();
    reg.Register(new AtmosphericProfileExperiment(new AtmosphereLayers()));
    reg.Register(new LongTermStudyExperiment());
    ExperimentRegistry.Instance = reg;

    return (vessel, therm, storage);
  }

  // Mimic what NovaThermometerModule does on a loaded vessel: declare
  // the thermometer active in a body+situation, push the snapshot.
  private static void EnterLts(Thermometer t, double nowUT,
                                string body = "Kerbin",
                                Situation sit = Situation.SrfLanded,
                                uint bodyId = 1) {
    var subject = new SubjectKey(
        LongTermStudyExperiment.ExperimentId, body, sit.ToString(),
        LongTermStudyExperiment.SliceIndexAt(nowUT, BodyYearSeconds));
    t.IsActive = true;
    t.OnSubjectChanged(subject, sit, bodyId, body, BodyYearSeconds, nowUT);
  }

  [TestMethod]
  public void FullYear_TwelveSlicesAtFullFidelity() {
    var (vessel, therm, storage) = Build();
    EnterLts(therm, 0);
    vessel.Tick(BodyYearSeconds);

    Assert.AreEqual(12, storage.Files.Count, "One file per slice over a full year");
    var ltsFiles = storage.Files.Where(f => f.ExperimentId == "lts").ToList();
    Assert.AreEqual(12, ltsFiles.Count);
    foreach (var f in ltsFiles)
      Assert.AreEqual(1.0, f.Fidelity, 1e-3,
          $"Slice {f.SubjectId} expected full fidelity, got {f.Fidelity}");
  }

  [TestMethod]
  public void Files_AreUniqueSlices_ZeroThroughEleven() {
    var (vessel, therm, storage) = Build();
    EnterLts(therm, 0);
    vessel.Tick(BodyYearSeconds);

    var sliceIndices = storage.Files
        .Where(f => f.ExperimentId == "lts")
        .Select(f => SubjectKey.TryParse(f.SubjectId, out var k) ? k.SliceIndex ?? -1 : -1)
        .OrderBy(i => i)
        .ToList();
    CollectionAssert.AreEqual(Enumerable.Range(0, 12).ToList(), sliceIndices);
  }

  [TestMethod]
  public void StarvedInstrument_PartialFidelity() {
    // Battery sized so EC runs out partway through the year. The
    // first Tick iteration runs IntegrateBuffers with stale rates
    // (needsSolve=true, no Solve has run yet), which delays drain
    // start by one slice. Effective active period: ~4600s before the
    // battery empties. With 1000s slices that's 4 full slices at
    // fidelity 1.0 plus a partial 0.6 = total 4.6.
    var (vessel, therm, storage) = Build(batteryContents: 27);
    EnterLts(therm, 0);
    vessel.Tick(BodyYearSeconds);

    Assert.AreEqual(12, storage.Files.Count, "Slice files emitted regardless of starvation");

    var totalFidelity = storage.Files.Where(f => f.ExperimentId == "lts").Sum(f => f.Fidelity);
    Assert.IsTrue(totalFidelity > 4.4 && totalFidelity < 4.8,
        $"Total fidelity should be ≈ 4.6 (≈4600s active / 1000s slice), got {totalFidelity:F2}");

    // Tail slices after starvation should be at fidelity 0 — confirms
    // we're tracking satisfaction, not just slice count.
    var lastSlice = storage.Files
        .Where(f => f.ExperimentId == "lts")
        .OrderByDescending(f => SubjectKey.TryParse(f.SubjectId, out var k) ? k.SliceIndex ?? -1 : -1)
        .First();
    Assert.AreEqual(0, lastSlice.Fidelity, 1e-6,
        "Final slice should be at fidelity 0 (battery long since drained)");
  }

  [TestMethod]
  public void MidSliceSubjectChange_FinalisesPartialFile() {
    var (vessel, therm, storage) = Build();
    // Enter slice 0 on Kerbin Landed at t=0.
    EnterLts(therm, 0, body: "Kerbin", sit: Situation.SrfLanded);
    // Half the slice elapses…
    vessel.Tick(SliceDuration / 2);
    // …then the situation changes (e.g. liftoff). Finalise old subject,
    // start fresh on FlyingLow.
    var newSubject = new SubjectKey("lts", "Kerbin", Situation.FlyingLow.ToString(), 0);
    therm.OnSubjectChanged(newSubject, Situation.FlyingLow, 1, "Kerbin", BodyYearSeconds, SliceDuration / 2);

    var oldSubjectFile = storage.Files.FirstOrDefault(f => f.SubjectId == "lts@Kerbin:SrfLanded:0");
    Assert.IsNotNull(oldSubjectFile, "Partial-fidelity file should be emitted on subject change");
    Assert.AreEqual(0.5, oldSubjectFile.Fidelity, 0.05,
        "Half a slice elapsed → fidelity ≈ 0.5");
  }

  [TestMethod]
  public void NoActiveSubject_NoFiles() {
    var (vessel, therm, storage) = Build();
    // Don't EnterLts — instrument idle.
    vessel.Tick(BodyYearSeconds);
    Assert.AreEqual(0, storage.Files.Count);
  }

  [TestMethod]
  public void TickJumpsDirectlyToSliceBoundaries() {
    // Confirms the scheduler primitive event-driven path: on an unloaded
    // vessel jumping a full year, only one solve+update should fire per
    // slice (12 total), not 12_000 per-second iterations.
    var (vessel, therm, storage) = Build();
    EnterLts(therm, 0);
    int initialSolveCount = vessel.SolveCount;
    vessel.Tick(BodyYearSeconds);
    int solvesDuringYear = vessel.SolveCount - initialSolveCount;
    // Allow generous headroom — the exact count depends on integration
    // details — but it should be O(slices), not O(seconds).
    Assert.IsTrue(solvesDuringYear < 100,
        $"Solve count too high: {solvesDuringYear} solves in 1 body-year — event-driven path may be broken");
  }
}
