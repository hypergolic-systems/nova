using System.Linq;
using UnityEngine;
using Nova.Core.Components.Propulsion;

namespace Nova.Components;

public class NovaRcsModule : NovaPartModule, ITorqueProvider {

  [KSPField]
  public string thrusterTransformName = "RCSthruster";

  [KSPField]
  public bool useZaxis;

  private Rcs rcs;
  private Transform[] thrusterTransforms;
  private float lastAggregateThrottle = -1;
  private float[] lastNozzlePower;

  public override void OnStart(StartState state) {
    base.OnStart(state);
    rcs = Components.OfType<Rcs>().First();
    thrusterTransforms = part.FindModelTransforms(thrusterTransformName)
      .Where(t => t.gameObject.activeInHierarchy).ToArray();
    rcs.ThrusterCount = thrusterTransforms.Length;
    lastNozzlePower = new float[thrusterTransforms.Length];

    // ThrusterCount is only known after counting transforms, but OnBuildSolver
    // may have already run with count=0. Flag the solver for a single deferred rebuild.
    var vesselModule = vessel.FindVesselModuleImplementing<NovaVesselModule>();
    if (vesselModule != null) {
      vesselModule.NeedsSolverRebuild = true;
      vesselModule.InvalidateRcsCache();
    }
  }

  /// <summary>
  /// Number of active thruster transforms on this part.
  /// </summary>
  public int ThrusterCount => thrusterTransforms?.Length ?? 0;

  /// <summary>
  /// Per-nozzle max thrust in kN.
  /// </summary>
  public double ThrusterPower => rcs?.ThrusterPower ?? 0;

  /// <summary>
  /// Whether this module uses the Z axis for thrust direction.
  /// </summary>
  public bool UseZaxis => useZaxis;

  /// <summary>
  /// Live thruster transforms. NovaVesselModule reads positions/directions from these.
  /// </summary>
  public Transform[] ThrusterTransforms => thrusterTransforms;

  /// <summary>
  /// The core Rcs component for reading Satisfaction.
  /// </summary>
  public Rcs Rcs => rcs;

  public void GetPotentialTorque(out Vector3 pos, out Vector3 neg) {
    pos = neg = Vector3.zero;
    if (vessel == null || thrusterTransforms == null || rcs == null) return;
    var com = vessel.CoM;
    var refXform = vessel.ReferenceTransform;
    foreach (var t in thrusterTransforms) {
      var dir = useZaxis ? t.forward : t.up;
      var thrust = -dir * (float)rcs.ThrusterPower;
      var lever = t.position - com;
      // Compute torque in world frame, then rotate into vessel-local
      // frame so `pos.x / .y / .z` are torques around vessel pitch /
      // roll / yaw axes — matching SAS's per-axis ctrlState mapping
      // and the convention NovaReactionWheelModule already uses. The
      // previous world-frame components misaligned with the per-axis
      // input whenever the vessel was rotated relative to world.
      var torque = refXform.InverseTransformDirection(Vector3.Cross(lever, thrust));
      pos.x = Mathf.Max(pos.x, torque.x);
      neg.x = Mathf.Max(neg.x, -torque.x);
      pos.y = Mathf.Max(pos.y, torque.y);
      neg.y = Mathf.Max(neg.y, -torque.y);
      pos.z = Mathf.Max(pos.z, torque.z);
      neg.z = Mathf.Max(neg.z, -torque.z);
    }
  }

  /// <summary>
  /// Apply solved per-thruster throttle values. Called by NovaVesselModule
  /// after the vessel-wide RCS LP solve.
  /// </summary>
  private int applyLogCounter;

  public void ApplySolvedThrottles(double[] throttles) {

    var satisfaction = rcs.Satisfaction;
    double totalThrottle = 0;
    bool doLog = applyLogCounter++ % 50 == 0;

    for (int i = 0; i < thrusterTransforms.Length && i < throttles.Length; i++) {
      var t = thrusterTransforms[i];
      var throttle = throttles[i];
      totalThrottle += throttle;

      if (throttle > 1e-6 && part.Rigidbody != null) {
        var dir = useZaxis ? t.forward : t.up;
        var force = (float)(rcs.ThrusterPower * throttle * satisfaction);
        part.AddForceAtPosition(-dir * force, t.position);

        if (doLog)
          NovaLog.Log($"[RCS] Apply: {part.partInfo.name}[{i}] " +
            $"throttle={throttle:F4} sat={satisfaction:F3} force={force:F3}kN " +
            $"dir={-dir} pos={t.position}");
      }

      // Drive per-nozzle FX only when power changes (avoids per-frame effect cycling).
      var power = (float)(throttle * satisfaction);
      if (Mathf.Abs(power - lastNozzlePower[i]) > 0.01f) {
        part.Effect("running", power, i);
        lastNozzlePower[i] = power;
      }
    }

    // Set aggregate throttle for resource demand.
    var count = thrusterTransforms.Length;
    var aggregate = count > 0 ? (float)(totalThrottle / count) : 0f;
    if (Mathf.Abs(aggregate - lastAggregateThrottle) > 1e-4f) {
      lastAggregateThrottle = aggregate;
      rcs.Throttle = aggregate;
      vessel.FindVesselModuleImplementing<NovaVesselModule>()?.Virtual?.Invalidate();
    }
  }

  /// <summary>
  /// Called by NovaVesselModule when RCS is disabled or no input — zero everything.
  /// </summary>
  public void ClearThrottles() {
    if (lastAggregateThrottle != 0) {
      lastAggregateThrottle = 0;
      rcs.Throttle = 0;
      vessel.FindVesselModuleImplementing<NovaVesselModule>()?.Virtual?.Invalidate();
    }
    part.Effect("running", 0f); // idx=-1 clears all nozzles
    if (lastNozzlePower != null)
      System.Array.Clear(lastNozzlePower, 0, lastNozzlePower.Length);
  }
}
