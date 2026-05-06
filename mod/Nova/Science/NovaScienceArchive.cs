using System;
using System.Collections.Generic;
using Nova.Core.Persistence.Protos;
using Nova.Core.Science;

namespace Nova.Science;

// Game-wide record of received science. Singleton because the archive
// belongs to "the player's career", not any individual vessel or scene
// — multiple vessels transmit into the same record. Hydrated from /
// flushed to SaveFile.science_archive by NovaSaveLoader / NovaSaveBuilder.
//
// Dedup: keep the highest-fidelity record per subject. With current
// IsComplete semantics every transmitted file lands at fidelity 1.0,
// so this is effectively first-arrival-wins; the comparison is kept
// for the day partial transmission ships and partial-then-full upgrade
// paths matter.
public class NovaScienceArchive : IScienceArchive {

  public static NovaScienceArchive Instance { get; private set; } = new();

  // Fired whenever the archive's contents may have changed: a record
  // received, the singleton replaced (Reset), or contents replaced from
  // a save (HydrateFrom). Telemetry topics subscribe to mark-dirty so
  // they don't have to poll. Static + parameterless on purpose — the
  // event survives Instance replacement and subscribers re-snapshot
  // from `Instance` themselves.
  public static event Action Changed;

  private readonly Dictionary<string, ArchivedScienceRecord> bySubject = new();

  // Replace the live singleton. Call when the game's archive context
  // changes (load, scene transition into a new game).
  public static void Reset() {
    Instance = new NovaScienceArchive();
    Changed?.Invoke();
  }

  public void Receive(ScienceFile file,
                      uint sourceVesselPersistentId,
                      string sourceVesselName,
                      double ut) {
    if (file == null || string.IsNullOrEmpty(file.SubjectId)) return;
    if (bySubject.TryGetValue(file.SubjectId, out var existing)
        && existing.File != null
        && existing.File.Fidelity >= file.Fidelity) {
      return;
    }
    bySubject[file.SubjectId] = new ArchivedScienceRecord {
      File = file,
      ReceivedAtUt = ut,
      SourceVesselPersistentId = sourceVesselPersistentId,
      SourceVesselName = sourceVesselName ?? "",
    };
    Changed?.Invoke();
  }

  public IEnumerable<ArchivedScienceRecord> AllRecords() => bySubject.Values;

  public int Count => bySubject.Count;

  public bool TryGet(string subjectId, out ArchivedScienceRecord record)
      => bySubject.TryGetValue(subjectId, out record);

  // Replace contents from a saved proto. Used by NovaSaveLoader after a
  // load completes.
  public void HydrateFrom(ScienceArchive proto) {
    bySubject.Clear();
    if (proto == null) {
      Changed?.Invoke();
      return;
    }
    foreach (var rec in proto.Records) {
      if (rec.File == null || string.IsNullOrEmpty(rec.File.SubjectId)) continue;
      bySubject[rec.File.SubjectId] = rec;
    }
    Changed?.Invoke();
  }

  // Snapshot the live archive for save serialization.
  public ScienceArchive ToProto() {
    var proto = new ScienceArchive();
    foreach (var rec in bySubject.Values) proto.Records.Add(rec);
    return proto;
  }
}
