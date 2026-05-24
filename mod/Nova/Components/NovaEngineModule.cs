using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Nova.Core.Components.Propulsion;
using Nova.Effects;
using Waterfall;

namespace Nova.Components;

public class NovaEngineModule : NovaPartModule, IEngineStatus, ITorqueProvider, IWaterfallControllerProvider {

  // Gimbal config — optional, mirrored from stock ModuleGimbal field
  // names so cfg patches read intuitively. `gimbalRange` is in degrees
  // here (matching the stock convention); `Engine.GimbalRangeRad`
  // stores the converted radians. `gimbalTransformName` names the
  // pivot transform in the model — typically "Gimbal" for stock parts.
  [KSPField] public float gimbalRange = 0f;
  [KSPField] public string gimbalTransformName = "Gimbal";

  private Engine engine;
  // Protected so NovaNuclearEngineModule can read/write it from its
  // ApplyPlayerThrottle override.
  protected float lastThrottle = -1;
  private bool wasFlowing;
  private bool wasActive;

  // Auto-discovered at OnStart by scanning the part's EFFECTS for a
  // sub-effect with a looping AUDIO/AUDIO_MULTI_POOL entry. Stock
  // ModuleEngines/EnginesFX has an explicit `runningEffectName` cfg
  // field; we discover it instead so per-engine cfg authors don't
  // have to keep our value in sync with whichever audio pack
  // (WaterfallRestock, plume-pack-of-the-week) is layered on top —
  // each pack tends to rename this differently (`running` for the
  // Skipper, `fx-swivel-running` for the LV-T45, …) but all of them
  // make it the one-and-only looping audio in the EFFECTS block.
  private string runningEffectName;

  private Transform[] thrustTransforms;
  private float[] thrustMultipliers;

  // Gimbal pivot — null if `gimbalRange` is 0 or the model doesn't
  // contain the named transform. `initGimbalRot` is the pivot's local
  // rotation at zero deflection; we rebuild localRotation from this
  // each tick so accumulated drift is impossible.
  private Transform gimbalTransform;
  private Quaternion initGimbalRot;

  // Nominal thrust force direction at zero gimbal deflection, expressed
  // in part-local frame. Captured at OnStart from `-thrustTransform.
  // forward` (the same expression FixedUpdate uses to apply thrust),
  // so the QP and ITorqueProvider compute slot torques in the same
  // sign convention the physics step actually realises. Using
  // `gt.forward` instead is unsafe — engine models put gt.forward in
  // either direction depending on how the artist oriented the pivot.
  private Vector3 nominalThrustDirLocal;

  /// <summary>
  /// The gimbal pivot transform, or null if this engine doesn't gimbal
  /// (or the named transform wasn't found). Read by
  /// `NovaVesselModule.SolveAttitude` to extract vessel-local pivot
  /// geometry when building the QP.
  /// </summary>
  public Transform GimbalTransform => gimbalTransform;

  /// <summary>
  /// Nominal thrust force direction in world frame (at zero gimbal
  /// deflection). Caller should `vessel.ReferenceTransform.InverseTransformDirection`
  /// it for vessel-local use. Returns Vector3.up as a safe fallback when
  /// thrustTransforms haven't been resolved.
  /// </summary>
  public Vector3 NominalThrustDirectionWorld =>
      thrustTransforms != null && thrustTransforms.Length > 0
          ? part.transform.TransformDirection(nominalThrustDirLocal)
          : Vector3.up;

  /// <summary>The virtual component, exposed for callers (eg. the attitude solver).</summary>
  public Engine Engine => engine;

