using System.Linq;
using UnityEngine;
using Nova.Communications;
using Nova.Core.Components.Communications;

namespace Nova.Components;

// KSP-side wrapper for an antenna part. The Antenna virtual component
// (TxPower, Gain, MaxRate, RefDistance) is built by ComponentFactory
// from the cfg; this module owns the deploy animation and keeps the
// component's IsDeployed flag in sync.
//
// Nova drives the model animation itself — stock ModuleDeployableAntenna
// is stripped via cfg patch on every deployable antenna part. The
// player-facing extend/retract control lives on the `setAntennaDeployed`
// op via NovaPartTopic. Fixed antennas (configs that don't set
// `animationName`, integrated antennas on probe cores, …) skip all
// animation logic and stay IsDeployed = true.
//
// Cfg shape (on the NovaAntennaModule MODULE block):
//   animationName = <clip name on the part model>   (omit for fixed)
//   retractable   = true | false                    (default true)
//
// No ICommAntenna — Nova owns its own graph; stock CommNet routing is
// bypassed.
public class NovaAntennaModule : NovaPartModule {

  private Antenna antenna;

  // Animation drive — non-null only when the cfg names a clip that
  // resolves on the model. Fixed (no-anim) antennas leave both null
  // and IsDeployed stays true for the part's lifetime.
  private Animation anim;
  private string animationName = "";
  private bool retractable = true;
  private bool animating;

  public override void OnStart(StartState state) {
    base.OnStart(state);

    antenna = Components?.OfType<Antenna>().FirstOrDefault();
    if (antenna == null) return;

    // Read animation config from the prefab MODULE block. The cfg
    // belongs to NovaAntennaModule, not the stripped stock module —
    // GetPrefabModuleConfig matches by `name = NovaAntennaModule`.
    var cfg = GetPrefabModuleConfig();
    if (cfg != null) {
      animationName = cfg.GetValue("animationName") ?? "";
      if (cfg.HasValue("retractable"))
        bool.TryParse(cfg.GetValue("retractable"), out retractable);
    }

    // Resolve the deploy animation. cfg's animationName is the preference;
    // ResolveAnimation falls back to "first model Animation, first clip"
    // so ReStock-overhauled parts with renamed clips still animate.
    // Empty animationName in cfg → fixed antenna, no fallback runs.
    if (!string.IsNullOrEmpty(animationName)) {
      (anim, animationName) = ResolveAnimation(animationName);
      if (anim != null) {
        // Mirror stock startFSM: ClampForever holds the end pose when
        // the clip finishes so the bell doesn't snap back to frame 0.
        anim[animationName].wrapMode = WrapMode.ClampForever;
      } else {
        Debug.LogWarning($"[Nova/Antenna] {part.partInfo?.name}: "
            + $"no model animations found — treating as fixed antenna");
        animationName = "";
      }
    }

    if (state == StartState.Editor) {
      // VAB convention: antennas render extended so the player can see
      // the deployed silhouette while building.
      antenna.IsDeployed = true;
      if (anim != null) SetAnimationPosition(1f);
      return;
    }

    // Three flight cases — same shape as NovaDeployableSolarModule /
    // NovaRadiatorModule:
    //   1. Loaded from save (LoadedFromSave) — use proto value already
    //      in antenna.IsDeployed.
    //   2. Fresh launch + deployable — start retracted (stock convention:
    //      player has to extend deployable antennas after launch).
    //   3. Fresh launch + fixed — IsDeployed stays at the component
    //      default (true), set explicitly here for clarity.
    bool deployed;
    if (antenna.LoadedFromSave) deployed = antenna.IsDeployed;
    else if (anim != null)      deployed = false;
    else                        deployed = true;

    antenna.IsDeployed = deployed;
    if (anim != null) SetAnimationPosition(deployed ? 1f : 0f);
    NovaCommunicationsAddon.Instance?.Network?.Invalidate();
  }

  // Matched-vessel quickload hook (NovaSaveLoader.ApplyVesselState).
  // VirtualComponent.Load has just refreshed antenna.IsDeployed from
  // proto — snap the animation to match and invalidate the network
  // (the graph treats deploy-state changes as topology events).
  public override void OnNovaStateRestored() {
    if (antenna == null) return;
    if (anim != null) SetAnimationPosition(antenna.IsDeployed ? 1f : 0f);
    animating = false;
    NovaCommunicationsAddon.Instance?.Network?.Invalidate();
  }

  // Player-facing extend/retract — called from NovaPartTopic on the
  // `setAntennaDeployed` op. Not exposed in the stock PAW (Nova owns
  // its player-facing UI).
  public void Extend() {
    if (antenna == null) return;
    if (animating || antenna.IsDeployed) return;
    if (anim == null) {
      // No animation (fixed antenna or cfg/mesh mismatch). Flip state
      // instantly; the mesh stays wherever it defaulted.
      antenna.IsDeployed = true;
      OnDeployStateChanged();
      return;
    }

    // SetAnimationPosition leaves the AnimationState disabled. Unity's
    // legacy Play() doesn't reliably re-enable a stopped state, so
    // flip enabled/weight ourselves before playing.
    anim[animationName].normalizedTime = 0f;
    anim[animationName].speed = HighLogic.LoadedSceneIsEditor ? 5f : 1f;
    anim[animationName].enabled = true;
    anim[animationName].weight = 1f;
    anim.Play(animationName);
    animating = true;
  }

  public void Retract() {
    if (antenna == null) return;
    if (animating || !antenna.IsDeployed) return;
    if (!retractable && !HighLogic.LoadedSceneIsEditor) return;
    if (anim == null) {
      antenna.IsDeployed = false;
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
    if (!animating || anim == null || antenna == null) return;

    var time = anim[animationName].normalizedTime;
    if (anim[animationName].speed > 0 && time >= 1f) {
      anim.Stop(animationName);
      SetAnimationPosition(1f);
      animating = false;
      antenna.IsDeployed = true;
      OnDeployStateChanged();
    } else if (anim[animationName].speed < 0 && time <= 0f) {
      anim.Stop(animationName);
      SetAnimationPosition(0f);
      animating = false;
      antenna.IsDeployed = false;
      OnDeployStateChanged();
    }
  }

  private void OnDeployStateChanged() {
    // Deploy state change is a topology event for the comm graph —
    // pairwise rates and per-vessel path-to-home may all change.
    NovaCommunicationsAddon.Instance?.Network?.Invalidate();
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
