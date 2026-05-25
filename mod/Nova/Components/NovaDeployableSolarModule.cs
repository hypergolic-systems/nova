using System.Linq;
using UnityEngine;

namespace Nova.Components;

public class NovaDeployableSolarModule : NovaSolarModule {

  [KSPField]
  public string animationName;

  [KSPField]
  public bool retractable = true;

  private Animation anim;
  private bool animating;

  public override void OnStart(StartState state) {
    base.OnStart(state);

    // ResolveAnimation falls back to "first model Animation, first clip"
    // when the cfg-named clip isn't on the model — ReStock and other
    // mesh-replacing mods rename clips, so a Nova cfg hardcoding the
    // stock name (e.g. "solarpanels4" → ReStock's "1x6SolarPanels")
    // would otherwise miss. The cfg name is now a preference, not a
    // hard requirement. Returns (null, "") if the model carries no
    // animations at all; downstream null-guards cover anim==null
    // (panel still tracks IsDeployed for power generation, just no
    // visual transition).
    (anim, animationName) = ResolveAnimation(animationName);

    // Mirror stock ModuleDeployablePart.startFSM: pin the wrap mode
    // to ClampForever so when the deploy/retract clip finishes Unity
    // leaves the AnimationState alive at the end pose. The default
    // `Once` disables the state the moment normalizedTime hits the
    // sentinel, and `normalizedTime` doesn't reliably read that
    // sentinel across the disable boundary — which leaves
    // `animating` stuck true and the deploy flags never flip.
    if (anim != null) {
      anim[animationName].wrapMode = WrapMode.ClampForever;
    }

    // Surface the retractable flag to the virtual component so the
    // UI can pick it up via NovaPartTopic — drives whether the row
    // gets a toggle (retractable) or a one-shot open button.
    solarPanel.IsRetractable = retractable;

    // Three OnStart cases:
    //   1. Loaded from save (LoadedFromSave) — use the proto value
    //      already in solarPanel.IsDeployed.
    //   2. Editor scene — panels render extended (matches the prior
    //      `state == StartState.Editor` shortcut, no surprise in the VAB).
    //   3. Fresh launch — start retracted (stock convention; the prior
    //      `[KSPField(isPersistant)] isExtended` defaulted to false).
    // The proto save path bypasses the stock ConfigNode tree, so a
    // pure `isExtended` KSPField wouldn't round-trip — that was the
    // bug behind "panel saved deployed, reloaded retracted".
    bool deployed = solarPanel.LoadedFromSave
        ? solarPanel.IsDeployed
        : state == StartState.Editor;
    solarPanel.IsDeployed = deployed;
    SetAnimationPosition(deployed ? 1f : 0f);

    if (state != StartState.Editor) {
      var vesselModule = vessel.FindVesselModuleImplementing<NovaVesselModule>();
      if (vesselModule != null)
        vesselModule.InvalidateSolarData();
    }
  }

  // Per-panel deploy. Called from NovaPartTopic on `setSolarDeployed`;
  // not exposed in the stock PAW (Nova owns its player-facing UI). Stock
  // ModuleDeployablePart walks part.symmetryCounterparts here; Nova
  // does not — each panel's Extend/Retract affects only the panel it
  // was called on, so the PWR UI produces single-panel actions. Deploy
  // a four-way symmetry group via the SOLAR subgroup bulk control.
  public void Extend() {
    if (animating || solarPanel.IsDeployed) return;
    if (anim == null) {
      // No animation available (cfg/mesh mismatch). Flip state
      // instantly so deploy logic still responds; visual stays
      // wherever the mesh defaulted.
      solarPanel.IsDeployed = true;
      OnDeployStateChanged();
      return;
    }

    // SetAnimationPosition leaves the AnimationState disabled (anim.Stop
    // at the end). Unity's legacy Play() doesn't reliably re-enable a
    // stopped state, so flip enabled/weight ourselves before playing —
    // matches the pattern in stock ModuleDeployablePart.
    anim[animationName].normalizedTime = 0f;
    anim[animationName].speed = HighLogic.LoadedSceneIsEditor ? 5f : 1f;
    anim[animationName].enabled = true;
    anim[animationName].weight = 1f;
    anim.Play(animationName);
    animating = true;
  }

  public void Retract() {
    if (animating || !solarPanel.IsDeployed) return;
    if (!retractable && !HighLogic.LoadedSceneIsEditor) return;
    if (anim == null) {
      solarPanel.IsDeployed = false;
      OnDeployStateChanged();
      return;
    }

    anim[animationName].normalizedTime = 1f;
    anim[animationName].speed = HighLogic.LoadedSceneIsEditor ? -5f : -1f;
    anim[animationName].enabled = true;
    anim[animationName].weight = 1f;
    anim.Play(animationName);
    animating = true;
  }

  public void FixedUpdate() {
    if (!animating || anim == null) return;

    var time = anim[animationName].normalizedTime;
    if (anim[animationName].speed > 0 && time >= 1f) {
      anim.Stop(animationName);
      SetAnimationPosition(1f);
      animating = false;
      solarPanel.IsDeployed = true;
      OnDeployStateChanged();
    } else if (anim[animationName].speed < 0 && time <= 0f) {
      anim.Stop(animationName);
      SetAnimationPosition(0f);
      animating = false;
      solarPanel.IsDeployed = false;
      OnDeployStateChanged();
    }
  }

  private void OnDeployStateChanged() {
    if (vessel != null) {
      var vesselModule = vessel.FindVesselModuleImplementing<NovaVesselModule>();
      if (vesselModule != null)
        vesselModule.InvalidateSolarData();
    }
  }

  private void SetAnimationPosition(float time) {
    if (anim == null) return;
    anim[animationName].normalizedTime = time;
    anim[animationName].speed = 0f;
    anim[animationName].enabled = true;
    anim[animationName].weight = 1f;
    anim.Sample();
    anim.Stop(animationName);
  }
}
