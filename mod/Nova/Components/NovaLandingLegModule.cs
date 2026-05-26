using System.Linq;
using ModuleWheels;
using UnityEngine;
using Nova.Core.Components.Structural;
using Nova.Telemetry;

namespace Nova.Components;

// Landing-leg part module. Replaces stock ModuleWheelDeployment on
// LEG-type wheel parts. The rest of the stock wheel stack
// (ModuleWheelBase + Suspension + Bogey + Damage + Lock) is left
// intact and continues to own the foot collider, suspension physics,
// and damage model — Nova does not reimplement that.
//
// Player input. The G key (KSPActionGroup.Gear) routes through the
// [KSPAction] hook below. Until OnActive() fires for this part (or
// the player flips Activated via the topic op), the action is a
// no-op. Activated mirrors stock engine semantics: born true when
// requiresStaging = false, becomes true on first stage-fire when
// requiresStaging = true.
//
// EC mechanics. The LandingLeg virtual component holds a High-
// priority PFS device demanding MotorPowerW while the leg is in
// motion. Each FixedUpdate, we step Position by
//   direction × FullSpeedPerSecond × device.Activity × dt
// so a starved bus (Activity = 0) freezes the leg in place; partial
// power deploys at proportional speed.
//
// Animation drive. We manage normalizedTime + anim.Sample() + Stop()
// directly (same pattern as NovaMysteryGooModule.SetAnimationPosition).
// Setting speed=0 with enabled=true and letting Unity blend is
// non-deterministic on freeze; the manual path is exact.
//
// Wheel collider gate. We add/remove a WheelSubsystem entry on
// ModuleWheelBase.InopSystems mirroring stock ModuleWheelDeployment's
// SetInopSubsystems(Position < TsubSys). The foot collider only
// produces ground contact once the leg passes deployThreshold.
public class NovaLandingLegModule : NovaPartModule {

  [KSPField] public string animationName   = "Deploy";
  [KSPField] public float  deployThreshold = 1.0f;
  [KSPField] public bool   requiresStaging = true;
  [KSPField] public bool   startsDeployed  = false;
  [KSPField] public string fxDeploy        = "deploy";
  [KSPField] public string fxRetract       = "retract";
  [KSPField] public string fxDeployed      = "deployed";
  [KSPField] public string fxRetracted     = "retracted";

  private LandingLeg leg;
  private Animation anim;
  private ModuleWheelBase wheelBase;
  private WheelSubsystem inopSubsystem;
  private bool inopAdded;
  private bool wasMoving;
  private double lastPositionEmitted = -1;

  public LandingLeg Leg => leg;

  public override void OnStart(StartState state) {
    base.OnStart(state);

    leg = Components.OfType<LandingLeg>().FirstOrDefault();
    if (leg == null) return;

    // KSPField defaults apply only on fresh spawn — Load() overrides
    // them with persisted editor selections.
    if (!leg.LoadedFromSave) {
      leg.RequiresStaging = requiresStaging;
      leg.StartsDeployed  = startsDeployed;
    }

    (anim, animationName) = ResolveAnimation(animationName);
    if (anim != null) anim[animationName].wrapMode = WrapMode.ClampForever;

    wheelBase = part.FindModuleImplementing<ModuleWheelBase>();
    if (wheelBase == null) {
      Debug.LogError("[Nova/LandingLeg] " + part.partInfo?.name +
          " has NovaLandingLegModule but no sibling ModuleWheelBase — leg disabled");
      return;
    }
    inopSubsystem = new WheelSubsystem("Nova: Leg retracted",
        WheelSubsystem.SystemTypes.All, this);

    // Fresh-spawn defaults — Load() leaves LoadedFromSave true and
    // these are skipped:
    //   editor          — render the configured StartsDeployed pose;
    //                     Activated is editor-only meaningful (mirrors
    //                     !RequiresStaging for preview purposes).
    //   fresh flight    — start at the configured StartsDeployed pose;
    //                     Activated = !RequiresStaging (born active iff
    //                     no staging gate).
    if (!leg.LoadedFromSave) {
      double initialPos = leg.StartsDeployed ? 1.0 : 0.0;
      leg.Position = initialPos;
      leg.TargetPosition = initialPos;
      leg.Activated = !leg.RequiresStaging;
    }

    SetAnimationPosition((float)leg.Position);
    UpdateInopSubsystem();
    RefreshStagingPresentation();
  }

