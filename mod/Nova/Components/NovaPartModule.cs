using System;
using System.Collections.Generic;
using System.Linq;
using Nova.Core.Components;
using Nova.Telemetry;
using UnityEngine;

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

  /// <summary>
  /// Resolve a deploy animation on the part. Prefers the cfg-named
  /// clip; falls back to the first Animation component on the model
  /// using its first clip's name. Returns `(null, "")` if the model
  /// carries no animations at all (fixed/non-deployable part — caller
  /// should treat as deploy-state-only with no visual transition).
  ///
  /// The fallback is what keeps mesh-replacing mods (ReStock,
  /// RealAntennas, etc.) working without per-mod cfg edits: those
  /// rewrite the model and rename the clip, so a Nova cfg that hardcodes
  /// the stock name (e.g. "solarpanels4" → ReStock's "1x6SolarPanels")
  /// would miss with FindModelAnimators(name) alone. Without the
  /// fallback, the Extend()/Retract() helpers hit their `anim == null`
  /// branch and the panel flips IsDeployed instantly with no visual
  /// transition — which was the "produces EC but never animates"
  /// regression. Stock ModuleDeployablePart.FindAnimations does the
  /// same first-Animation fallback for the same reason.
  /// </summary>
  protected (Animation anim, string clipName) ResolveAnimation(string preferredName) {
    if (!string.IsNullOrEmpty(preferredName)) {
      var named = part.FindModelAnimator(preferredName);
      if (named != null) return (named, preferredName);
    }
    var anims = part.FindModelAnimators();
    foreach (var a in anims) {
      foreach (AnimationState st in a) {
        if (st?.clip != null) return (a, st.clip.name);
      }
    }
    return (null, "");
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
