using System.Text;
using Nova.Core.Components.Science;
using Nova.Core.Persistence.Protos;

namespace Nova.Core.Telemetry;

// Per-storage data-storage wire frame.
//
// Wire format:
//   [partId,
//    displayedBytes, capacityBytes, fileCount,
//    [ [<ScienceFile fields...>], ... ]
//   ]
//
// ScienceFile inner shape:
//   [subjectId, experimentId, fidelity, producedAt, instrument,
//    recordedMinAltM, recordedMaxAltM,
//    startUt, endUt, sliceDurationSeconds]
//
// `displayedBytes` lerps with file fidelity (Σ fidelity × size). The
// reservation total (used-bytes) is intentionally not exposed:
// capacity gating is server-side; the player only sees collected.
//
// Direct-measurement files store their fidelity directly. Interpolated
// files (sliceDurationSeconds > 0) derive fidelity from start/end UT
// against `simNowUt`, so save-cli, unloaded vessels, and closed-tab
// UIs all see the same live-climbing value.
public static class StorageFormatter {
  public static void Write(StringBuilder sb, uint partId, DataStorage storage, double simNowUt) {
    JsonWriter.Begin(sb, '[');
    bool first = true;

    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteUintAsString(sb, partId);

    long displayed = storage?.DisplayedBytes ?? 0;
    long capacity  = storage?.CapacityBytes  ?? 0;
    int  count     = storage?.Files.Count    ?? 0;

    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteDouble(sb, displayed);
    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteDouble(sb, capacity);
    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteDouble(sb, count);

    JsonWriter.Sep(sb, ref first);
    JsonWriter.Begin(sb, '[');
    bool firstFile = true;
    if (storage != null) {
      foreach (var file in storage.Files) {
        JsonWriter.Sep(sb, ref firstFile);
        WriteFile(sb, file, simNowUt);
      }
    }
    JsonWriter.End(sb, ']');

    JsonWriter.End(sb, ']');
  }

  private static void WriteFile(StringBuilder sb, ScienceFile file, double simNowUt) {
    JsonWriter.Begin(sb, '[');
    bool ff = true;

    JsonWriter.Sep(sb, ref ff);
    JsonWriter.WriteString(sb, file.SubjectId ?? "");
    JsonWriter.Sep(sb, ref ff);
    JsonWriter.WriteString(sb, file.ExperimentId ?? "");
    JsonWriter.Sep(sb, ref ff);
    JsonWriter.WriteDouble(sb, ComputeLiveFidelity(file, simNowUt));
    JsonWriter.Sep(sb, ref ff);
    JsonWriter.WriteDouble(sb, file.ProducedAt);
    JsonWriter.Sep(sb, ref ff);
    JsonWriter.WriteString(sb, file.Instrument ?? "");
    JsonWriter.Sep(sb, ref ff);
    JsonWriter.WriteDouble(sb, file.RecordedMinAltM);
    JsonWriter.Sep(sb, ref ff);
    JsonWriter.WriteDouble(sb, file.RecordedMaxAltM);
    JsonWriter.Sep(sb, ref ff);
    JsonWriter.WriteDouble(sb, file.StartUt);
    JsonWriter.Sep(sb, ref ff);
    JsonWriter.WriteDouble(sb, file.EndUt);
    JsonWriter.Sep(sb, ref ff);
    JsonWriter.WriteDouble(sb, file.SliceDurationSeconds);

    JsonWriter.End(sb, ']');
  }

  private static double ComputeLiveFidelity(ScienceFile file, double nowUt) {
    if (file.SliceDurationSeconds > 0) {
      double covered = System.Math.Min(nowUt, file.EndUt) - file.StartUt;
      return System.Math.Min(1.0, System.Math.Max(0.0, covered / file.SliceDurationSeconds));
    }
    return file.Fidelity;
  }
}
