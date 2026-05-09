using System.Linq;
using UnityEngine;
using Nova.Core.Components.Propulsion;

namespace Nova.Components;

public class NovaReactionWheelModule : NovaPartModule, ITorqueProvider {

  private ReactionWheel wheel;

  private Vector3 smoothedTorque;
  private const float TorqueResponseSpeed = 30f;

  public ReactionWheel Wheel => wheel;

  public override void OnStart(StartState state) {
    base.OnStart(state);
    wheel = Components.OfType<ReactionWheel>().First();

    var vesselModule = vessel.FindVesselModuleImplementing<NovaVesselModule>();
    if (vesselModule != null)
      vesselModule.InvalidateRcsCache();
  }

  public void GetPotentialTorque(out Vector3 pos, out Vector3 neg) {
    pos = neg = Vector3.zero;
    if (wheel == null) return;
    // Report effective torque after lerp so SAS tunes its PID correctly.
    float lerpFactor = TorqueResponseSpeed * TimeWarp.fixedDeltaTime;
    pos.x = neg.x = (float)wheel.PitchTorque * lerpFactor;
    pos.y = neg.y = (float)wheel.RollTorque * lerpFactor;
    pos.z = neg.z = (float)wheel.YawTorque * lerpFactor;
  }

  /// <summary>
  /// Apply solved torque with response smoothing. Called by NovaVesselModule
  /// after the attitude solver runs.
  /// </summary>
  public void ApplyTorque(Vector3 targetLocalTorque) {
    smoothedTorque = Vector3.Lerp(smoothedTorque, targetLocalTorque,
      TorqueResponseSpeed * TimeWarp.fixedDeltaTime);
    var worldTorque = vessel.ReferenceTransform.rotation * smoothedTorque;
    part.AddTorque(worldTorque);
  }
}
