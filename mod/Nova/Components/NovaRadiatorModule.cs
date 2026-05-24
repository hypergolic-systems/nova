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

  [KSPField(isPersistant = true)]
  public bool isExtended;

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

    // Fixed panel: no animation, always deployed.
    if (!IsDeployable) {
      radiator.IsDeployed = true;
      return;
    }

    // Empty (not null) when no Animation on the model has the named
    // clip — e.g. ReStock-replaced meshes whose anim was renamed.
    // ?[0] threw on the empty case, aborting OnStart before
    // IsDeployed was wired; FirstOrDefault degrades to "no animation,
    // deploy state still tracks isExtended" and the null-guards
    // downstream already cover that path.
    anim = part.FindModelAnimators(animationName)?.FirstOrDefault();

    // ClampForever so Unity holds the end pose after the clip — see
    // NovaDeployableSolarModule for the rationale (Once leaves
    // normalizedTime ambiguous across the disable boundary).
    if (anim != null) {
      anim[animationName].wrapMode = WrapMode.ClampForever;
    }

    if (state == StartState.Editor || isExtended) {
      SetAnimationPosition(1f);
      radiator.IsDeployed = true;
    } else {
      SetAnimationPosition(0f);
      radiator.IsDeployed = false;
    }

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
    if (!IsDeployable) return;
    if (animating || isExtended) return;
    if (anim == null) {
      isExtended = true;
      if (radiator != null) radiator.IsDeployed = true;
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
    if (!IsDeployable) return;
    if (animating || !isExtended) return;
    if (!retractable && !HighLogic.LoadedSceneIsEditor) return;
    if (anim == null) {
      isExtended = false;
      if (radiator != null) radiator.IsDeployed = false;
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
      isExtended = true;
      animating = false;
      if (radiator != null) radiator.IsDeployed = true;
      OnDeployStateChanged();
    } else if (anim[animationName].speed < 0 && time <= 0f) {
      anim.Stop(animationName);
      SetAnimationPosition(0f);
      isExtended = false;
      animating = false;
      if (radiator != null) radiator.IsDeployed = false;
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
