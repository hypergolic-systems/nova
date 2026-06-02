using System.Collections.Generic;
using System.Text;

namespace Nova.Core.Telemetry;

// Per-vessel crew roster wire frame. Used by NovaCrewRoster/<guid>.
//
// Wire format (positional):
//   [vesselId, [crew]]
//
// where each crew entry is:
//   [partId, name, traitChar, gender, veteran]
//
// `traitChar` is a single-char tag — P=Pilot, E=Engineer, S=Scientist,
// T=Tourist, ?=unknown — kept terse to keep a full-crew vessel's frame
// compact. Trait labels rendered to the player live in the UI.
// `gender` mirrors KSP's ProtoCrewMember.gender / Kerbal.gender
// (0=male, 1=female). `veteran` is the orange-suit founding-four flag.
public static class CrewRosterFormatter {
  public struct KerbalEntry {
    public string PartId;
    public string Name;
    public char   TraitChar;
    public int    Gender;     // 0 / 1
    public bool   Veteran;
  }

  public static void Write(StringBuilder sb,
      string vesselGuid, IReadOnlyList<KerbalEntry> crew) {
    JsonWriter.Begin(sb, '[');
    bool first = true;

    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteString(sb, vesselGuid);

    JsonWriter.Sep(sb, ref first);
    JsonWriter.Begin(sb, '[');
    bool firstCrew = true;
    if (crew != null) {
      for (int i = 0; i < crew.Count; i++) {
        JsonWriter.Sep(sb, ref firstCrew);
        var e = crew[i];
        JsonWriter.Begin(sb, '[');
        bool firstField = true;

        JsonWriter.Sep(sb, ref firstField);
        JsonWriter.WriteString(sb, e.PartId ?? "");
        JsonWriter.Sep(sb, ref firstField);
        JsonWriter.WriteString(sb, e.Name ?? "");
        JsonWriter.Sep(sb, ref firstField);
        sb.Append('"').Append(e.TraitChar).Append('"');
        JsonWriter.Sep(sb, ref firstField);
        sb.Append(e.Gender);
        JsonWriter.Sep(sb, ref firstField);
        sb.Append(e.Veteran ? 1 : 0);

        JsonWriter.End(sb, ']');
      }
    }
    JsonWriter.End(sb, ']');

    JsonWriter.End(sb, ']');
  }

  // Trait-string → single-char map. Matches the in-game Trait string
  // ("Pilot" / "Engineer" / "Scientist" / "Tourist") and the Kerbal
  // proto's `trait` field; anything else collapses to '?'.
  public static char TraitChar(string trait) {
    if (string.IsNullOrEmpty(trait)) return '?';
    switch (trait[0]) {
      case 'P': case 'p': return 'P';
      case 'E': case 'e': return 'E';
      case 'S': case 's': return 'S';
      case 'T': case 't': return 'T';
      default:            return '?';
    }
  }
}
