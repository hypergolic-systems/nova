using System;
using System.Collections.Generic;
using System.Linq;
using Nova.Core.Components;
using Nova.Telemetry;

namespace Nova.Components;

public class NovaPartModule : PartModule {

  internal IEnumerable<VirtualComponent> Components;

  [KSPField(isPersistant = false, guiActive = true, guiActiveEditor = true, guiName = "Virtual Components")]
  public string virtualComponentStatus = "";

  public override void OnLoad(ConfigNode node) {
    base.OnLoad(node);
  }

  public override void OnSave(ConfigNode node) {
    base.OnSave(node);
  }

  public override void OnStart(StartState state) {
    base.OnStart(state);

    if (state == StartState.Editor) {
      OnStartEditor();
    } else {
      OnStartFlight();
    }
    virtualComponentStatus = $"{string.Join(", ", Components.Select(c => c.Name))} ({(state == StartState.Editor ? "Editor" : "Flight")})";
  }

  /// <summary>
  /// Called by `NovaSaveLoader.ApplyVesselState` after `VirtualComponent.Load`
  /// has updated every component on this part. Override to sync KSP-side
  /// rendered/animated state with the freshly-restored VirtualComponent
  /// state — engine FX, deployment animation pose, RTG glow, light
  /// emissives, etc. Per-frame Update() handles steady-state visuals;
  /// this hook handles "the underlying state just changed, refresh what
  /// Update wouldn't catch this frame" — e.g. an engine that was firing
  /// before the load is now Active=false but its FX latch on until
  /// something explicitly disables them.
  /// </summary>
  public virtual void OnNovaStateRestored() {}

  internal ConfigNode GetPrefabModuleConfig() {
    return part.partInfo.partConfig.GetNodes("MODULE")
      .FirstOrDefault(n => n.GetValue("name") == GetType().Name);
  }

  private void OnStartEditor() {
    var moduleConfig = GetPrefabModuleConfig()
      ?? throw new Exception($"No prefab MODULE config for {GetType().Name}");
    var cmp = ComponentFactory.Create(moduleConfig);
    Components = new List<VirtualComponent> { cmp };
    // Re-broadcast the editor structure topic now that this part has
    // its Components populated. `onEditorShipModified` fires before
    // module OnStart for the first part the player attaches, so a
    // pod placed alone (e.g. just a Mk1) would otherwise emit with
    // empty Components and miss its `tank` tag — never showing up in
    // the editor's Tanks view until something else dirtied the topic.
    NovaEditorShipStructureTopic.MarkInstanceDirty();
  }

  private void OnStartFlight() {
    var mod = vessel.FindVesselModuleImplementing<NovaVesselModule>();
    if (mod?.Virtual == null)
      throw new InvalidOperationException(
        $"VirtualVessel not initialized before PartModule.OnStart for {part.partInfo.name} (id={part.persistentId})");

    var components = mod.Virtual.GetComponents(part.persistentId).ToList();
    if (components.Count == 0)
      throw new InvalidOperationException(
        $"No VirtualComponents for part {part.persistentId} ({part.partInfo.name})");

    Components = components;
  }
}
