using System.Linq;
using UnityEngine;
using Nova.Core.Components.Propulsion;

namespace Nova.Components;

public class NovaEngineModule : NovaPartModule, IEngineStatus {

  private Engine engine;
  private bool activated;
  private float lastThrottle = -1;
  private bool wasFlowing;

  private FXGroup runningGroup;
  private FXGroup powerGroup;
  private FXGroup flameoutGroup;

  private Transform[] thrustTransforms;
  private float[] thrustMultipliers;

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

      // Parent FX particles to thrust transforms (like stock AutoPlaceFXGroup).
      PlaceFXAtThrust(runningGroup, thrustTransforms[0]);
      PlaceFXAtThrust(powerGroup, thrustTransforms[0]);
      PlaceFXAtThrust(flameoutGroup, thrustTransforms[0]);
    } else {
      thrustMultipliers = new float[0];
    }
  }

  public override void OnActive() {
    activated = true;
    if (engine != null) engine.Ignited = true;
  }

  public void FixedUpdate() {
    if (!activated) return;

    var throttle = vessel.ctrlState.mainThrottle;
    if (throttle != lastThrottle) {
      lastThrottle = throttle;
      engine.Throttle = throttle;
      vessel.FindVesselModuleImplementing<NovaVesselModule>()?.Virtual?.Invalidate();
    }

    // Apply thrust force at each thrust transform.
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
