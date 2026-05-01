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

    // Atmospheric Profile — edge-detected layer transitions.
    var atmExp = registry.Get(AtmosphericProfileExperiment.ExperimentId);
    SubjectKey? atmSubject = atmExp?.ResolveSubject(ctx);
    if (atmSubject.HasValue && !atmSubject.Equals(lastAtmSubject)) {
      EmitFile(atmSubject.Value, atmExp.FileSizeBytes, fidelity: 1.0, ut: ctx.UT);
    }
    lastAtmSubject = atmSubject;

    // Long-term study — situation/body change detection. Subject change
    // finalises the old slice's accumulator (partial-fidelity file)
    // and starts a fresh one. The slice-rollover logic itself lives in
    // Thermometer.Update, scheduled via VirtualComponent.ValidUntil.
    var ltsExp = registry.Get(LongTermStudyExperiment.ExperimentId);
    SubjectKey? ltsSubject = ltsExp?.ResolveSubject(ctx);
    if (ltsSubject.HasValue && !SubjectsMatchSituationAndBody(thermometer, ltsSubject.Value, ctx)) {
      thermometer.OnSubjectChanged(
          ltsSubject,
          ctx.Situation,
          ctx.BodyId,
          ctx.BodyName,
          ctx.BodyYearSeconds,
          ctx.UT);
    } else if (!ltsSubject.HasValue && !string.IsNullOrEmpty(thermometer.ActiveSubjectId)) {
      // No longer applicable — exit LTS, finalise current accumulator.
      thermometer.OnSubjectChanged(null, Situation.None, 0, "", 0, ctx.UT);
    }

    thermometer.IsActive = atmSubject.HasValue || ltsSubject.HasValue;
  }

  // True iff the thermometer's active LTS subject already matches the
  // current observable's situation+body. Slice-index alone changing
  // does NOT count — that's a slice rollover, handled by Update, not a
  // subject change.
  private static bool SubjectsMatchSituationAndBody(Thermometer t, SubjectKey newKey, SubjectContext ctx) {
    return t.LastKnownBody == ctx.BodyId
        && t.LastKnownSituation == ctx.Situation
        && !string.IsNullOrEmpty(t.ActiveSubjectId)
        && SubjectKey.TryParse(t.ActiveSubjectId, out var active)
        && active.BodyName == newKey.BodyName
        && active.Variant == newKey.Variant;
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
