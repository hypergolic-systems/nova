using System.Linq;
using Nova.Core.Components.Science;
using Nova.Core.Persistence.Protos;
using Nova.Core.Science;

namespace Nova.Components;

// KSP-side adapter for the Thermometer instrument. Owns the live KSP
// context (Vessel, CelestialBody, UT) and feeds it to the pure-data
// experiments. Atmospheric Profile edge detection lives here — when
// the vessel transits a layer boundary, a fidelity-1.0 file is
// deposited into the first DataStorage on the vessel.
//
// Long-term study (M3) integration: this module also pushes the
// current situation+body snapshot down to the core Thermometer so its
// Update() can keep accumulating on unloaded vessels.
public class NovaThermometerModule : NovaPartModule {

  // Last-emitted atm-profile subject for edge detection. null = vessel
  // started outside any layer (vacuum / wrong body), so the first time
  // we enter a layer we emit.
  private SubjectKey? lastAtmSubject;

  public void FixedUpdate() {
    if (!HighLogic.LoadedSceneIsFlight) return;
    if (vessel == null || Components == null) return;

    var thermometer = Components.OfType<Thermometer>().FirstOrDefault();
    if (thermometer == null) return;

    var registry = ExperimentRegistry.Instance;
    if (registry == null) return;  // mod not fully initialised yet

    var ctx = BuildSubjectContext();

    var atmExp = registry.Get(AtmosphericProfileExperiment.ExperimentId);
    SubjectKey? atmSubject = atmExp?.ResolveSubject(ctx);

    if (atmSubject.HasValue && !atmSubject.Equals(lastAtmSubject)) {
      EmitFile(atmSubject.Value, atmExp.FileSizeBytes, fidelity: 1.0, ut: ctx.UT);
    }
    lastAtmSubject = atmSubject;

    // For M2, "active" tracks atm-profile applicability. M3 ORs in
    // long-term-study applicability when the LTS accumulator path lands.
    thermometer.IsActive = atmSubject.HasValue;
  }

  private SubjectContext BuildSubjectContext() {
    var body = vessel.mainBody;
    return new SubjectContext(
      bodyId: (uint)body.flightGlobalsIndex,
      bodyName: body.bodyName,
      situation: (Situation)(int)ScienceUtil.GetExperimentSituation(vessel),
      altitude: vessel.altitude,
      pressure: vessel.staticPressurekPa / 101.325, // kPa → atm
      ut: Planetarium.GetUniversalTime(),
      bodyYearSeconds: ResolveBodyYear(body));
  }

  private void EmitFile(SubjectKey subject, long sizeBytes, double fidelity, double ut) {
    var mod = vessel.FindVesselModuleImplementing<NovaVesselModule>();
    if (mod?.Virtual == null) return;
    var storage = mod.Virtual.AllComponents()
        .OfType<DataStorage>()
        .FirstOrDefault(s => s.CanDeposit(sizeBytes));
    if (storage == null) {
      NovaLog.Log($"[Science] No storage with capacity for {subject}; dropped");
      return;
    }
    var file = new ScienceFile {
      SubjectId    = subject.ToString(),
      ExperimentId = subject.ExperimentId,
      Fidelity     = fidelity,
      ProducedAt   = ut,
    };
    storage.Deposit(file, sizeBytes);
    NovaLog.Log($"[Science] Emitted {subject} fidelity={fidelity:F2} → storage on part {part.persistentId}");
  }

  // Walk CelestialBody.referenceBody chain root-ward to find the planet
  // that orbits the Sun, then return its orbital period. Cached lookup
  // is via FlightGlobals.Bodies (small list — full scan is fine).
  private static double ResolveBodyYear(CelestialBody body) {
    var byName = FlightGlobals.Bodies.ToDictionary(b => b.bodyName, b => b);
    return BodyYear.For(
      body.bodyName,
      bn => {
        if (!byName.TryGetValue(bn, out var b)) return null;
        var rb = b.referenceBody;
        if (rb == null || rb == b) return null;
        return rb.bodyName;
      },
      bn => byName.TryGetValue(bn, out var b) && b.orbit != null ? b.orbit.period : 0);
  }
}
