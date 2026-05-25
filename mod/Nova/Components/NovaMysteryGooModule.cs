using System.Linq;
using UnityEngine;
using Nova.Core.Components.Science;
using Nova.Telemetry;

namespace Nova.Components;

// KSP-side adapter for a Mystery Goo chamber. The MysteryGoo virtual
// component (sample inventory, exposure timer, ScienceFile production)
// is built by ComponentFactory from the cfg; this module seeds initial
// samples on first-launch and drives the cover animation directly
// (Nova strips stock ModuleAnimateGeneric).
//
// Player ops come in via the NovaPartTopic 'setGooCoverOpen' op — not
// the stock PAW. Cover-open/close has chamber-state semantics: opening
// arms exposure of the next Pristine sample, closing mid-exposure
// invalidates it (see MysteryGoo.OpenCover / CloseCover).
//
// Cfg shape (on the NovaMysteryGooModule MODULE block):
//   capacity          = N
//   allowedSampleType = id   (repeatable)
//   initialSample     = id   (repeatable; pre-loaded inventory)
//   animationName     = <clip on the model>  (optional; default = stock
//                       "Deploy" via ResolveAnimation fallback)
public class NovaMysteryGooModule : NovaPartModule {

  private MysteryGoo goo;
  private Animation anim;
  private string animationName = "Deploy";
  private bool animating;

  public override void OnStart(StartState state) {
    base.OnStart(state);

    goo = Components?.OfType<MysteryGoo>().FirstOrDefault();
    if (goo == null) return;

    // Stamp the part title once — ScienceFiles produced by exposure
    // carry this as their `Instrument` field.
    if (goo.InstrumentName == "Mystery Goo" && part?.partInfo?.title != null)
      goo.InstrumentName = part.partInfo.title;

    // Seed initial samples on a fresh chamber (no save state restored).
    // A loaded chamber already has Samples populated by MysteryGoo.Load.
    goo.SeedInitialSamples();

    // Resolve cover animation. The cfg may override animationName; the
    // ResolveAnimation fallback uses the first model clip if our preferred
    // name doesn't resolve — covers ReStock-style mesh swaps.
    var cfg = GetPrefabModuleConfig();
    if (cfg != null && cfg.HasValue("animationName"))
      animationName = cfg.GetValue("animationName");
    (anim, animationName) = ResolveAnimation(animationName);
    if (anim != null) {
      anim[animationName].wrapMode = WrapMode.ClampForever;
    }

    if (state == StartState.Editor) {
      // VAB convention: cover starts closed (loaded samples not visible).
      goo.CoverOpen = false;
      if (anim != null) SetAnimationPosition(0f);
      return;
    }

    // Flight: snap the cover animation to the persisted state.
    if (anim != null) SetAnimationPosition(goo.CoverOpen ? 1f : 0f);
  }

  // Matched-vessel quickload hook. VirtualComponent.Load has just
  // refreshed cover/sample state — snap the cover animation to match.
  public override void OnNovaStateRestored() {
    if (goo == null) return;
    if (anim != null) SetAnimationPosition(goo.CoverOpen ? 1f : 0f);
    animating = false;
  }

  // Called by NovaPartTopic.HandleOp on the `setGooCoverOpen` op.
  public void OpenCover() {
    if (goo == null || goo.CoverOpen || animating) return;
    double ut = Planetarium.GetUniversalTime();
    goo.OpenCover(ut);
    if (anim != null) PlayAnimation(forward: true);
    else OnCoverStateChanged();
  }

  public void CloseCover() {
    if (goo == null || !goo.CoverOpen || animating) return;
    double ut = Planetarium.GetUniversalTime();
    goo.CloseCover(ut);
    if (anim != null) PlayAnimation(forward: false);
    else OnCoverStateChanged();
  }

  public void FixedUpdate() {
    if (goo == null || !HighLogic.LoadedSceneIsFlight) {
      // Editor scene: nothing to drive.
      return;
    }

    // Refresh live progress every tick during exposure so the wire
    // frame's ETA countdown stays current. MarkPartDirty so the next
    // broadcast picks it up; the 10 Hz cadence gates the actual emit.
    if (goo.CoverOpen && goo.ExposingIndex >= 0) {
      goo.UpdateLiveProgress(Planetarium.GetUniversalTime());
      if (part != null) NovaPartTopic.MarkPartDirty(part.persistentId);
    }

    if (!animating || anim == null) return;
    var time = anim[animationName].normalizedTime;
    if (anim[animationName].speed > 0 && time >= 1f) {
      anim.Stop(animationName);
      SetAnimationPosition(1f);
      animating = false;
      OnCoverStateChanged();
    } else if (anim[animationName].speed < 0 && time <= 0f) {
      anim.Stop(animationName);
      SetAnimationPosition(0f);
      animating = false;
      OnCoverStateChanged();
    }
  }

  private void PlayAnimation(bool forward) {
    anim[animationName].normalizedTime = forward ? 0f : 1f;
    anim[animationName].speed = forward
        ? (HighLogic.LoadedSceneIsEditor ? 5f : 1f)
        : (HighLogic.LoadedSceneIsEditor ? -5f : -1f);
    anim[animationName].enabled = true;
    anim[animationName].weight = 1f;
    anim.Play(animationName);
    animating = true;
  }

  private void OnCoverStateChanged() {
    if (part != null) NovaPartTopic.MarkPartDirty(part.persistentId);
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
