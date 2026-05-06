using System;
using System.Linq;
using Nova.Core.Components.Science;
using Nova.Core.Science;

namespace Nova.Components;

// KSP-side adapter for the Thermometer. Loaded-vessel only: PartModules
// don't FixedUpdate when unloaded.
//
// Atm-profile is direct-measurement: each tick (loaded + active + in
// layer) we push an altitude reading into the file via
// `WriteAtmReading`. Files exist on storage as soon as observation
// begins; toggling the experiment off freezes them but doesn't delete.
//
// LTS is interpolated: file creation happens on enable / situation
// change / slice rollover. No per-tick file update needed — the file's
// (start_ut, end_ut) drives interpolation at read time, which works on
// unloaded vessels too via the M1 ValidUntil scheduler in
// VirtualVessel.Tick.
public class NovaThermometerModule : NovaPartModule {

  // Per-frame state for atm-profile layer-boundary snap. We need both
  // previous altitude (to derive ascent/descent direction) and previous
  // layer (to detect entry/exit). Reset whenever the experiment is
  // disabled so a re-enable starts fresh.
  private double prevAtmAltitude = double.NaN;
  private string prevAtmLayer = null;

  public void FixedUpdate() {
    if (!HighLogic.LoadedSceneIsFlight || vessel == null || Components == null) return;

    var thermometer = Components.OfType<Thermometer>().FirstOrDefault();
    if (thermometer == null) return;

    // Stamp the part title once so files carry a player-facing name
    // without needing live KSP context elsewhere.
    if (thermometer.InstrumentName == "Thermometer" && part?.partInfo?.title != null)
      thermometer.InstrumentName = part.partInfo.title;

    var c = thermometer.Vessel.Context;
    double ut = Planetarium.GetUniversalTime();

    UpdateAtmosphericProfile(thermometer, c, ut);
    UpdateLongTermStudy(thermometer, c, ut);
  }

  // Direct-measurement update. While enabled and in an applicable
  // layer, push a reading into the file each tick. WriteAtmReading
  // creates-or-upserts the file; layer-boundary transitions trigger
  // a SnapAtmBoundary call so recorded_min/max latch on the exact
  // layer edge instead of the FixedUpdate-discrete sample value.
  private void UpdateAtmosphericProfile(
      Thermometer thermometer,
      Nova.Core.Components.IVesselContext c,
      double ut) {
    if (!thermometer.AtmEnabled) {
      thermometer.AtmActive = false;
      thermometer.AtmCurrentLayer = null;
      prevAtmAltitude = double.NaN;
      prevAtmLayer = null;
      return;
    }

    var newLayer = AtmosphericProfileExperiment.LayerAt(c.BodyName, c.Altitude);
    thermometer.AtmCurrentLayer = newLayer;
    thermometer.AtmActive = newLayer != null;

    // First tick after enable has no prior altitude — assume ascending
    // (the typical launch case). Subsequent ticks use real direction.
    bool ascending = double.IsNaN(prevAtmAltitude) || c.Altitude > prevAtmAltitude;
    bool layerChanged = newLayer != prevAtmLayer;

    // Exit snap: rocket left prevAtmLayer this tick. Snap the boundary
    // it crossed on the way out (top if ascending, floor if descending).
    if (layerChanged && prevAtmLayer != null) {
      thermometer.SnapAtmBoundary(c.BodyName, prevAtmLayer, snapTop: ascending);
    }

    if (newLayer != null) {
      thermometer.WriteAtmReading(c.BodyName, newLayer, c.Altitude, ut);
      // Entry snap: rocket entered newLayer this tick. Snap the boundary
      // it crossed on the way in (floor if ascending, top if descending).
      // After WriteAtmReading so the file is guaranteed to exist.
      if (layerChanged) {
        thermometer.SnapAtmBoundary(c.BodyName, newLayer, snapTop: !ascending);
      }
    }

    prevAtmLayer = newLayer;
    prevAtmAltitude = c.Altitude;
  }

  // Interpolated-measurement creation. We only need to ensure a file
  // exists for the current slice; once created its fidelity
  // interpolates against UT for free. The M1 scheduler (Update) takes
  // care of slice boundaries.
  private static void UpdateLongTermStudy(
      Thermometer thermometer,
      Nova.Core.Components.IVesselContext c,
      double ut) {
    if (!thermometer.LtsEnabled) {
      thermometer.LtsActive = false;
      thermometer.LtsCurrentSubjectId = null;
      thermometer.ValidUntil = double.PositiveInfinity;
      return;
    }

    if (c.Situation == Situation.None || c.BodyYearSeconds <= 0) {
      thermometer.LtsActive = false;
      thermometer.LtsCurrentSubjectId = null;
      thermometer.ValidUntil = double.PositiveInfinity;
      return;
    }

    var subject = LongTermStudyExperiment.SubjectFor(
        c.BodyName, c.Situation, ut, c.BodyYearSeconds);
    var sliceEnd = LongTermStudyExperiment.NextSliceBoundary(ut, c.BodyYearSeconds);
    var sliceDur = LongTermStudyExperiment.SliceDurationFor(c.BodyYearSeconds);
    var sliceStart = sliceEnd - sliceDur;

    // EnsureLtsFile is idempotent — first call creates with start_ut =
    // sliceStart (the file represents observation from this point on).
    // Re-running the same subject leaves the existing file's start_ut
    // intact, so a player who starts mid-slice gets a partial file.
    var subjectIdString = subject.ToString();
    if (subjectIdString != thermometer.LtsCurrentSubjectId) {
      // Subject just changed (situation flip or first call) — start
      // the new file with start_ut = now (the experiment was just
      // enabled / just became applicable, not since slice start).
      thermometer.EnsureLtsFile(subject, ut, sliceEnd, sliceDur);
    } else {
      // Existing subject — ensure it's still tracked. For safety;
      // EnsureLtsFile is a no-op when the file already exists.
      thermometer.EnsureLtsFile(subject, sliceStart, sliceEnd, sliceDur);
    }

    thermometer.LtsCurrentSubjectId = subjectIdString;
    thermometer.LtsActive = true;
    thermometer.ValidUntil = sliceEnd;
  }
}
