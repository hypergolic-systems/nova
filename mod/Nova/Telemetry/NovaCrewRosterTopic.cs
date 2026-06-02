using System.Collections.Generic;
using System.Text;
using Dragonglass.Telemetry.Topics;
using Nova.Core.Telemetry;
using UnityEngine;

namespace Nova.Telemetry;

// Per-vessel crew roster topic. Drives the CREW section in the side
// rack: lists every kerbal aboard, grouped by the part they occupy.
//
// MonoBehaviour attached to the Vessel's GameObject by
// NovaSubscriptionManager when a `NovaCrewRoster/<vesselGuid>`
// subscribe signal arrives. Lifetime tied to the Vessel.
//
// Wire format (positional):
//   [vesselId, [[partId, name, traitChar, gender, veteran], ...]]
//
// `traitChar` collapses the player-facing trait string ("Pilot",
// "Engineer", "Scientist", "Tourist") to a single letter on the wire
// — the UI maps it back. Anything unrecognised collapses to '?'.
//
// Cadence: poll-and-compare each Update; MarkDirty only on change.
// Crew membership / assignment is a step function in ordinary play
// (EVA, crew transfer, dock, vessel split). The broadcaster's flush
// cadence handles the rest.
public sealed class NovaCrewRosterTopic : Topic {
  private const string LogPrefix = "[Nova/Telemetry] ";

  private Vessel _vessel;
  private string _vesselGuid;
  private string _name;

  // Stable in-memory roster used for diffing + emission. List rather
  // than HashSet so we preserve part-order for diff sensitivity (the
  // wire frame is positional and a stable order keeps Δ small).
  private readonly List<CrewRosterFormatter.KerbalEntry> _entries
      = new List<CrewRosterFormatter.KerbalEntry>();

  // Scratch buffer for the freshly-sampled roster — compared against
  // _entries to decide whether to MarkDirty.
  private readonly List<CrewRosterFormatter.KerbalEntry> _sample
      = new List<CrewRosterFormatter.KerbalEntry>();

  public override string Name => _name;

  protected override void OnEnable() {
    _vessel = GetComponent<Vessel>();
    if (_vessel == null) {
      Debug.LogWarning(LogPrefix + "NovaCrewRosterTopic attached to non-Vessel GameObject; disabling");
      enabled = false;
      return;
    }
    _vesselGuid = _vessel.id.ToString("D");
    _name = "NovaCrewRoster/" + _vesselGuid;
    base.OnEnable();
    SampleAndUpdate(forceEmit: true);
  }

  private void Update() {
    SampleAndUpdate(forceEmit: false);
  }

  private void SampleAndUpdate(bool forceEmit) {
    _sample.Clear();
    if (_vessel.parts != null) {
      for (int i = 0; i < _vessel.parts.Count; i++) {
        var p = _vessel.parts[i];
        if (p == null || p.protoModuleCrew == null) continue;
        var partId = p.persistentId.ToString();
        for (int c = 0; c < p.protoModuleCrew.Count; c++) {
          var pcm = p.protoModuleCrew[c];
          if (pcm == null) continue;
          _sample.Add(new CrewRosterFormatter.KerbalEntry {
            PartId    = partId,
            Name      = pcm.name ?? "",
            TraitChar = CrewRosterFormatter.TraitChar(pcm.trait),
            Gender    = (int)pcm.gender,
            Veteran   = pcm.veteran,
          });
        }
      }
    }

    bool changed = forceEmit || !SameRoster(_entries, _sample);
    if (!changed) return;

    _entries.Clear();
    _entries.AddRange(_sample);

    MarkDirty();
  }

  private static bool SameRoster(
      List<CrewRosterFormatter.KerbalEntry> a,
      List<CrewRosterFormatter.KerbalEntry> b) {
    if (a.Count != b.Count) return false;
    for (int i = 0; i < a.Count; i++) {
      var x = a[i];
      var y = b[i];
      if (x.PartId    != y.PartId)    return false;
      if (x.Name      != y.Name)      return false;
      if (x.TraitChar != y.TraitChar) return false;
      if (x.Gender    != y.Gender)    return false;
      if (x.Veteran   != y.Veteran)   return false;
    }
    return true;
  }

  public override void WriteData(StringBuilder sb) {
    CrewRosterFormatter.Write(sb, _vesselGuid, _entries);
  }
}
