using System.Collections.Generic;
using System.Text;
using Nova.Core.Persistence.Protos;
using Nova.Core.Science;

namespace Nova.Core.Telemetry;

// Singleton archive wire frame. Every record the player has
// transmitted home, plus the full possible-subject inventory so the
// UI can render unstudied gaps next to archived results.
//
// Wire format (positional):
//   [
//     // Per-body summary. parentName is "" for the Sun, otherwise
//     // the orbital parent. Counts are pre-rolled so the UI's body
//     // list renders without scanning subjects.
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
public static class ScienceArchiveFormatter {
  public static void Write(StringBuilder sb,
      IScienceArchive archive,
      IEnumerable<(string name, string parent)> bodies) {
    var bodyList = new List<(string name, string parent)>();
    if (bodies != null) foreach (var b in bodies) bodyList.Add(b);

    JsonWriter.Begin(sb, '[');

    // ---- Body summary list -------------------------------------
    JsonWriter.Begin(sb, '[');
    bool firstBody = true;
    foreach (var b in bodyList) {
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
    foreach (var b in bodyList) {
      WriteAtmGridForBody(sb, archive, b.name, ref firstGrid);
      WriteLtsGridForBody(sb, archive, b.name, ref firstGrid);
    }
    JsonWriter.End(sb, ']');

    JsonWriter.End(sb, ']');
  }

  private static void CountSubjects(IScienceArchive archive, string bodyName,
                                    ref int possible, ref int archived) {
    var layers = AtmosphericProfileExperiment.LayersFor(bodyName);
    if (layers != null) {
      foreach (var l in layers) {
        possible++;
        var key = new SubjectKey(
            AtmosphericProfileExperiment.ExperimentId, bodyName, l.name);
        if (archive != null && archive.TryGet(key.ToString(), out _)) archived++;
      }
    }
    foreach (var (situation, slice) in
             LongTermStudyExperiment.AllSubjectsFor(bodyName)) {
      possible++;
      var key = new SubjectKey(
          LongTermStudyExperiment.ExperimentId,
          bodyName, situation.ToString(), slice);
      if (archive != null && archive.TryGet(key.ToString(), out _)) archived++;
    }
  }

  private static void WriteAtmGridForBody(
      StringBuilder sb, IScienceArchive archive, string bodyName, ref bool first) {
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
      StringBuilder sb, IScienceArchive archive, string bodyName, ref bool first) {
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
      StringBuilder sb, IScienceArchive archive, SubjectKey key,
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
    if (archive != null
        && archive.TryGet(key.ToString(), out var record)
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
