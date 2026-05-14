using System.Collections.Generic;
using System.Text;

namespace Nova.Core.Telemetry;

// Per-vessel structure wire frame. Used by NovaVesselStructure/<guid>
// and (with a synthetic id) by NovaEditorShipStructure.
//
// Wire format (positional):
//   [vesselId, vesselName, [
//     [partId, internalName, displayTitle, parentId|null],
//     ...
//   ]]
//
// `displayTitle` is the player-facing part title (from the part's
// AvailablePart on the mod side, or from the part's `.cfg` `title`
// value on the sim side). When no title is known, the caller passes
// the internal name.
//
// Per-view dispatch in the UI: each view subscribes to NovaPart/<id>
// for every part on the vessel and switches on the components present
// in each part's frame — there's no per-part tag filter on the wire.
public static class VesselStructureFormatter {
  public struct PartEntry {
    public uint   PartId;
    public string InternalName;
    public string DisplayTitle;
    public uint?  ParentId;
  }

  public static void Write(StringBuilder sb,
      string vesselGuid, string vesselName, IEnumerable<PartEntry> parts) {
    JsonWriter.Begin(sb, '[');
    bool first = true;

    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteString(sb, vesselGuid);

    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteString(sb, vesselName ?? "");

    JsonWriter.Sep(sb, ref first);
    JsonWriter.Begin(sb, '[');
    bool firstPart = true;
    if (parts != null) {
      foreach (var p in parts) {
        JsonWriter.Sep(sb, ref firstPart);
        WritePart(sb, p);
      }
    }
    JsonWriter.End(sb, ']');

    JsonWriter.End(sb, ']');
  }

  private static void WritePart(StringBuilder sb, PartEntry p) {
    JsonWriter.Begin(sb, '[');
    bool first = true;

    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteUintAsString(sb, p.PartId);

    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteString(sb, p.InternalName ?? "");

    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteString(sb, p.DisplayTitle ?? p.InternalName ?? "");

    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteNullableUintAsString(sb, p.ParentId);

    JsonWriter.End(sb, ']');
  }
}
