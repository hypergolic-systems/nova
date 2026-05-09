using System.Linq;
using System.Text;
using Dragonglass.Telemetry.Topics;
using KSP.UI.Screens;
using Nova.Components;
using Nova.Core.Components;
using UnityEngine;

namespace Nova.Telemetry;

// Editor-scene parallel of NovaVesselStructureTopic — emits the part
// tree of the current ShipConstruct so the editor's Vessel window can
// list every part with its Nova system tags. Single instance (no
// per-id routing) since KSP's editor only ever holds one ShipConstruct
// at a time; the wire `shipId` is the constant `"editor"`.
//
// Lives on the persistent Dragonglass.Telemetry host (attached by
// NovaTelemetryAddon, same as NovaSceneTopic). Outside the editor
// scene, WriteData emits an empty parts list and a blank ship name —
// the topic stays subscribed but is effectively idle.
//
// Dirty triggers come from the engine, not from polling: KSP fires
// `GameEvents.onEditorShipModified` on every structural change (part
// attach, detach, decouple, undo/redo) and `onEditorLoad` /
// `onEditorRestart` for whole-ship swaps. setTankCustom does *not*
// dirty this topic — the per-part tag set doesn't change when a
// tank's resource mix changes (the `tank` tag tracks component
// presence, not contents). Per-part state changes flow through
// NovaPartTopic.
//
// Wire format (matches NovaVesselStructureTopic):
//   [shipId, shipName, [
//     [partId, internalName, displayTitle, parentId|null, [tag, ...]],
//     ...
//   ]]
public sealed class NovaEditorShipStructureTopic : Topic {
  private const string LogPrefix = "[Nova/Telemetry] ";
  private const string ShipId = "editor";

  public override string Name => "NovaEditorShipStructure/" + ShipId;

  protected override void OnEnable() {
    base.OnEnable();
    GameEvents.onEditorShipModified.Add(OnEditorShipModified);
    GameEvents.onEditorLoad.Add(OnEditorLoad);
    GameEvents.onEditorRestart.Add(MarkDirty);
    GameEvents.onEditorStarted.Add(MarkDirty);
    MarkDirty();
  }

  protected override void OnDisable() {
    base.OnDisable();
    GameEvents.onEditorShipModified.Remove(OnEditorShipModified);
    GameEvents.onEditorLoad.Remove(OnEditorLoad);
    GameEvents.onEditorRestart.Remove(MarkDirty);
    GameEvents.onEditorStarted.Remove(MarkDirty);
  }

  private void OnEditorShipModified(ShipConstruct _) => MarkDirty();
  private void OnEditorLoad(ShipConstruct _, CraftBrowserDialog.LoadType __) => MarkDirty();

  public override void WriteData(StringBuilder sb) {
    JsonWriter.Begin(sb, '[');
    bool first = true;

    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteString(sb, ShipId);

    var ship = (HighLogic.LoadedScene == GameScenes.EDITOR && EditorLogic.fetch != null)
      ? EditorLogic.fetch.ship
      : null;

    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteString(sb, ship?.shipName ?? "");

    JsonWriter.Sep(sb, ref first);
    WriteParts(sb, ship);

    JsonWriter.End(sb, ']');
  }

  private static void WriteParts(StringBuilder sb, ShipConstruct ship) {
    JsonWriter.Begin(sb, '[');
    if (ship == null || ship.parts == null) {
      JsonWriter.End(sb, ']');
      return;
    }
    bool first = true;
    for (int i = 0; i < ship.parts.Count; i++) {
      var part = ship.parts[i];
      if (part == null) continue;
      JsonWriter.Sep(sb, ref first);
      WritePart(sb, part);
    }
    JsonWriter.End(sb, ']');
  }

  private static void WritePart(StringBuilder sb, Part part) {
    JsonWriter.Begin(sb, '[');
    bool first = true;

    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteUintAsString(sb, part.persistentId);

    var internalName = part.partInfo?.name ?? "";
    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteString(sb, internalName);

    var title = part.partInfo?.title;
    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteString(sb, string.IsNullOrEmpty(title) ? internalName : title);

    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteNullableUintAsString(sb, part.parent?.persistentId);

    JsonWriter.Sep(sb, ref first);
    WriteTags(sb, part);

    JsonWriter.End(sb, ']');
  }

  private static void WriteTags(StringBuilder sb, Part part) {
    JsonWriter.Begin(sb, '[');
    var components = part.Modules?
      .OfType<NovaPartModule>()
      .Where(m => m.Components != null)
      .SelectMany(m => m.Components);
    if (components == null) {
      JsonWriter.End(sb, ']');
      return;
    }
    var tags = SystemTags.For(components);
    bool first = true;
    foreach (var tag in tags) {
      JsonWriter.Sep(sb, ref first);
      JsonWriter.WriteString(sb, tag);
    }
    JsonWriter.End(sb, ']');
  }
}
