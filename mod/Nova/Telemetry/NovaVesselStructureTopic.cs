using System.Collections.Generic;
using System.Text;
using Dragonglass.Telemetry.Topics;
using Nova.Components;
using Nova.Core.Components;
using UnityEngine;

namespace Nova.Telemetry;

// Per-vessel structure topic: vessel id, name, and the list of parts
// with their parent links and Nova system tags. Low-frequency — dirty
// only on topology change (NovaVesselModule signals via
// MarkVesselDirty). The UI uses tags as a coarse filter so views like
// PowerView only subscribe to NovaPart/<id> for parts that actually
// participate in their subsystem.
//
// MonoBehaviour attached to the Vessel's GameObject by
// NovaSubscriptionManager when a `NovaVesselStructure/<id>` subscribe
// signal arrives. Lifetime tied to the Vessel — Unity destroys the
// component when the Vessel is destroyed (unload, decouple, dock).
//
// Wire format (positional array):
//   [vesselId, vesselName, [
//     [partId, partName, parentId|null, [tag, ...]],
//     ...
//   ]]
public sealed class NovaVesselStructureTopic : Topic {
  private const string LogPrefix = "[Nova/Telemetry] ";

  private Vessel _vessel;
  private NovaVesselModule _vesselModule;
  private string _vesselGuid;
  private string _name;

  // Static index so NovaVesselModule can flag dirtiness without
  // keeping a back-reference to its topic. Keyed by Vessel GUID
  // (string form of `vessel.id`) for parity with Dragonglass's
  // FlightTopic, which is what the UI passes through as vesselId.
  private static readonly Dictionary<string, NovaVesselStructureTopic> _byVessel
      = new Dictionary<string, NovaVesselStructureTopic>();

  public override string Name => _name;

  protected override void OnEnable() {
    _vessel = GetComponent<Vessel>();
    if (_vessel == null) {
      Debug.LogWarning(LogPrefix + "NovaVesselStructureTopic attached to non-Vessel GameObject; disabling");
      enabled = false;
      return;
    }
    _vesselModule = _vessel.GetComponent<NovaVesselModule>();
    _vesselGuid = _vessel.id.ToString("D");
    _name = "NovaVesselStructure/" + _vesselGuid;
    _byVessel[_vesselGuid] = this;
    base.OnEnable();
    MarkDirty();
  }

  protected override void OnDisable() {
    base.OnDisable();
    if (_vesselGuid != null) _byVessel.Remove(_vesselGuid);
  }

  /// <summary>
  /// Called by NovaVesselModule when a topology change has reshuffled
  /// the part tree or component set. Idempotent — the broadcaster
  /// dedupes via the IsDirty flag.
  /// </summary>
  public static void MarkVesselDirty(string vesselGuid) {
    if (_byVessel.TryGetValue(vesselGuid, out var topic) && topic != null) {
      topic.MarkDirty();
    }
  }

  public override void WriteData(StringBuilder sb) {
    JsonWriter.Begin(sb, '[');
    bool first = true;

    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteString(sb, _vesselGuid);

    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteString(sb, _vessel.GetDisplayName() ?? _vessel.vesselName ?? "");

    JsonWriter.Sep(sb, ref first);
    WriteParts(sb);

    JsonWriter.End(sb, ']');
  }

  private void WriteParts(StringBuilder sb) {
    JsonWriter.Begin(sb, '[');
    if (_vesselModule == null || _vesselModule.Virtual == null) {
      JsonWriter.End(sb, ']');
      return;
    }
    var virt = _vesselModule.Virtual;

    bool firstPart = true;
    foreach (var partId in virt.AllPartIds()) {
      JsonWriter.Sep(sb, ref firstPart);
      WritePart(sb, virt, partId);
    }
    JsonWriter.End(sb, ']');
  }

  private static void WritePart(StringBuilder sb, VirtualVessel virt, uint partId) {
    JsonWriter.Begin(sb, '[');
    bool first = true;

    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteUintAsString(sb, partId);

    var internalName = virt.GetPartName(partId) ?? "";
    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteString(sb, internalName);

    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteString(sb, ResolveTitle(partId, internalName));

    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteNullableUintAsString(sb, virt.GetPartParent(partId));

    JsonWriter.Sep(sb, ref first);
    WriteTags(sb, SystemTags.For(virt.GetComponents(partId)));

    JsonWriter.End(sb, ']');
  }

  // Pull the player-facing display title from the live KSP Part (its
  // AvailablePart's `title`). Falls back to the internal partName
  // when no Part GameObject is currently loaded — happens only for
  // unloaded vessels, which the structure topic doesn't handle yet.
  private static string ResolveTitle(uint partId, string fallback) {
    if (FlightGlobals.PersistentLoadedPartIds != null
        && FlightGlobals.PersistentLoadedPartIds.TryGetValue(partId, out var part)
        && part != null
        && part.partInfo != null
        && !string.IsNullOrEmpty(part.partInfo.title)) {
      return part.partInfo.title;
    }
    return fallback;
  }

  private static void WriteTags(StringBuilder sb, List<string> tags) {
    JsonWriter.Begin(sb, '[');
    bool first = true;
    foreach (var tag in tags) {
      JsonWriter.Sep(sb, ref first);
      JsonWriter.WriteString(sb, tag);
    }
    JsonWriter.End(sb, ']');
  }
}
