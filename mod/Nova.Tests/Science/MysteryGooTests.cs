using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nova.Core.Components;
using Nova.Core.Components.Science;
using Nova.Core.Persistence.Protos;
using Nova.Core.Samples;
using Nova.Core.Science;
using Nova.Tests.TestHelpers;

namespace Nova.Tests.Science;

[TestClass]
public class MysteryGooTests {

  // Spin up a vessel with one MysteryGoo + one DataStorage on a single
  // part. `seed` controls which sample-type ids get pre-loaded; pass
  // an empty list for an empty chamber (no SeedInitialSamples call).
  private static (VirtualVessel vessel, MysteryGoo goo, DataStorage storage) Build(
      IEnumerable<string> seed = null,
      double partDryMassKg = 0.05) {
    var goo = new MysteryGoo {
      Capacity              = 3,
      AllowedSampleTypeIds  = new[] { "mystery-goo-prime", "mystery-goo-dark" },
      InitialSampleTypeIds  = (seed ?? new[] { "mystery-goo-prime", "mystery-goo-prime", "mystery-goo-dark" }).ToList(),
      InstrumentName        = "Mystery Goo Test Chamber",
    };
    var storage = new DataStorage { CapacityBytes = 100_000 };

    var vessel = new VirtualVessel { Context = new StubVesselContext() };
    vessel.AddPart(1, "GooExperiment", partDryMassKg,
        new List<VirtualComponent> { goo, storage });
    vessel.UpdatePartTree(new Dictionary<uint, uint?> { { 1u, null } });
    vessel.InitializeSolver(0);

    // SeedInitialSamples is normally driven by the mod-side adapter
    // on a fresh launch; tests do it here so the chamber is populated
    // before exposure runs.
    goo.SeedInitialSamples();
    return (vessel, goo, storage);
  }

  private static double NodeMass(VirtualVessel vessel) {
    double total = 0;
    foreach (var n in vessel.Systems.Staging.Nodes) total += n.Mass();
    return total;
  }

  // ── Mass ────────────────────────────────────────────────────────

  [TestMethod]
  public void FreshChamber_NodeMass_IncludesSampleContribution() {
    // Default seed: 2 prime (50 g each) + 1 dark (80 g) = 180 g sample
    // mass; chamber's structural mass is 50 g (passed to AddPart).
    var (vessel, _, _) = Build();
    // Total: 50 g (dry) + 100 g (2 prime) + 80 g (dark) = 230 g.
    Assert.AreEqual(0.230, NodeMass(vessel), 1e-9);
  }

  [TestMethod]
  public void EmptyChamber_NodeMass_IsDryOnly() {
    // No samples seeded — only the structural chamber mass remains.
    var (vessel, goo, _) = Build(seed: System.Array.Empty<string>());
    Assert.AreEqual(0, goo.Samples.Count);
    Assert.AreEqual(0.05, NodeMass(vessel), 1e-9);
  }

  [TestMethod]
  public void StateTransitions_AreMassNeutral() {
    // Pristine → Exposed → Invalidated → all leave Node.Mass()
    // unchanged. Sample state is metadata; physical mass stays put
    // unless a future eject API removes the sample.
    var (vessel, goo, _) = Build();
    double initial = NodeMass(vessel);

    goo.Samples[0].Condition = SampleCondition.Exposed;
    Assert.AreEqual(initial, NodeMass(vessel), 1e-9, "Exposed must not change mass");

    goo.Samples[1].Condition = SampleCondition.Invalidated;
    Assert.AreEqual(initial, NodeMass(vessel), 1e-9, "Invalidated must not change mass");

    goo.Samples[2].Condition = SampleCondition.Exposed;
    Assert.AreEqual(initial, NodeMass(vessel), 1e-9);
  }

  // ── Cover / exposure lifecycle ──────────────────────────────────

  [TestMethod]
  public void OpenCover_ArmsExposingOnNextPristine_AndSchedulesValidUntil() {
    var (_, goo, _) = Build();
    goo.OpenCover(nowUT: 100);

    Assert.IsTrue(goo.CoverOpen);
    Assert.AreEqual(0, goo.ExposingIndex, "first Pristine sample loaded");
    Assert.AreEqual(100, goo.ExposureStartUt, 1e-9);
    // mystery-goo-prime: 30 s exposure duration.
    Assert.AreEqual(130, goo.ValidUntil, 1e-9);
  }