  /// <summary>
  /// Report this engine's gimbal torque authority to SAS. Without this,
  /// SAS sums only `wheel + RCS` for total vessel authority but our QP
  /// happily routes through the gimbal too — so SAS undersizes
  /// `ctrlState`, the QP saturates wheel + gimbal, and the actual
  /// torque comes in larger than SAS asked for. The miscalibration
  /// scales with current engine output, hence the slow oscillations
  /// at low throttle and the loss of control at high throttle.
  ///
  /// Reports the torque available *right now*: scaled by the engine's
  /// LP-solved `NormalizedOutput`, since gimbal authority is zero on a
  /// shut-down engine and proportional to thrust on a throttled one.
  /// </summary>
  public void GetPotentialTorque(out Vector3 pos, out Vector3 neg) {
    pos = neg = Vector3.zero;
    if (engine == null || gimbalTransform == null
        || engine.GimbalRangeRad <= 0
        || vessel == null) return;

    float currentThrust = (float)(engine.Thrust * engine.NormalizedOutput);
    if (currentThrust <= 0) return;

    // ITorqueProvider expects per-axis magnitudes in the vessel
    // reference frame (matching `NovaReactionWheelModule`'s convention
    // where `pos.x` is "torque around vessel pitch axis"). World-frame
    // numbers misalign with SAS's per-axis ctrlState mapping whenever
    // the vessel is rotated, which causes the QP to deliver more
    // vessel-frame torque than SAS expected.
    var refXform = vessel.ReferenceTransform;
    var thrustDir = refXform.InverseTransformDirection(NominalThrustDirectionWorld);
    var pitchAxis = refXform.InverseTransformDirection(gimbalTransform.right);
    var yawAxis = refXform.InverseTransformDirection(gimbalTransform.up);
    var leverWorld = gimbalTransform.position - vessel.CoM;
    var lever = refXform.InverseTransformDirection(leverWorld);
    float sideMag = currentThrust * Mathf.Sin((float)engine.GimbalRangeRad);

    // Full-deflection lateral forces in vessel-local frame, then
    // lever × force for the torque vectors. Absolute values
    // componentwise — gimbal ±deflection is symmetric, so a slot
    // can deliver +X torque one way and -X the other.
    var pitchSide = Vector3.Cross(pitchAxis, thrustDir).normalized;
    var pitchTorque = Vector3.Cross(lever, pitchSide * sideMag);
    var yawSide = Vector3.Cross(yawAxis, thrustDir).normalized;
    var yawTorque = Vector3.Cross(lever, yawSide * sideMag);

    pos.x = neg.x = Mathf.Max(Mathf.Abs(pitchTorque.x), Mathf.Abs(yawTorque.x));
    pos.y = neg.y = Mathf.Max(Mathf.Abs(pitchTorque.y), Mathf.Abs(yawTorque.y));
    pos.z = neg.z = Mathf.Max(Mathf.Abs(pitchTorque.z), Mathf.Abs(yawTorque.z));
  }

  public override void OnStart(StartState state) {
    base.OnStart(state);

    engine = Components.OfType<Engine>().First();

    thrustTransforms = part.FindModelTransforms("thrustTransform");
    if (thrustTransforms.Length > 0) {
      var mult = 1f / thrustTransforms.Length;
      thrustMultipliers = Enumerable.Repeat(mult, thrustTransforms.Length).ToArray();

      // Capture nominal thrust direction (gimbal at rest) in part-local
      // frame. Same expression FixedUpdate uses for force application,
      // so slot-torque calcs stay sign-consistent with the physics.
      nominalThrustDirLocal =
          part.transform.InverseTransformDirection(-thrustTransforms[0].forward);
    } else {
      thrustMultipliers = new float[0];
    }


    runningEffectName = DiscoverRunningEffectName();

    // Gimbal setup. Find the pivot, capture its zero-deflection pose,
    // and stamp the radians-converted range onto the virtual component
    // so the attitude solver can see this engine as gimbal-capable.
    // If the cfg promises a gimbal but the model doesn't have the
    // named transform, log + leave gimbal disabled rather than throw.
    if (gimbalRange > 0f) {
      gimbalTransform = part.FindModelTransform(gimbalTransformName);
      if (gimbalTransform != null) {
        initGimbalRot = gimbalTransform.localRotation;
        engine.GimbalRangeRad = gimbalRange * Mathf.Deg2Rad;
        // Tell the vessel module to rebuild attitude with this engine's
        // gimbal slots in the mix.
        if (state != StartState.Editor) {
          var vm = vessel?.FindVesselModuleImplementing<NovaVesselModule>();
          vm?.InvalidateRcsCache();
        }
      } else {
        Debug.LogWarning($"[Nova/Engine] {part.partInfo?.name}: gimbalRange={gimbalRange} " +
                         $"but transform '{gimbalTransformName}' not found in model");
      }
    }
  }

