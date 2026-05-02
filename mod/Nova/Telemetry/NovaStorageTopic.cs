using System.Collections.Generic;
using System.Linq;
using System.Text;
using Dragonglass.Telemetry.Topics;
using Nova.Components;
using Nova.Core.Components.Science;
using Nova.Core.Persistence.Protos;
using UnityEngine;

namespace Nova.Telemetry;

// Per-storage data-storage topic. Lives on parts that host a
// `DataStorage` virtual component. Publishes the storage's headline
// counters (displayed bytes / capacity / file count) plus the inline
// list of `ScienceFile` records.
//
// `displayedBytes` lerps with file fidelity (Σ fidelity × size) so the
// gauge climbs smoothly as observations accrue. The reservation total
// (`UsedBytes` C#-side) is intentionally not exposed: capacity gating
// is server-side; the player only sees the science-collected number.
//
// Wire format:
//   [partId,
//    displayedBytes, capacityBytes, fileCount,
//    [ [<ScienceFile fields...>], ... ]
//   ]
//
// ScienceFile inner shape mirrors `NovaScienceFileFrame`:
//   [subjectId, experimentId, fidelity, producedAt, instrument,
//    recordedMinAltM, recordedMaxAltM,
//    startUt, endUt, sliceDurationSeconds]
//
// No inbound ops yet — file actions (transmit / discard / etc.) will
// land here when those flows ship.
public sealed class NovaStorageTopic : Topic {
  private const string LogPrefix = "[Nova/Telemetry] ";

  private Part _part;
  private string _name;

  private static readonly Dictionary<uint, NovaStorageTopic> _byPart
      = new Dictionary<uint, NovaStorageTopic>();

  public override string Name => _name;

  protected override void OnEnable() {
    _part = GetComponent<Part>();
    if (_part == null) {
      Debug.LogWarning(LogPrefix + "NovaStorageTopic attached to non-Part GameObject; disabling");
      enabled = false;
      return;
    }
    _name = "NovaStorage/" + _part.persistentId;
    _byPart[_part.persistentId] = this;
    base.OnEnable();
    MarkDirty();
  }

  protected override void OnDisable() {
    base.OnDisable();
    if (_part != null) _byPart.Remove(_part.persistentId);
  }

  public static void MarkPartDirty(uint partPersistentId) {
    if (_byPart.TryGetValue(partPersistentId, out var topic) && topic != null) {
      topic.MarkDirty();
    }
  }

  public override void WriteData(StringBuilder sb) {
    JsonWriter.Begin(sb, '[');
    bool first = true;

    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteUintAsString(sb, _part.persistentId);

    var storage = ResolveStorage();

    long displayed = storage?.DisplayedBytes ?? 0;
    long capacity  = storage?.CapacityBytes  ?? 0;
    int  count     = storage?.Files.Count    ?? 0;

    WriteNum(sb, displayed, ref first);
    WriteNum(sb, capacity,  ref first);
    WriteNum(sb, count,     ref first);

    // Inline file list. Typical storages hold tens of files; the 10 Hz
    // cadence keeps bandwidth modest. Interpolated files recompute
    // fidelity from `now` against start/end UT so the UI sees a
    // live-climbing value.
    double simNow = Planetarium.GetUniversalTime();
    JsonWriter.Sep(sb, ref first);
    JsonWriter.Begin(sb, '[');
    bool firstFile = true;
    if (storage != null) {
      foreach (var file in storage.Files) {
        JsonWriter.Sep(sb, ref firstFile);
        JsonWriter.Begin(sb, '[');
        bool ff = true;
        JsonWriter.Sep(sb, ref ff);
        JsonWriter.WriteString(sb, file.SubjectId ?? "");
        JsonWriter.Sep(sb, ref ff);
        JsonWriter.WriteString(sb, file.ExperimentId ?? "");
        double liveFidelity = ComputeLiveFidelity(file, simNow);
        WriteNum(sb, liveFidelity, ref ff);
        WriteNum(sb, file.ProducedAt, ref ff);
        JsonWriter.Sep(sb, ref ff);
        JsonWriter.WriteString(sb, file.Instrument ?? "");
        WriteNum(sb, file.RecordedMinAltM, ref ff);
        WriteNum(sb, file.RecordedMaxAltM, ref ff);
        WriteNum(sb, file.StartUt, ref ff);
        WriteNum(sb, file.EndUt, ref ff);
        WriteNum(sb, file.SliceDurationSeconds, ref ff);
        JsonWriter.End(sb, ']');
      }
    }
    JsonWriter.End(sb, ']');

    JsonWriter.End(sb, ']');
  }

  private DataStorage ResolveStorage() {
    if (_part == null) return null;
    if (_part.vessel != null) {
      var vm = _part.vessel.GetComponent<NovaVesselModule>();
      var c = vm?.Virtual?.GetComponents(_part.persistentId);
      if (c != null) return c.OfType<DataStorage>().FirstOrDefault();
    }
    var modules = _part.Modules?.OfType<NovaPartModule>();
    return modules?
      .Where(m => m.Components != null)
      .SelectMany(m => m.Components)
      .OfType<DataStorage>()
      .FirstOrDefault();
  }

  // Direct-measurement files store their fidelity directly (updated
  // every tick by the experiment). Interpolated files derive fidelity
  // from start/end UT against now, so save-cli / unloaded vessels /
  // closed-tab UIs see the same value.
  private static double ComputeLiveFidelity(ScienceFile file, double nowUT) {
    if (file.SliceDurationSeconds > 0) {
      double covered = System.Math.Min(nowUT, file.EndUt) - file.StartUt;
      return System.Math.Min(1.0, System.Math.Max(0.0, covered / file.SliceDurationSeconds));
    }
    return file.Fidelity;
  }

  private static void WriteNum(StringBuilder sb, double value, ref bool first) {
    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteDouble(sb, value);
  }
}
