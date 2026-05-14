using System.Collections.Generic;
using System.Text;
using Dragonglass.Telemetry.Topics;
using Nova.Core.Persistence.Protos;
using Nova.Core.Science;
using Nova.Core.Telemetry;
using Nova.Science;
using UnityEngine;

namespace Nova.Telemetry;

// Singleton topic carrying the KSC-side science archive — every record
// the player has transmitted home, plus the full possible-subject
// inventory so the UI can render unstudied gaps next to archived
// results.
//
// Wire format (positional):
//   [
//     // Per-body summary. Order: FlightGlobals.Bodies (solar-system
//     // depth-first). parentName is "" for the Sun, otherwise the
//     // orbital parent. Counts are pre-rolled so the UI's body list
//     // renders without scanning subjects.
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
//
// Read-only — no inbound ops in v1.
public sealed class NovaScienceArchiveTopic : Topic {
  public static NovaScienceArchiveTopic Instance { get; private set; }

  public override string Name => "NovaScienceArchive";

  protected override void OnEnable() {
    Instance = this;
    NovaScienceArchive.Changed += OnArchiveChanged;
    GameEvents.onLevelWasLoaded.Add(OnLevelLoaded);
    base.OnEnable();
    MarkDirty();
  }

  protected override void OnDisable() {
    NovaScienceArchive.Changed -= OnArchiveChanged;
    GameEvents.onLevelWasLoaded.Remove(OnLevelLoaded);
    base.OnDisable();
    if (Instance == this) Instance = null;
  }

  private void OnArchiveChanged() {
    MarkDirty();
  }

  // Body roster only populates fully once KSP transitions out of the
  // loading scene. Re-emit on every level load so subscribers see the
  // ready roster without polling.
  private void OnLevelLoaded(GameScenes scene) {
    MarkDirty();
  }

  public override void WriteData(StringBuilder sb) {
    ScienceArchiveFormatter.Write(sb, NovaScienceArchive.Instance, ResolveBodies());
  }

  // ---- Body roster ---------------------------------------------

  // Solar-system order, each body paired with its orbital parent's
  // name ("" for the Sun, which references itself in stock).
  // Defensive: the topic broadcaster ticks during the KSP loading
  // scene, before `FlightGlobals.Bodies` is fully populated. Skip
  // bodies whose name field hasn't been wired yet rather than NRE.
  private static IEnumerable<(string name, string parent)> ResolveBodies() {
    var bodies = FlightGlobals.Bodies;
    if (bodies == null) yield break;
    for (int i = 0; i < bodies.Count; i++) {
      var body = bodies[i];
      if (body == null) continue;
      string name = body.bodyName;
      if (string.IsNullOrEmpty(name)) continue;
      string parent = "";
      var refBody = body.referenceBody;
      if (refBody != null && refBody != body) {
        parent = refBody.bodyName ?? "";
      }
      yield return (name, parent);
    }
  }

}
