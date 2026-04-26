using System.Collections;
using UnityEngine;

namespace Nova.Telemetry;

// Boots Nova's telemetry hooks into Dragonglass's persistent
// telemetry host. The host (named "Dragonglass.Telemetry" and
// DontDestroyOnLoad'd) is created by Dragonglass.Telemetry's own
// Startup.Instantly TelemetryAddon, so we run at the same scope
// and defer attachment via a coroutine — order between two
// Startup.Instantly addons is not guaranteed by KSP.
[KSPAddon(KSPAddon.Startup.Instantly, true)]
public class NovaTelemetryAddon : MonoBehaviour {
  private const string HostName = "Dragonglass.Telemetry";
  private const int MaxAttempts = 60; // ~1 s at default fixed-update cadence

  void Start() {
    StartCoroutine(AttachWhenHostReady());
  }

  private IEnumerator AttachWhenHostReady() {
    for (int attempt = 0; attempt < MaxAttempts; attempt++) {
      var host = GameObject.Find(HostName);
      if (host != null) {
        if (host.GetComponent<NovaSubscriptionManager>() == null) {
          host.AddComponent<NovaSubscriptionManager>();
          NovaLog.Log("Telemetry subscription manager attached to Dragonglass host");
        }
        yield break;
      }
      yield return null;
    }
    NovaLog.LogWarning("Dragonglass.Telemetry host GameObject did not appear within "
        + MaxAttempts + " frames; Nova telemetry topics will not publish");
  }
}
