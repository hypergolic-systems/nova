using System.Collections.Generic;
using System.Text;
using Dragonglass.Telemetry.Topics;
using Nova.Core.Persistence.Protos;
using Nova.Core.Science;
using Nova.Science;
using UnityEngine;

namespace Nova.Telemetry;

// Singleton topic carrying the KSC-side science archive — every record
// the player has transmitted home, plus the full possible-subject
// inventory so the UI can render unstudied gaps next to archived
// results.
//
// Wire format (positional):
//   [
//     // Per-body summary. Order: FlightGlobals.Bodies (solar-system
//     // depth-first). parentName is "" for the Sun, otherwise the
//     // orbital parent. Counts are pre-rolled so the UI's body list
//     // renders without scanning subjects.
//     [ [bodyName, parentName, archivedCount, possibleCount], … ],
//
//     // Per-(body, experimentId) subject grids. One outer entry per
//     // (body, experiment) pair with at least one possible subject.
//     // `slice` is -1 for variant-only experiments (atm-profile);
//     // `sourceVesselName` reads off the archive record's persisted
//     // name field — never resolved at emit-time. Gaps emit fidelity
//     // 0, receivedAtUt 0, sourceVesselName "".
//     [
//       [ bodyName, experimentId,
//         [ [variant, slice, fidelity, receivedAtUt, sourceVesselName], … ]
//       ],
//       …
//     ]
//   ]
//
// Read-only — no inbound ops in v1.
public sealed class NovaScienceArchiveTopic : Topic {
  public static NovaScienceArchiveTopic Instance { get; private set; }

  public override string Name => "NovaScienceArchive";

  protected override void OnEnable() {
    Instance = this;
    NovaScienceArchive.Changed += OnArchiveChanged;
    GameEvents.onLevelWasLoaded.Add(OnLevelLoaded);
    base.OnEnable();
    MarkDirty();
  }

  protected override void OnDisable() {
    NovaScienceArchive.Changed -= OnArchiveChanged;
    GameEvents.onLevelWasLoaded.Remove(OnLevelLoaded);
    base.OnDisable();
    if (Instance == this) Instance = null;
  }

  private void OnArchiveChanged() {
    MarkDirty();
  }

  // Body roster only populates fully once KSP transitions out of the
  // loading scene. Re-emit on every level load so subscribers see the
  // ready roster without polling.
  private void OnLevelLoaded(GameScenes scene) {
    MarkDirty();
  }

  public override void WriteData(StringBuilder sb) {
    var archive = NovaScienceArchive.Instance;
    var bodies = ResolveBodies();

    JsonWriter.Begin(sb, '[');

    // ---- Body summary list -------------------------------------
    JsonWriter.Begin(sb, '[');
    bool firstBody = true;
    foreach (var b in bodies) {
      int possible = 0, archived = 0;
      CountSubjects(archive, b.name, ref possible, ref archived);
      JsonWriter.Sep(sb, ref firstBody);
      JsonWriter.Begin(sb, '[');
      bool f = true;
      JsonWriter.Sep(sb, ref f);
      JsonWriter.WriteString(sb, b.name);
      JsonWriter.Sep(sb, ref f);
      JsonWriter.WriteString(sb, b.parent);
      JsonWriter.Sep(sb, ref f);
      JsonWriter.WriteDouble(sb, archived);
      JsonWriter.Sep(sb, ref f);
      JsonWriter.WriteDouble(sb, possible);
      JsonWriter.End(sb, ']');
    }
    JsonWriter.End(sb, ']');

    sb.Append(',');

    // ---- Per-(body, experiment) subject grids ------------------
    JsonWriter.Begin(sb, '[');
    bool firstGrid = true;
    foreach (var b in bodies) {
      WriteAtmGridForBody(sb, archive, b.name, ref firstGrid);
      WriteLtsGridForBody(sb, archive, b.name, ref firstGrid);
    }
    JsonWriter.End(sb, ']');

    JsonWriter.End(sb, ']');
  }

  // ---- Body roster ---------------------------------------------

  // Solar-system order, each body paired with its orbital parent's
  // name ("" for the Sun, which references itself in stock).
  // Defensive: the topic broadcaster ticks during the KSP loading
  // scene, before `FlightGlobals.Bodies` is fully populated. Skip
  // bodies whose name field hasn't been wired yet rather than NRE.
  private static IEnumerable<(string name, string parent)> ResolveBodies() {
    var bodies = FlightGlobals.Bodies;
    if (bodies == null) yield break;
    for (int i = 0; i < bodies.Count; i++) {
      var body = bodies[i];
      if (body == null) continue;
      string name = body.bodyName;
      if (string.IsNullOrEmpty(name)) continue;
      string parent = "";
      var refBody = body.referenceBody;
      if (refBody != null && refBody != body) {
        parent = refBody.bodyName ?? "";
      }
      yield return (name, parent);
    }
  }

