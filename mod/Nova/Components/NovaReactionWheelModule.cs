using System.Linq;
using UnityEngine;
using Nova.Core.Components.Propulsion;

namespace Nova.Components;

public class NovaReactionWheelModule : NovaPartModule, ITorqueProvider {

  private ReactionWheel wheel;

  // Mirrors stock ModuleReactionWheel: per-tick lerp toward the target
  // torque ramps the response in over ~3 ticks. Without it, full peak
  // torque arrives on tick 1 and stock SAS's PID — tuned against a
  // smoothed first-order response — oscillates at saturation.
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
    pos.x = neg.x = (float)wheel.PitchTorque;
    pos.y = neg.y = (float)wheel.RollTorque;
    pos.z = neg.z = (float)wheel.YawTorque;
  }

  // Called by NovaVesselModule's OnFlyByWire callback.
  public void ApplyTorque(Vector3 targetLocalTorque) {
    smoothedTorque = Vector3.Lerp(smoothedTorque, targetLocalTorque,
      TorqueResponseSpeed * TimeWarp.fixedDeltaTime);
    var worldTorque = vessel.ReferenceTransform.rotation * smoothedTorque;
    part.AddTorque(worldTorque);
  }
}
