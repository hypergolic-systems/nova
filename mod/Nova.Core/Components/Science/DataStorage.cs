using System.Collections.Generic;
using Nova.Core.Persistence.Protos;

namespace Nova.Core.Components.Science;

// On-vessel storage for ScienceFile blobs. Capacity is in bytes; the
// per-file byte cost lives at deposit time on the caller (which knows
// the experiment that produced the file). Capacity check rejects
// over-fills; UI / debug logging surfaces the drop.
public class DataStorage : VirtualComponent {
  public long CapacityBytes;

  // Total bytes currently used by files in this storage. Persisted
  // implicitly via Files; not a separate field.
  public long UsedBytes;

  public List<ScienceFile> Files = new();

  public long FreeBytes => CapacityBytes - UsedBytes;

  public bool CanDeposit(long sizeBytes) => sizeBytes >= 0 && sizeBytes <= FreeBytes;

  // Lookup an existing file by its subject key. Returns null when no
  // file for that subject lives here yet.
  public ScienceFile FindBySubject(string subjectId) {
    if (string.IsNullOrEmpty(subjectId)) return null;
    foreach (var f in Files)
      if (f.SubjectId == subjectId) return f;
    return null;
  }

  public bool HasSubject(string subjectId) => FindBySubject(subjectId) != null;

  // Upsert by subject id. If a file already exists for this subject,
  // updates it in-place (no extra bytes consumed, the slot was reserved
  // when the file was first created). Otherwise capacity-checks and
  // appends.
  //
  // Returns true on success. False = no existing file AND not enough
  // capacity for a new one.
  public bool Upsert(ScienceFile file, long sizeBytes) {
    for (int i = 0; i < Files.Count; i++) {
      if (Files[i].SubjectId == file.SubjectId) {
        Files[i] = file;
        return true;
      }
    }
    if (!CanDeposit(sizeBytes)) return false;
    Files.Add(file);
    UsedBytes += sizeBytes;
    return true;
  }

  // Remove the file for `subjectId`, freeing its reserved bytes.
  // Used when an experiment toggle is re-enabled mid-regime — the
  // prior partial observation is discarded so the fresh observation
  // starts with no inherited bounds. Returns true if a file was
  // removed.
  public bool RemoveBySubject(string subjectId) {
    if (string.IsNullOrEmpty(subjectId)) return false;
    for (int i = 0; i < Files.Count; i++) {
      if (Files[i].SubjectId == subjectId) {
        UsedBytes -= FileSizeFor(Files[i].ExperimentId);
        if (UsedBytes < 0) UsedBytes = 0;
        Files.RemoveAt(i);
        return true;
      }
    }
    return false;
  }

  public override void LoadStructure(PartStructure ps) {
    if (ps.DataStorage == null) return;
    CapacityBytes = ps.DataStorage.CapacityBytes;
  }

  public override void SaveStructure(PartStructure ps) {
    ps.DataStorage = new DataStorageStructure { CapacityBytes = CapacityBytes };
  }

  public override void Load(PartState state) {
    if (state.DataStorage == null) return;
    Files.Clear();
    UsedBytes = 0;
    foreach (var f in state.DataStorage.Files) {
      Files.Add(f);
      UsedBytes += FileSizeFor(f.ExperimentId);
    }
  }

  public override void Save(PartState state) {
    var s = new DataStorageState();
    s.Files.AddRange(Files);
    state.DataStorage = s;
  }

  // Hardcoded per-experiment byte cost. Mirrors the constants on each
  // experiment class. Eight bytes lost in space, balance later if it
  // matters.
  private static long FileSizeFor(string experimentId) {
    return experimentId switch {
      Nova.Core.Science.AtmosphericProfileExperiment.ExperimentId
        => Nova.Core.Science.AtmosphericProfileExperiment.FileSizeBytes,
      Nova.Core.Science.LongTermStudyExperiment.ExperimentId
        => Nova.Core.Science.LongTermStudyExperiment.FileSizeBytes,
      _ => 1_000,
    };
  }

  public override VirtualComponent Clone() {
    var c = new DataStorage {
      CapacityBytes = CapacityBytes,
      UsedBytes = UsedBytes,
    };
    c.Files.AddRange(Files);
    return c;
  }
}