  public override void OnNovaStateRestored() {
    if (leg == null) return;
    SetAnimationPosition((float)leg.Position);
    UpdateInopSubsystem();
    RefreshStagingPresentation();
    wasMoving = leg.IsMoving;
  }

  public override void OnActive() {
    // Staging gate. No-op when RequiresStaging = false (the leg is
    // already armed and OnActive may still fire if it's in a stage).
    if (leg == null || !leg.RequiresStaging) return;
    leg.Activated = true;
    NovaPartTopic.MarkPartDirty(part.persistentId);
  }

  // IsStageable controls staging-stack visibility. Tracks RequiresStaging
  // so a player flip in the editor (via setLandingLegRequiresStaging)
  // refreshes the stack icon immediately when RefreshStagingPresentation
  // is called.
  public override bool IsStageable() => leg?.RequiresStaging ?? false;

  [KSPAction("Toggle Gear", KSPActionGroup.Gear)]
  public void ActionToggle(KSPActionParam param) {
    if (leg == null || !leg.Activated) return;
    switch (param.type) {
      case KSPActionType.Activate:   Extend();  break;
      case KSPActionType.Deactivate: Retract(); break;
      case KSPActionType.Toggle:
        if (leg.TargetPosition >= 0.5) Retract();
        else                            Extend();
        break;
    }
  }

  public void Extend() {
    if (leg == null || !leg.Activated) return;
    if (leg.TargetPosition >= 0.5) return;
    leg.TargetPosition = 1.0;
    // Stop the opposite loop in case we're reversing mid-motion;
    // stock leg cfgs put an AUDIO_LOOP in both `deploy` and `retract`
    // effect blocks and the only stop signal is Effect(name, 0f).
    StopEffect(fxRetract);
    PlayEffect(fxDeploy);
    OnLegStateChanged();
  }

  public void Retract() {
    if (leg == null || !leg.Activated) return;
    if (leg.TargetPosition < 0.5) return;
    leg.TargetPosition = 0.0;
    StopEffect(fxDeploy);
    PlayEffect(fxRetract);
    OnLegStateChanged();
  }

  // Editor-tunable property setters — called from NovaPartTopic.HandleOp.
  // Flight-time tweaks aren't supported (no UI surfaces them) but the
  // guards are defensive: SetActivated is the runtime override for
  // "forgot to stage", the other two are pure editor concerns.

  public void SetRequiresStaging(bool value) {
    if (leg == null) return;
    leg.RequiresStaging = value;
    if (HighLogic.LoadedSceneIsEditor) leg.Activated = !value;
    RefreshStagingPresentation();
  }

  public void SetStartsDeployed(bool value) {
    if (leg == null) return;
    leg.StartsDeployed = value;
    if (HighLogic.LoadedSceneIsEditor) {
      double pos = value ? 1.0 : 0.0;
      leg.Position = pos;
      leg.TargetPosition = pos;
      SetAnimationPosition((float)pos);
      UpdateInopSubsystem();
    }
  }

  public void SetActivated(bool value) {
    if (leg == null) return;
    leg.Activated = value;
    NovaPartTopic.MarkPartDirty(part.persistentId);
  }

