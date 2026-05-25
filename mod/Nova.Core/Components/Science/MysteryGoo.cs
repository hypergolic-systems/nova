using System.Collections.Generic;
using System.Linq;
using Nova.Core.Persistence.Protos;
using Nova.Core.Samples;
using Nova.Core.Science;
using Nova.Core.Systems;

namespace Nova.Core.Components.Science;

// Mystery-goo chamber. Holds an inventory of discrete typed samples
// and exposes them one at a time. Player operates only the cover:
// opening starts exposure of the next Pristine sample; closing before
// the timer elapses invalidates that sample (its mass stays, since
// it's still physically in the chamber). Clean completion produces a
// ScienceFile into the nearest DataStorage and flips the sample to
// Exposed. The chamber has no EC draw — goo is passive.
//
// This component is the canonical user of the foundational Sample
// abstraction; future containers (materials bay, surface-sample bin,
// drill core) will reuse SampleType / Sample / Mass().
public class MysteryGoo : VirtualComponent {
  // ── cfg-declared (not persisted) ────────────────────────────────────
  public int    Capacity;
  public IReadOnlyList<string> AllowedSampleTypeIds = System.Array.Empty<string>();
  public IReadOnlyList<string> InitialSampleTypeIds = System.Array.Empty<string>();

  // Player-facing name stamped onto produced ScienceFiles; set by the
  // mod-side adapter from `part.partInfo.title`. Fallback for tests.
  public string InstrumentName = "Mystery Goo";

  // ── runtime (persisted via MysteryGooState) ─────────────────────────
  public List<Sample> Samples = new();
  public int          ExposingIndex   = -1;
  public double       ExposureStartUt;
  public bool         CoverOpen;

  // ── live derived (not persisted) ────────────────────────────────────
  // Updated each FixedUpdate by the mod-side adapter (or test scaffolding)
  // so the wire frame carries pre-computed physical observables — the UI
  // never reconstructs progress from start_ut + duration per
  // feedback_wire_no_solver_internals. Both are 0 when not exposing.
  public double LiveExposureProgress;     // 0..1
  public double LiveExposureRemainingSec; // seconds

  public override void OnBuildSystems(VesselSystems systems, StagingFlowSystem.Node node) {
    node.Components.Add(this);
  }

  public override double Mass() {
    double total = 0;
    foreach (var s in Samples) total += s.Type.MassKg;
    return total;
  }

  // Refresh the live exposure-progress fields against `nowUT`. Called
  // by the mod-side adapter each FixedUpdate so the wire frame carries
  // (pre-clamped) physical observables. Tests / sim runners can call
  // directly. No-op when not exposing — fields are zeroed.
  public void UpdateLiveProgress(double nowUT) {
    if (!CoverOpen || ExposingIndex < 0 || ExposingIndex >= Samples.Count) {
      LiveExposureProgress = 0;
      LiveExposureRemainingSec = 0;
      return;
    }
    var duration = Samples[ExposingIndex].Type.ExposureDurationSec;
    if (duration <= 0) {
      LiveExposureProgress = 1;
      LiveExposureRemainingSec = 0;
      return;
    }
    var elapsed = nowUT - ExposureStartUt;
    var clamped = elapsed < 0 ? 0 : (elapsed > duration ? duration : elapsed);
    LiveExposureProgress = clamped / duration;
    LiveExposureRemainingSec = duration - clamped;
  }

  // Seed Pristine samples from InitialSampleTypeIds if the chamber is
  // empty. Idempotent: a chamber already populated (post-Load, or
  // already seeded by an earlier OnStart pass) is a no-op. Unknown
  // type ids are skipped silently — the cfg author should pin them.
  public void SeedInitialSamples() {
    if (Samples.Count > 0) return;
    foreach (var id in InitialSampleTypeIds) {
      if (!SampleRegistry.TryGet(id, out var type)) continue;
      Samples.Add(new Sample(type));
    }
  }

  // Index of the lowest Pristine sample, or -1 if none. The cover-open
  // cursor: opening the cover exposes whatever this returns.
  public int FindNextPristineIndex() {
    for (int i = 0; i < Samples.Count; i++) {
      if (Samples[i].Condition == SampleCondition.Pristine) return i;
    }
    return -1;
  }

  // Open the cover. If a Pristine sample is loaded, exposure begins
  // and ValidUntil schedules completion. No-op if already open or if
  // no Pristine sample is available (cover is open but inert).
  public void OpenCover(double nowUT) {
    if (CoverOpen) return;
    CoverOpen = true;
    int next = FindNextPristineIndex();
    if (next < 0) {
      ExposingIndex = -1;
      ValidUntil = double.PositiveInfinity;
      Vessel?.Invalidate();
      return;
    }
    ExposingIndex = next;
    ExposureStartUt = nowUT;
    ValidUntil = nowUT + Samples[next].Type.ExposureDurationSec;
    Vessel?.Invalidate();
  }

