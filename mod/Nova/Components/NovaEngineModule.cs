using System.Linq;
using UnityEngine;
using Nova.Core.Components.Propulsion;

namespace Nova.Components;

public class NovaEngineModule : NovaPartModule, IEngineStatus, ITorqueProvider {

  // Gimbal config — optional, mirrored from stock ModuleGimbal field
  // names so cfg patches read intuitively. `gimbalRange` is in degrees
  // here (matching the stock convention); `Engine.GimbalRangeRad`
  // stores the converted radians. `gimbalTransformName` names the
  // pivot transform in the model — typically "Gimbal" for stock parts.
  [KSPField] public float gimbalRange = 0f;
  [KSPField] public string gimbalTransformName = "Gimbal";

  private Engine engine;
  private bool activated;
  private float lastThrottle = -1;
  private bool wasFlowing;

  private FXGroup runningGroup;
  private FXGroup powerGroup;
  private FXGroup flameoutGroup;

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

    var audioSource = gameObject.GetComponent<AudioSource>()
      ?? gameObject.AddComponent<AudioSource>();

    runningGroup = part.findFxGroup("running");
    runningGroup?.begin(audioSource);
    powerGroup = part.findFxGroup("power");
    powerGroup?.begin(audioSource);
    flameoutGroup = part.findFxGroup("flameout");
    flameoutGroup?.begin(audioSource);

    thrustTransforms = part.FindModelTransforms("thrustTransform");
    if (thrustTransforms.Length > 0) {
      var mult = 1f / thrustTransforms.Length;
      thrustMultipliers = Enumerable.Repeat(mult, thrustTransforms.Length).ToArray();

      // Capture nominal thrust direction (gimbal at rest) in part-local
      // frame. Same expression FixedUpdate uses for force application,
      // so slot-torque calcs stay sign-consistent with the physics.
      nominalThrustDirLocal =
          part.transform.InverseTransformDirection(-thrustTransforms[0].forward);

      // Parent FX particles to thrust transforms (like stock AutoPlaceFXGroup).
      PlaceFXAtThrust(runningGroup, thrustTransforms[0]);
      PlaceFXAtThrust(powerGroup, thrustTransforms[0]);
      PlaceFXAtThrust(flameoutGroup, thrustTransforms[0]);
    } else {
      thrustMultipliers = new float[0];
    }


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
    activated = true;
    if (engine != null) engine.Ignited = true;
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

    if (!activated) return;

    var throttle = vessel.ctrlState.mainThrottle;
    if (throttle != lastThrottle) {
      lastThrottle = throttle;
      engine.Throttle = throttle;
      vessel.FindVesselModuleImplementing<NovaVesselModule>()?.Virtual?.Invalidate();
    }

    // Apply thrust force at each thrust transform. The thrust transform
    // is a child of the gimbal pivot (when present), so `t.forward`
    // already reflects this frame's deflection — the lateral force
    // component falls out of the standard `AddForceAtPosition` call.
    if (part.Rigidbody != null) {
      var thrustForce = (float)(engine.Thrust * engine.NormalizedOutput);
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
    if (!activated) return;

    var output = (float)engine.NormalizedOutput;
    var flowing = output > 0;

    runningGroup?.setActive(flowing);
    powerGroup?.setActive(flowing);

    if (flowing) {
      // Match stock ModuleEngines power scaling.
      var runningPower = Mathf.Clamp(output * 2f, 0.5f, 1.75f);
      var powerPower = Mathf.Lerp(0.45f, 1.2f, output);
      runningGroup?.SetPower(runningPower);
      powerGroup?.SetPower(powerPower);
    }

    // Flameout: was flowing, now starved while throttle is still up.
    // Surface to the virtual component so NovaEngineTopic samples it
    // from a single source. Cleared when the engine recovers (next
    // frame where flow ≥ epsilon) or when throttle drops to zero.
    bool flamedOut = wasFlowing && !flowing && engine.Throttle > 0;
    if (flamedOut) {
      flameoutGroup?.Burst();
      engine.Flameout = true;
    } else if (flowing || engine.Throttle <= 0) {
      engine.Flameout = false;
    }
    wasFlowing = flowing;
  }

  private static void PlaceFXAtThrust(FXGroup group, Transform thruster) {
    if (group == null || thruster == null) return;
    foreach (var light in group.lights) {
      light.transform.parent = thruster;
      light.transform.localPosition = Vector3.zero;
      light.transform.localRotation = Quaternion.identity;
    }
    foreach (var ps in group.fxEmittersNewSystem) {
      ps.transform.parent = thruster;
      ps.transform.localPosition = Vector3.zero;
      ps.transform.localRotation = Quaternion.identity;
      ps.transform.Rotate(-90f, 0f, 0f);
    }
  }

  // IEngineStatus implementation
  public bool isOperational => engine != null && engine.Throttle > 0;
  public float normalizedOutput => engine != null ? (float)engine.NormalizedOutput : 0f;
  public float throttleSetting => engine != null ? (float)engine.Throttle : 0f;
  public string engineName => "Nova";
}