  public override void OnActive() {
    if (engine == null) return;
    engine.Active = true;
  }

  /// <summary>
  /// Reset transient FX-detection state after a quickload / revert so
  /// `wasFlowing` doesn't latch a pre-load flameout into a freshly-
  /// restored engine. Waterfall controllers read their values live each
  /// LateUpdate from `Engine.ThrustOutputFraction` / `.Active`, so the
  /// FX themselves don't need any explicit reset here — the closures
  /// see the restored state on their next frame.
  /// </summary>
  public override void OnNovaStateRestored() {
    if (engine == null) return;
    // Reset transient FX-detect state to whatever the restored engine
    // says — so the very-next Update fires an engage/disengage edge
    // only if Active actually flipped, not because we lost track.
    wasActive = engine.Active;
    if (!engine.Active) wasFlowing = false;
  }

  // Hook for subclasses to receive the player throttle each FixedUpdate.
  // Base behaviour: write directly to engine.Throttle (the LP demand).
  // NovaNuclearEngineModule overrides to route the value to
  // engine.PlayerThrottle so the NTR state machine consumes it. Returns
  // true when the value differs from the prior frame — the caller
  // invalidates the vessel solver on a change.
  protected virtual bool ApplyPlayerThrottle(float throttle) {
    if (throttle == lastThrottle) return false;
    lastThrottle = throttle;
    engine.Throttle = throttle;
    return true;
  }

  public void FixedUpdate() {
    // Apply gimbal pose every frame, regardless of activated state.
    // The QP zeroes the deflections when the engine isn't firing
    // (MaxThrottle = NormalizedOutput = 0), so a freshly-shutdown
    // engine returns to its init pose; a running engine takes the
    // attitude solver's commanded deflection. Done before the
    // !activated bail so the bell visually springs back when the
    // engine cuts.
    if (gimbalTransform != null) {
      var pitchDeg = (float)(engine.GimbalPitchDeflection * gimbalRange);
      var yawDeg = (float)(engine.GimbalYawDeflection * gimbalRange);
      gimbalTransform.localRotation =
          initGimbalRot
          * Quaternion.AngleAxis(pitchDeg, Vector3.right)
          * Quaternion.AngleAxis(yawDeg, Vector3.up);
    }

    if (engine == null || !engine.Active) return;

    var vm = vessel.FindVesselModuleImplementing<NovaVesselModule>();
    // StoredCommands gate — when the vessel-level authorization fails
    // (probe ledger empty), the throttle reads as 0 regardless of
    // mainThrottle, matching how SolveAttitude zeroes attitude inputs.
    var throttle = (vm == null || vm.ControlAuthorizedThisTick)
        ? vessel.ctrlState.mainThrottle : 0f;
    if (ApplyPlayerThrottle(throttle)) {
      vm?.Virtual?.Invalidate();
    }

    // Apply thrust force at each thrust transform. The thrust transform
    // is a child of the gimbal pivot (when present), so `t.forward`
    // already reflects this frame's deflection — the lateral force
    // component falls out of the standard `AddForceAtPosition` call.
    // ThrustOutputFraction is virtual on Engine: for plain engines it
    // equals NormalizedOutput; for NuclearEngine it gates on reactor
    // state so idle coolant flow doesn't render as thrust.
    if (part.Rigidbody != null) {
      var thrustForce = (float)(engine.Thrust * engine.ThrustOutputFraction);
      for (int i = 0; i < thrustTransforms.Length; i++) {
        var t = thrustTransforms[i];
        part.AddForceAtPosition(
          -t.forward * thrustForce * thrustMultipliers[i],
          t.position
        );
      }
    }
  }

