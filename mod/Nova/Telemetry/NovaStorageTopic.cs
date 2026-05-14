using System.Collections.Generic;
using System.Linq;
using System.Text;
using Dragonglass.Telemetry.Topics;
using Nova.Components;
using Nova.Core.Components.Science;
using Nova.Core.Persistence.Protos;
using Nova.Core.Telemetry;
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
    StorageFormatter.Write(sb, _part.persistentId, ResolveStorage(), Planetarium.GetUniversalTime());
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

}