  [TestMethod]
  public void Tick_PastValidUntil_ProducesScienceFile_AndMarksExposed() {
    var (vessel, goo, storage) = Build();
    goo.OpenCover(nowUT: 100);

    // Advance the vessel clock past the exposure timer. VirtualVessel.Tick
    // fires Update() on the component when ValidUntil elapses.
    vessel.Tick(targetTime: 200);

    Assert.AreEqual(SampleCondition.Exposed, goo.Samples[0].Condition);
    Assert.AreEqual(1, storage.Files.Count, "exposure must deposit one ScienceFile");
    var f = storage.Files[0];
    Assert.AreEqual("mystery-goo", f.ExperimentId);
    Assert.AreEqual(1.0, f.Fidelity, 1e-9);
    Assert.IsTrue(f.IsComplete);
    Assert.AreEqual("mystery-goo@Kerbin:SrfLanded.mystery-goo-prime", f.SubjectId);
    Assert.AreEqual(f.SubjectId, goo.Samples[0].ExposedSubjectId,
        "Sample.ExposedSubjectId must link back to the produced file");

    // Exposure tracking resets — ValidUntil back to +∞, no active slot.
    Assert.AreEqual(-1, goo.ExposingIndex);
    Assert.AreEqual(double.PositiveInfinity, goo.ValidUntil);
  }

  [TestMethod]
  public void CloseCover_MidExposure_InvalidatesSample_AndProducesNoFile() {
    var (vessel, goo, storage) = Build();
    goo.OpenCover(nowUT: 100);
    // Close at 115 — 15 s into a 30 s exposure; well short of completion.
    goo.CloseCover(nowUT: 115);

    Assert.IsFalse(goo.CoverOpen);
    Assert.AreEqual(SampleCondition.Invalidated, goo.Samples[0].Condition);
    Assert.AreEqual(0, storage.Files.Count, "mid-cycle close must produce no file");
    Assert.AreEqual(-1, goo.ExposingIndex);

    // Tick across the original would-be expiry — nothing fires; the
    // sample is already in its terminal state.
    vessel.Tick(targetTime: 200);
    Assert.AreEqual(SampleCondition.Invalidated, goo.Samples[0].Condition);
    Assert.AreEqual(0, storage.Files.Count);
  }

  [TestMethod]
  public void CloseCover_AfterCompletion_DoesNotInvalidate() {
    // If exposure completes (Update fires) and THEN the player closes
    // the cover, the now-Exposed sample must stay Exposed — Close
    // only invalidates Pristine samples mid-exposure.
    var (vessel, goo, storage) = Build();
    goo.OpenCover(nowUT: 100);
    vessel.Tick(targetTime: 200);
    Assert.AreEqual(SampleCondition.Exposed, goo.Samples[0].Condition);

    goo.CloseCover(nowUT: 250);
    Assert.AreEqual(SampleCondition.Exposed, goo.Samples[0].Condition,
        "Close after completion must leave the exposed sample alone");
    Assert.AreEqual(1, storage.Files.Count);
  }

  [TestMethod]
  public void SequentialExposure_AdvancesCursor() {
    // Three exposure cycles in a row consume samples 0 → 1 → 2 in order.
    var (vessel, goo, storage) = Build();

    goo.OpenCover(nowUT: 100); vessel.Tick(200); goo.CloseCover(210);
    Assert.AreEqual(SampleCondition.Exposed, goo.Samples[0].Condition);

    goo.OpenCover(nowUT: 300); vessel.Tick(400); goo.CloseCover(410);
    Assert.AreEqual(SampleCondition.Exposed, goo.Samples[1].Condition);

    // Sample 2 is mystery-goo-dark (90 s); push the tick past the
    // longer timer to confirm it honours its own duration.
    goo.OpenCover(nowUT: 500); vessel.Tick(700);
    Assert.AreEqual(SampleCondition.Exposed, goo.Samples[2].Condition);

    Assert.AreEqual(2, storage.Files.Count,
        "two distinct subjects (prime + dark) produce two files; the second prime overwrites the first");
  }

  [TestMethod]
  public void DarkSample_HonoursLongerExposureDuration() {
    // Two prime, one dark — but here we directly compare the
    // dark-only timer.  Use a single-sample seed so cursor lands on dark.
    var (_, goo, _) = Build(seed: new[] { "mystery-goo-dark" });
    goo.OpenCover(nowUT: 1000);
    // mystery-goo-dark: 90 s exposure duration.
    Assert.AreEqual(1090, goo.ValidUntil, 1e-9);
  }

  [TestMethod]
  public void OpenCover_AllConsumed_IsNoOp() {
    // Chamber empty after we manually mark every sample Exposed →
    // opening the cover should not arm anything and ValidUntil must
    // stay at +Infinity (else VirtualVessel.Tick would spin).
    var (_, goo, _) = Build();
    foreach (var s in goo.Samples) s.Condition = SampleCondition.Exposed;

    goo.OpenCover(nowUT: 100);
    Assert.IsTrue(goo.CoverOpen, "cover physically opens — just nothing to expose");
    Assert.AreEqual(-1, goo.ExposingIndex);
    Assert.AreEqual(double.PositiveInfinity, goo.ValidUntil);
  }

  // ── Save / Load round-trip ──────────────────────────────────────