  // ---- Counters ------------------------------------------------

  private static void CountSubjects(NovaScienceArchive archive, string bodyName,
                                    ref int possible, ref int archived) {
    var layers = AtmosphericProfileExperiment.LayersFor(bodyName);
    if (layers != null) {
      foreach (var l in layers) {
        possible++;
        var key = new SubjectKey(
            AtmosphericProfileExperiment.ExperimentId, bodyName, l.name);
        if (archive.TryGet(key.ToString(), out _)) archived++;
      }
    }
    foreach (var (situation, slice) in
             LongTermStudyExperiment.AllSubjectsFor(bodyName)) {
      possible++;
      var key = new SubjectKey(
          LongTermStudyExperiment.ExperimentId,
          bodyName, situation.ToString(), slice);
      if (archive.TryGet(key.ToString(), out _)) archived++;
    }
  }

  // ---- Grid writers --------------------------------------------

  private static void WriteAtmGridForBody(
      StringBuilder sb, NovaScienceArchive archive, string bodyName, ref bool first) {
    var layers = AtmosphericProfileExperiment.LayersFor(bodyName);
    if (layers == null) return;

    JsonWriter.Sep(sb, ref first);
    JsonWriter.Begin(sb, '[');
    bool f = true;
    JsonWriter.Sep(sb, ref f);
    JsonWriter.WriteString(sb, bodyName);
    JsonWriter.Sep(sb, ref f);
    JsonWriter.WriteString(sb, AtmosphericProfileExperiment.ExperimentId);
    JsonWriter.Sep(sb, ref f);
    JsonWriter.Begin(sb, '[');
    bool firstSubj = true;
    foreach (var l in layers) {
      var key = new SubjectKey(
          AtmosphericProfileExperiment.ExperimentId, bodyName, l.name);
      WriteSubjectEntry(sb, archive, key, l.name, sliceIndex: -1, ref firstSubj);
    }
    JsonWriter.End(sb, ']');
    JsonWriter.End(sb, ']');
  }

  private static void WriteLtsGridForBody(
      StringBuilder sb, NovaScienceArchive archive, string bodyName, ref bool first) {
    JsonWriter.Sep(sb, ref first);
    JsonWriter.Begin(sb, '[');
    bool f = true;
    JsonWriter.Sep(sb, ref f);
    JsonWriter.WriteString(sb, bodyName);
    JsonWriter.Sep(sb, ref f);
    JsonWriter.WriteString(sb, LongTermStudyExperiment.ExperimentId);
    JsonWriter.Sep(sb, ref f);
    JsonWriter.Begin(sb, '[');
    bool firstSubj = true;
    foreach (var (situation, slice) in
             LongTermStudyExperiment.AllSubjectsFor(bodyName)) {
      var key = new SubjectKey(
          LongTermStudyExperiment.ExperimentId,
          bodyName, situation.ToString(), slice);
      WriteSubjectEntry(sb, archive, key, situation.ToString(),
                        sliceIndex: slice, ref firstSubj);
    }
    JsonWriter.End(sb, ']');
    JsonWriter.End(sb, ']');
  }

  private static void WriteSubjectEntry(
      StringBuilder sb, NovaScienceArchive archive, SubjectKey key,
      string variant, int sliceIndex, ref bool first) {
    JsonWriter.Sep(sb, ref first);
    JsonWriter.Begin(sb, '[');
    bool f = true;
    JsonWriter.Sep(sb, ref f);
    JsonWriter.WriteString(sb, variant);
    JsonWriter.Sep(sb, ref f);
    JsonWriter.WriteDouble(sb, sliceIndex);

    double fidelity = 0;
    double receivedAt = 0;
    string sourceVessel = "";
    if (archive.TryGet(key.ToString(), out var record)
        && record != null && record.File != null) {
      fidelity     = record.File.Fidelity;
      receivedAt   = record.ReceivedAtUt;
      sourceVessel = record.SourceVesselName ?? "";
    }
    JsonWriter.Sep(sb, ref f);
    JsonWriter.WriteDouble(sb, fidelity);
    JsonWriter.Sep(sb, ref f);
    JsonWriter.WriteDouble(sb, receivedAt);
    JsonWriter.Sep(sb, ref f);
    JsonWriter.WriteString(sb, sourceVessel);
    JsonWriter.End(sb, ']');
  }
}
