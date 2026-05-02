using Nova.Core.Science;
using UnityEngine;

namespace Nova.Components;

// Runs once at MainMenu (after FlightGlobals.Bodies has been loaded
// from the active save's planetary system) to swap the hardcoded
// AtmosphericProfileExperiment.Layer pressure bounds for live values
// pulled from each body's CelestialBody atmosphere curve.
//
// Without this, the layer table's pre-baked pressure approximations
// drift from KSP's actual atmosphere — a vessel that ascends through
// "all of the troposphere" by KSP's curve might score < 100% (or
// > 100%, clamped to 1.0 well before the layer top) under the static
// table. With it, "100% captured" matches the real boundary.
[KSPAddon(KSPAddon.Startup.MainMenu, true)]
public class NovaScienceInit : MonoBehaviour {

  void Start() {
    if (FlightGlobals.Bodies == null) return;

    foreach (var bodyName in AtmosphericProfileExperiment.KnownBodies) {
      var body = FlightGlobals.Bodies.Find(b => b.bodyName == bodyName);
      if (body == null || !body.atmosphere) continue;
      AtmosphericProfileExperiment.RefreshLayerPressures(
          bodyName,
          // CelestialBody.GetPressure returns kPa at altitude.
          // Convert to standard atmospheres (1 atm = 101.325 kPa).
          alt => body.GetPressure(alt) / 101.325);
    }
    Destroy(gameObject);
  }
}
