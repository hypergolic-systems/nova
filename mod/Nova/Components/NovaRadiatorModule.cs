using System.Linq;
using UnityEngine;
using Nova.Core.Components.Thermal;

namespace Nova.Components;

// Radiator part module. Handles both fixed panels and deployable
// folding radiators in one class; deploy mechanics fire only when
// `animationName` is non-empty in the cfg. Drives the part's deploy
// animation directly (we strip stock ModuleDeployableRadiator) and
// syncs the virtual Radiator component's IsDeployed flag on
// animation completion. Fixed panels skip all animation paths and
// stay deployed.
public class NovaRadiatorModule : NovaPartModule {

  // Empty / missing → fixed panel, no animation. Set to the
  // animation clip name on the part model (stock folding rads use
  // "Deploy") to enable deploy mechanics.
  [KSPField]
  public string animationName = "";

  [KSPField]
  public bool retractable = true;

  private Animation anim;
  private bool animating;
  private Radiator radiator;

  // Deployable iff a non-empty animationName is configured. Also
  // surfaced via the Radiator virtual component's IsDeployable for
  // the wire / UI.
  private bool IsDeployable => !string.IsNullOrEmpty(animationName);

  public override void OnStart(StartState state) {
    base.OnStart(state);

    radiator = Components.OfType<Radiator>().FirstOrDefault();
    if (radiator == null) return;
    radiator.IsDeployable = IsDeployable;
    radiator.IsRetractable = retractable;

    // Fixed panel: no animation, always deployed.
    if (!IsDeployable) {
      radiator.IsDeployed = true;
      return;
    }

    // ResolveAnimation falls back to "first model Animation, first clip"
    // when the cfg-named clip isn't on the model — see the same call
    // in NovaDeployableSolarModule for the ReStock-renaming context.
    (anim, animationName) = ResolveAnimation(animationName);

    // ClampForever so Unity holds the end pose after the clip — see
    // NovaDeployableSolarModule for the rationale (Once leaves
    // normalizedTime ambiguous across the disable boundary).
    if (anim != null) {
      anim[animationName].wrapMode = WrapMode.ClampForever;
    }

    // Three OnStart cases — same pattern as NovaDeployableSolarModule:
    //   1. Loaded from save (LoadedFromSave) — use proto value already
    //      in radiator.IsDeployed.
    //   2. Editor scene — rads render extended (matches the prior
    //      `state == StartState.Editor` shortcut, no surprise in the VAB).
    //   3. Fresh launch — start retracted (stock convention; the prior
    //      `[KSPField(isPersistant)] isExtended` defaulted to false).
    bool deployed = radiator.LoadedFromSave
        ? radiator.IsDeployed
        : state == StartState.Editor;
    radiator.IsDeployed = deployed;
    SetAnimationPosition(deployed ? 1f : 0f);

    if (state != StartState.Editor) {
      var vesselModule = vessel.FindVesselModuleImplementing<NovaVesselModule>();
      if (vesselModule?.Virtual != null)
        vesselModule.Virtual.Invalidate();
    }
  }

  // Deploy methods are called from NovaPartTopic.HandleOp on a
  // `setRadiatorDeployed` op — never exposed in the stock PAW. Player-
  // facing deploy lives in the Nova UI (ThermalView).
  public void Extend() {
    if (!IsDeployable || radiator == null) return;
    if (animating || radiator.IsDeployed) return;
    if (anim == null) {
      radiator.IsDeployed = true;
      OnDeployStateChanged();
      return;
    }

    anim[animationName].normalizedTime = 0f;
    anim[animationName].speed = HighLogic.LoadedSceneIsEditor ? 5f : 1f;
    anim[animationName].enabled = true;
    anim[animationName].weight = 1f;
    anim.Play(animationName);
    animating = true;
  }

  public void Retract() {
    if (!IsDeployable || radiator == null) return;
    if (animating || !radiator.IsDeployed) return;
    if (!retractable && !HighLogic.LoadedSceneIsEditor) return;
    if (anim == null) {
      radiator.IsDeployed = false;
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
    if (!animating || anim == null || radiator == null) return;

    var time = anim[animationName].normalizedTime;
    if (anim[animationName].speed > 0 && time >= 1f) {
      anim.Stop(animationName);
      SetAnimationPosition(1f);
      animating = false;
      radiator.IsDeployed = true;
      OnDeployStateChanged();
    } else if (anim[animationName].speed < 0 && time <= 0f) {
      anim.Stop(animationName);
      SetAnimationPosition(0f);
      animating = false;
      radiator.IsDeployed = false;
      OnDeployStateChanged();
    }
  }

  private void OnDeployStateChanged() {
    if (vessel != null) {
      var vesselModule = vessel.FindVesselModuleImplementing<NovaVesselModule>();
      if (vesselModule?.Virtual != null)
        vesselModule.Virtual.Invalidate();
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
