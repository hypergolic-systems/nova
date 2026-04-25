using UnityEngine;

namespace Nova.Components;

public class NovaDeployableSolarModule : NovaSolarModule {

  [KSPField]
  public string animationName;

  [KSPField]
  public bool retractable = true;

  [KSPField(isPersistant = true)]
  public bool isExtended;

  private Animation anim;
  private bool animating;

  public override void OnStart(StartState state) {
    base.OnStart(state);

    anim = part.FindModelAnimators(animationName)?[0];

    if (state == StartState.Editor || isExtended) {
      SetAnimationPosition(1f);
      solarPanel.IsDeployed = true;
    } else {
      SetAnimationPosition(0f);
      solarPanel.IsDeployed = false;
    }

    UpdateEvents();

    if (state != StartState.Editor) {
      var vesselModule = vessel.FindVesselModuleImplementing<NovaVesselModule>();
      if (vesselModule != null)
        vesselModule.InvalidateSolarData();
    }
  }

  [KSPEvent(guiActive = true, guiActiveEditor = true, guiActiveUnfocused = true,
    unfocusedRange = 4f, guiName = "Extend Solar Panel")]
  public void Extend() {
    if (animating || isExtended) return;
    if (anim == null) return;

    anim[animationName].normalizedTime = 0f;
    anim[animationName].speed = HighLogic.LoadedSceneIsEditor ? 5f : 1f;
    anim.Play(animationName);
    animating = true;
    UpdateEvents();

    foreach (var sym in part.symmetryCounterparts) {
      var mod = sym.FindModuleImplementing<NovaDeployableSolarModule>();
      if (mod != null && !mod.animating && !mod.isExtended)
        mod.Extend();
    }
  }

  [KSPEvent(guiActive = true, guiActiveEditor = true, guiActiveUnfocused = true,
    unfocusedRange = 4f, guiName = "Retract Solar Panel")]
  public void Retract() {
    if (animating || !isExtended) return;
    if (!retractable && !HighLogic.LoadedSceneIsEditor) return;
    if (anim == null) return;

    anim[animationName].normalizedTime = 1f;
    anim[animationName].speed = HighLogic.LoadedSceneIsEditor ? -5f : -1f;
    anim.Play(animationName);
    animating = true;
    UpdateEvents();

    foreach (var sym in part.symmetryCounterparts) {
      var mod = sym.FindModuleImplementing<NovaDeployableSolarModule>();
      if (mod != null && !mod.animating && mod.isExtended)
        mod.Retract();
    }
  }

  public void FixedUpdate() {
    if (!animating || anim == null) return;

    var time = anim[animationName].normalizedTime;
    if (anim[animationName].speed > 0 && time >= 1f) {
      anim.Stop(animationName);
      SetAnimationPosition(1f);
      isExtended = true;
      animating = false;
      solarPanel.IsDeployed = true;
      OnDeployStateChanged();
    } else if (anim[animationName].speed < 0 && time <= 0f) {
      anim.Stop(animationName);
      SetAnimationPosition(0f);
      isExtended = false;
      animating = false;
      solarPanel.IsDeployed = false;
      OnDeployStateChanged();
    }
  }

  private void OnDeployStateChanged() {
    UpdateEvents();
    if (vessel != null) {
      var vesselModule = vessel.FindVesselModuleImplementing<NovaVesselModule>();
      if (vesselModule != null)
        vesselModule.InvalidateSolarData();
    }
  }

  private void UpdateEvents() {
    Events["Extend"].active = !isExtended && !animating;
    Events["Retract"].active = isExtended && !animating
      && (retractable || HighLogic.LoadedSceneIsEditor);
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