  public void Update() {
    if (engine == null) return;

    // Fire stock-convention EFFECTS sub-effects so any audio-pack
    // (WaterfallRestock, native-stock, …) that ships AUDIO under
    // engage/disengage/flameout/<running> can hear engine state.
    // We replaced ModuleEngines so nothing else calls part.Effect on
    // these engines.
    bool isActive = engine.Active;
    if (isActive != wasActive) {
      part.Effect(isActive ? "engage" : "disengage", 0f);
      wasActive = isActive;
    }

    if (!isActive) return;

    var output = (float)engine.ThrustOutputFraction;
    var flowing = output > 0;

    if (runningEffectName != null)
      part.Effect(runningEffectName, output);

    // Detect flameout edge: was producing thrust, now starved while the
    // player still wants throttle. `lastThrottle` holds the player input
    // (via ApplyPlayerThrottle), not the engine's internal LP demand —
    // an NTR's idle-coolant flow would otherwise spoof this.
    // NuclearEngine and IonEngine both write Flameout authoritatively
    // in their OnPostSolve; the base Engine (chemical) has no such
    // hook, so this Update-side write is the only Flameout writer for
    // plain liquid engines. Visual flameout pop now belongs to a
    // Waterfall effect modifier keyed off the `flameout` controller
    // (one-frame impulse when `wasFlowing && !flowing`).
    bool flamedOut = wasFlowing && !flowing && lastThrottle > 0;
    if (flamedOut) {
      engine.Flameout = true;
      part.Effect("flameout", 0f);
    } else if (flowing || lastThrottle <= 0) engine.Flameout = false;
    wasFlowing = flowing;
  }

  // Walk the part's EFFECTS sub-blocks and return the first one whose
  // AUDIO (or AUDIO_MULTI_POOL) entry has `loop = true`. Used as the
  // looping engine-running effect. Returns null if nothing matches —
  // engine then just doesn't fire a running sound (no exception, no
  // silent spam of bad part.Effect calls).
  private string DiscoverRunningEffectName() {
    var cfg = part?.partInfo?.partConfig?.GetNode("EFFECTS");
    if (cfg == null) return null;
    for (int i = 0; i < cfg.nodes.Count; i++) {
      var subNode = cfg.nodes[i];
      if (NodeHasLoopingAudio(subNode.GetNodes("AUDIO"))
          || NodeHasLoopingAudio(subNode.GetNodes("AUDIO_MULTI_POOL"))) {
        return subNode.name;
      }
    }
    return null;
  }

  private static bool NodeHasLoopingAudio(ConfigNode[] audioNodes) {
    if (audioNodes == null) return false;
    foreach (var a in audioNodes) {
      bool loop = false;
      if (a.TryGetValue("loop", ref loop) && loop) return true;
    }
    return false;
  }

  // ── Waterfall integration ──────────────────────────────────────────
  //
  // Yields the controllers every Nova engine publishes to its sibling
  // `ModuleWaterfallFX`: throttle (actual thrust-producing fraction,
  // 0..1) and active (ignition flag, 0/1). NTR / Ion subclasses add
  // their kind-specific controllers via `override`. Wired by
  // `WaterfallInitializePatch` postfix on `ModuleWaterfallFX.Initialize`.
  //
  // Closures dereference `Engine` (the typed component accessor) each
  // call, not a captured local — the property reads through the same
  // `engine` field set in OnStart, and re-binds naturally if a future
  // change re-initializes Components on the PartModule.
  public virtual IEnumerable<WaterfallController> CreateWaterfallControllers() {
    yield return new NovaWaterfallController("throttle",
        () => Engine == null ? 0f : (float)Engine.ThrustOutputFraction);
    yield return new NovaWaterfallController("active",
        () => Engine != null && Engine.Active ? 1f : 0f);
  }

  // IEngineStatus implementation. Virtual so NovaNuclearEngineModule
  // can re-shape `isOperational` / `normalizedOutput` around reactor
  // state (its idle-coolant flow makes engine.Throttle non-zero even
  // when not producing thrust).
  public virtual bool isOperational => engine != null && engine.Throttle > 0;
  public virtual float normalizedOutput => engine != null ? (float)engine.NormalizedOutput : 0f;
  public virtual float throttleSetting => engine != null ? (float)engine.Throttle : 0f;
  public virtual string engineName => "Nova";

  public override bool IsStageable() => true;
}