  [TestMethod]
  public void StateRoundTrip_PreservesFields() {
    var src = new MysteryGoo {
      Capacity              = 3,
      AllowedSampleTypeIds  = new[] { "mystery-goo-prime", "mystery-goo-dark" },
      InitialSampleTypeIds  = new[] { "mystery-goo-prime", "mystery-goo-dark" },
    };
    src.SeedInitialSamples();
    src.Samples[0].Condition = SampleCondition.Exposed;
    src.Samples[0].ExposedAtUt = 1234.5;
    src.Samples[0].ExposedSubjectId = "mystery-goo@Kerbin:SrfLanded.mystery-goo-prime";
    src.CoverOpen = true;
    src.ExposingIndex = 1;
    src.ExposureStartUt = 2000;

    var state = new PartState();
    src.Save(state);

    var dst = new MysteryGoo {
      Capacity              = 3,
      AllowedSampleTypeIds  = src.AllowedSampleTypeIds,
      InitialSampleTypeIds  = src.InitialSampleTypeIds,
    };
    dst.Load(state);

    // Field-equality shape (per feedback_persistence_tests_two_shapes —
    // proto fields survive the round trip).
    Assert.AreEqual(2, dst.Samples.Count);
    Assert.AreEqual("mystery-goo-prime", dst.Samples[0].Type.Id);
    Assert.AreEqual(SampleCondition.Exposed, dst.Samples[0].Condition);
    Assert.AreEqual(1234.5, dst.Samples[0].ExposedAtUt, 1e-9);
    Assert.AreEqual(src.Samples[0].ExposedSubjectId, dst.Samples[0].ExposedSubjectId);
    Assert.AreEqual(SampleCondition.Pristine, dst.Samples[1].Condition);
    Assert.IsTrue(dst.CoverOpen);
    Assert.AreEqual(1, dst.ExposingIndex);
    Assert.AreEqual(2000, dst.ExposureStartUt, 1e-9);

    // Observable shape: ValidUntil must be re-armed to the original
    // schedule (ExposureStartUt + per-type duration), so the next Tick
    // completes the exposure at the same UT it would have on the
    // source. Dark = 90 s.
    Assert.AreEqual(2090, dst.ValidUntil, 1e-9);
  }

  [TestMethod]
  public void Load_NotExposing_LeavesValidUntilInfinity() {
    var src = new MysteryGoo {
      Capacity              = 1,
      AllowedSampleTypeIds  = new[] { "mystery-goo-prime" },
      InitialSampleTypeIds  = new[] { "mystery-goo-prime" },
    };
    src.SeedInitialSamples();
    // Cover closed, no exposure in flight.
    var state = new PartState();
    src.Save(state);

    var dst = new MysteryGoo {
      Capacity              = 1,
      AllowedSampleTypeIds  = src.AllowedSampleTypeIds,
      InitialSampleTypeIds  = src.InitialSampleTypeIds,
    };
    dst.Load(state);

    Assert.AreEqual(double.PositiveInfinity, dst.ValidUntil);
  }

  [TestMethod]
  public void LoadedMidExposure_TickCompletesAtOriginalUT() {
    // Save mid-exposure, load into a fresh vessel, tick forward —
    // the saved exposure should fire its completion as if the unloaded
    // gap never happened.
    var (vesselA, gooA, _) = Build();
    gooA.OpenCover(nowUT: 100);

    // Save partway through the exposure (15 s in, 15 s to go).
    var state = new PartState();
    gooA.Save(state);

    // Brand-new vessel + load.
    var (vesselB, gooB, storageB) = Build(seed: System.Array.Empty<string>());
    gooB.Load(state);
    Assert.AreEqual(SampleCondition.Pristine, gooB.Samples[0].Condition,
        "sample is mid-exposure but still Pristine until completion fires");

    vesselB.Tick(targetTime: 200);
    Assert.AreEqual(SampleCondition.Exposed, gooB.Samples[0].Condition,
        "Tick past the saved completion UT must fire the deferred completion");
    Assert.AreEqual(1, storageB.Files.Count);
  }

  // ── Live progress helpers ───────────────────────────────────────

  [TestMethod]
  public void UpdateLiveProgress_PreClampsForWire() {
    var (_, goo, _) = Build();
    goo.OpenCover(nowUT: 100);
    // 15 / 30 → 0.5 progress, 15 s remaining.
    goo.UpdateLiveProgress(115);
    Assert.AreEqual(0.5, goo.LiveExposureProgress, 1e-9);
    Assert.AreEqual(15, goo.LiveExposureRemainingSec, 1e-9);

    // Past completion clamps to 1 / 0 — the wire never reports >100%.
    goo.UpdateLiveProgress(1000);
    Assert.AreEqual(1.0, goo.LiveExposureProgress, 1e-9);
    Assert.AreEqual(0, goo.LiveExposureRemainingSec, 1e-9);

    // Cover closed → both fields zeroed regardless of saved start UT.
    goo.CloseCover(nowUT: 115);
    goo.UpdateLiveProgress(115);
    Assert.AreEqual(0, goo.LiveExposureProgress, 1e-9);
    Assert.AreEqual(0, goo.LiveExposureRemainingSec, 1e-9);
  }
}
