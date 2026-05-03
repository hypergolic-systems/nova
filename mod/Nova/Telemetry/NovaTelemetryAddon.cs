using System.Collections;
using Dragonglass.Telemetry.Topics;
using UnityEngine;

namespace Nova.Telemetry;

// Boots Nova's telemetry hooks into Dragonglass's persistent
// telemetry host. The host (named "Dragonglass.Telemetry" and
// DontDestroyOnLoad'd) is created by Dragonglass.Telemetry's own
// Startup.Instantly TelemetryAddon, so we run at the same scope
// and defer attachment via a coroutine — order between two
// Startup.Instantly addons is not guaranteed by KSP.
//
// Two responsibilities:
//   1. Register Nova's topic-class overrides on TopicRegistry.
//      Engine/Stage installers in DG resolve the class to attach
//      via TopicRegistry.Resolve<T>(); registering before they run
//      (Flight scene) is sufficient.
//   2. Attach NovaSubscriptionManager to the host so per-vessel /
//      per-part Nova topics get spun up on subscribe signals.
[KSPAddon(KSPAddon.Startup.Instantly, true)]
public class NovaTelemetryAddon : MonoBehaviour {
  private const string HostName = "Dragonglass.Telemetry";
  private const int MaxAttempts = 60; // ~1 s at default fixed-update cadence

  void Start() {
    StartCoroutine(AttachWhenHostReady());
  }

  private IEnumerator AttachWhenHostReady() {
    // Wait for two preconditions, in any order:
    //   1. The host GameObject exists (named by DG's TelemetryAddon.Awake).
    //   2. TopicRegistry.Instance is set (assigned in DG's TelemetryAddon.Start).
    // KSP doesn't guarantee Start ordering between Startup.Instantly
    // addons, so it's possible to find the host one frame before the
    // registry is ready. Loop until both are present.
    for (int attempt = 0; attempt < MaxAttempts; attempt++) {
      var host = GameObject.Find(HostName);
      if (host != null && TopicRegistry.Instance != null) {
        // Topic overrides — register before any Flight-scene
        // installer runs so Resolve<T>() returns the Nova subclass.
        TopicRegistry.Instance.RegisterOverride<EngineTopic, NovaEngineTopic>();
        TopicRegistry.Instance.RegisterOverride<StageTopic, NovaStageTopic>();
        NovaLog.Log("Topic overrides registered: EngineTopic → NovaEngineTopic, StageTopic → NovaStageTopic");

        if (host.GetComponent<NovaSubscriptionManager>() == null) {
          host.AddComponent<NovaSubscriptionManager>();
          NovaLog.Log("Telemetry subscription manager attached to Dragonglass host");
        }
        if (host.GetComponent<NovaSceneTopic>() == null) {
          host.AddComponent<NovaSceneTopic>();
          NovaLog.Log("NovaScene topic attached to Dragonglass host");
        }
        yield break;
      }
      yield return null;
    }
    NovaLog.LogWarning("Dragonglass telemetry host or registry did not appear within "
        + MaxAttempts + " frames; Nova telemetry topics will not publish");
  }
}