  // Close the cover. If the currently-exposing sample's timer hasn't
  // elapsed, invalidate it. Mass-neutral — the invalidated sample
  // stays in the chamber.
  public void CloseCover(double nowUT) {
    if (!CoverOpen) return;
    CoverOpen = false;
    if (ExposingIndex >= 0 && ExposingIndex < Samples.Count) {
      var s = Samples[ExposingIndex];
      bool completed = nowUT >= ExposureStartUt + s.Type.ExposureDurationSec;
      if (!completed && s.Condition == SampleCondition.Pristine) {
        s.Condition = SampleCondition.Invalidated;
      }
    }
    ExposingIndex = -1;
    ValidUntil = double.PositiveInfinity;
    Vessel?.Invalidate();
  }

  public override void Update(double nowUT) {
    if (!CoverOpen || ExposingIndex < 0 || ExposingIndex >= Samples.Count) {
      ValidUntil = double.PositiveInfinity;
      return;
    }
    var s = Samples[ExposingIndex];
    double endUt = ExposureStartUt + s.Type.ExposureDurationSec;
    if (nowUT < endUt) { ValidUntil = endUt; return; }

    // Exposure complete: produce a ScienceFile and mark Exposed. Cover
    // stays open — player closes it (or opens-after-close, no-op since
    // it's already open). Next OpenCover after a Close picks up the
    // next Pristine sample via the cursor.
    var subject = MysteryGooExperiment.SubjectFor(
        s.Type, Vessel?.Context?.BodyName ?? "", Vessel?.Context?.Situation ?? Situation.None);
    var subjectId = subject.ToString();
    var storage = FindOrAcquireStorageFor(subjectId, MysteryGooExperiment.FileSizeBytes);
    if (storage != null) {
      // Upsert: if a file for this subject already exists (a prior
      // exposure of the same goo type in the same body+situation),
      // overwrite — completed-fidelity-1 is the final state either way.
      storage.Upsert(new ScienceFile {
        SubjectId    = subjectId,
        ExperimentId = MysteryGooExperiment.ExperimentId,
        Fidelity     = 1.0,
        IsComplete   = true,
        ProducedAt   = nowUT,
        Instrument   = InstrumentName,
      }, MysteryGooExperiment.FileSizeBytes);
      s.ExposedSubjectId = subjectId;
    }
    s.Condition = SampleCondition.Exposed;
    s.ExposedAtUt = nowUT;
    ExposingIndex = -1;
    ValidUntil = double.PositiveInfinity;
    Vessel?.Invalidate();
  }

  private DataStorage FindOrAcquireStorageFor(string subjectId, long sizeBytes) {
    if (Vessel == null) return null;
    DataStorage withCapacity = null;
    foreach (var s in Vessel.AllComponents().OfType<DataStorage>()) {
      if (s.HasSubject(subjectId)) return s;
      if (withCapacity == null && s.CanDeposit(sizeBytes)) withCapacity = s;
    }
    if (withCapacity == null)
      Vessel.Log?.Invoke($"[Science] No storage with capacity for {subjectId}; dropped");
    return withCapacity;
  }

  public override void Save(PartState state) {
    var s = new MysteryGooState {
      ExposingIndex   = ExposingIndex,
      ExposureStartUt = ExposureStartUt,
      CoverOpen       = CoverOpen,
    };
    foreach (var sample in Samples) {
      s.Samples.Add(new Nova.Core.Persistence.Protos.SampleState {
        TypeId           = sample.Type.Id,
        State            = (int)sample.Condition,
        ExposedAtUt      = sample.ExposedAtUt,
        ExposedSubjectId = sample.ExposedSubjectId ?? "",
      });
    }
    state.MysteryGoo = s;
  }

  public override void Load(PartState state) {
    if (state.MysteryGoo == null) return;
    var g = state.MysteryGoo;
    Samples.Clear();
    foreach (var sm in g.Samples) {
      if (!SampleRegistry.TryGet(sm.TypeId, out var type)) continue;
      Samples.Add(new Sample(type) {
        Condition        = (SampleCondition)sm.State,
        ExposedAtUt      = sm.ExposedAtUt,
        ExposedSubjectId = sm.ExposedSubjectId ?? "",
      });
    }
    CoverOpen       = g.CoverOpen;
    ExposureStartUt = g.ExposureStartUt;
    ExposingIndex   = g.ExposingIndex;

    // Re-arm ValidUntil so the next Tick completes (or skips) the
    // mid-exposure sample at the originally-scheduled time. Unloaded-
    // vessel ticks past completion will fire Update once.
    if (CoverOpen && ExposingIndex >= 0 && ExposingIndex < Samples.Count) {
      ValidUntil = ExposureStartUt + Samples[ExposingIndex].Type.ExposureDurationSec;
    } else {
      ValidUntil = double.PositiveInfinity;
    }
  }

  public override VirtualComponent Clone() {
    var c = (MysteryGoo)MemberwiseClone();
    c.Samples = new List<Sample>(Samples.Count);
    foreach (var s in Samples) {
      c.Samples.Add(new Sample(s.Type) {
        Condition        = s.Condition,
        ExposedAtUt      = s.ExposedAtUt,
        ExposedSubjectId = s.ExposedSubjectId,
      });
    }
    return c;
  }
}
