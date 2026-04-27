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

  // Per-panel deploy. Stock ModuleDeployablePart walks part.symmetryCounterparts
  // here; Nova does not — each panel's Extend/Retract affects only the
  // panel it was called on, so the PAW and the PWR UI both produce
  // single-panel actions. Deploy a four-way symmetry group by clicking
  // each panel (or the SOLAR subgroup bulk control in the UI).
  [KSPEvent(guiActive = true, guiActiveEditor = true, guiActiveUnfocused = true,
    unfocusedRange = 4f, guiName = "Extend Solar Panel")]
  public void Extend() {
    if (animating || isExtended) return;
    if (anim == null) return;

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
    UpdateEvents();
  }

  [KSPEvent(guiActive = true, guiActiveEditor = true, guiActiveUnfocused = true,
    unfocusedRange = 4f, guiName = "Retract Solar Panel")]
  public void Retract() {
    if (animating || !isExtended) return;
    if (!retractable && !HighLogic.LoadedSceneIsEditor) return;
    if (anim == null) return;

    anim[animationName].normalizedTime = 1f;
    anim[animationName].speed = HighLogic.LoadedSceneIsEditor ? -5f : -1f;
    anim[animationName].enabled = true;
    anim[animationName].weight = 1f;
    anim.Play(animationName);
    animating = true;
    UpdateEvents();
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
