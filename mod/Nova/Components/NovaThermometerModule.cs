using System.Linq;
using Nova.Core.Components.Science;
using Nova.Core.Science;

namespace Nova.Components;

// KSP-side adapter for the Thermometer. Loaded-vessel only: PartModules
// don't FixedUpdate when unloaded. Vessel-level body context is
// refreshed by NovaVesselModule on every tick (loaded or not), so
// slice-rollover keeps working in the background.
//
// Responsibilities here:
//   1. atm-profile edge-detection on layer crossings,
//   2. LTS subject change detection (situation/body change → finalise
//      old slice + start new accumulator).
public class NovaThermometerModule : NovaPartModule {

  private string lastAtmLayer;

  public void FixedUpdate() {
    if (!HighLogic.LoadedSceneIsFlight || vessel == null || Components == null) return;

    var thermometer = Components.OfType<Thermometer>().FirstOrDefault();
    if (thermometer == null) return;

    var c = thermometer.Vessel.Context;
    double ut = Planetarium.GetUniversalTime();

    // --- Atmospheric Profile: edge-detected layer crossings.
    var atmSubject = AtmosphericProfileExperiment.SubjectAt(c.BodyName, c.Altitude);
    var currentLayer = atmSubject?.Variant;
    if (currentLayer != null && currentLayer != lastAtmLayer)
      thermometer.EmitAtmFile(atmSubject.Value, ut);
    lastAtmLayer = currentLayer;
    thermometer.AtmActive = currentLayer != null;

    // --- Long-Term Study: situation/body change → finalise + restart.
    var ltsSubject = c.Situation != Situation.None && c.BodyYearSeconds > 0
        ? (SubjectKey?)LongTermStudyExperiment.SubjectFor(c.BodyName, c.Situation, ut, c.BodyYearSeconds)
        : null;
    bool needSwitch = ltsSubject.HasValue
        ? !LtsSubjectMatchesBodyAndSituation(thermometer.LtsSubjectId, c)
        : thermometer.LtsActive;
    if (needSwitch)
      thermometer.StartOrSwitchLts(ltsSubject, ut);
  }

  private static bool LtsSubjectMatchesBodyAndSituation(string subjectId, Nova.Core.Components.IVesselContext c) {
    if (string.IsNullOrEmpty(subjectId)) return false;
    if (!SubjectKey.TryParse(subjectId, out var key)) return false;
    return key.BodyName == c.BodyName && key.Variant == c.Situation.ToString();
  }
}