  public void FixedUpdate() {
    if (leg == null || anim == null) return;
    if (!HighLogic.LoadedSceneIsFlight) return;

    bool moving = leg.IsMoving;
    if (moving) {
      double direction = leg.TargetPosition > leg.Position ? 1.0 : -1.0;
      double step = leg.FullSpeedPerSecond * leg.Activity * Time.fixedDeltaTime;
      double next = leg.Position + direction * step;

      // Snap to target on overshoot — prevents drift past terminal.
      if (direction > 0 && next >= leg.TargetPosition) next = leg.TargetPosition;
      if (direction < 0 && next <= leg.TargetPosition) next = leg.TargetPosition;

      leg.Position = next;
      SetAnimationPosition((float)leg.Position);
      UpdateInopSubsystem();

      if (leg.Position == leg.TargetPosition) {
        // Kill both motor loops — the one-shot deployed/retracted FX
        // is a separate effect block, so stopping deploy/retract is
        // what silences the AUDIO_LOOP.
        StopEffect(fxDeploy);
        StopEffect(fxRetract);
        PlayEffect(leg.Position >= 0.5 ? fxDeployed : fxRetracted);
        OnLegStateChanged();
      }
    }

    // moving→stopped edge: telemetry needs to see the final pose +
    // demand-cleared device. Without this the wire would keep showing
    // stale IsMoving=true / non-zero CurrentEcW for one broadcaster
    // tick after the leg finished moving.
    if (moving != wasMoving) {
      NovaPartTopic.MarkPartDirty(part.persistentId);
      wasMoving = moving;
    }
    // Coarse mid-motion update — ~100 wire frames per full deploy.
    if (moving && System.Math.Abs(leg.Position - lastPositionEmitted) >= 0.01) {
      NovaPartTopic.MarkPartDirty(part.persistentId);
      lastPositionEmitted = leg.Position;
    }
  }

  private void UpdateInopSubsystem() {
    if (wheelBase == null || inopSubsystem == null) return;
    bool inop = leg.Position < deployThreshold;
    if (inop && !inopAdded) {
      wheelBase.InopSystems.AddSubsystem(inopSubsystem);
      inopAdded = true;
    } else if (!inop && inopAdded) {
      wheelBase.InopSystems.RemoveSubsystem(inopSubsystem);
      inopAdded = false;
    }
  }

  private void RefreshStagingPresentation() {
    // STRUT is the closest stock icon — stock catalogue is just
    // SCIENCE_GENERIC / DECOUPLER_HOR / DECOUPLER_VERT / LIQUID_ENGINE
    // / PARACHUTES / STRUT.
    if (leg == null) return;
    part.stagingIcon = leg.RequiresStaging ? "STRUT" : "";
    if (part.stackIcon != null) {
      if (leg.RequiresStaging) part.stackIcon.CreateIcon();
      else part.stackIcon.RemoveIcon();
    }
  }

  private void OnLegStateChanged() {
    if (vessel != null) {
      var vm = vessel.FindVesselModuleImplementing<NovaVesselModule>();
      vm?.Virtual?.Invalidate();
    }
    NovaPartTopic.MarkPartDirty(part.persistentId);
  }

  private void PlayEffect(string fx) {
    if (string.IsNullOrEmpty(fx)) return;
    part.Effect(fx);
  }

  // Stock pattern: Effect(name, 0f) silences the named effect's
  // AUDIO_LOOP. Without this the loop runs autonomously in Unity's
  // audio engine until the part is destroyed.
  private void StopEffect(string fx) {
    if (string.IsNullOrEmpty(fx)) return;
    part.Effect(fx, 0f);
  }

  private void SetAnimationPosition(float t) {
    if (anim == null) return;
    anim[animationName].normalizedTime = Mathf.Clamp01(t);
    anim[animationName].speed = 0f;
    anim[animationName].enabled = true;
    anim[animationName].weight = 1f;
    anim.Sample();
    anim.Stop(animationName);
  }

  public void OnDestroy() {
    if (wheelBase != null && inopSubsystem != null && inopAdded) {
      wheelBase.InopSystems.RemoveSubsystem(inopSubsystem);
      inopAdded = false;
    }
  }
}
