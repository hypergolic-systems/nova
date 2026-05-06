using System;
using System.Linq;
using Nova.Core.Persistence.Protos;
using Nova.Core.Resources;
using Nova.Core.Science;
using Nova.Core.Systems;

namespace Nova.Core.Components.Science;

// Stock-thermometer instrument. Runs the AtmosphericProfile and
// LongTermStudy experiments.
//
// Files are progressive records living on a DataStorage. Two kinds:
//
//  * Atmospheric Profile (DIRECT): the mod-side PartModule, while the
//    vessel is loaded + active in an applicable layer, calls
//    `WriteAtmReading(altitude, ut)` each tick. The file gets created
//    on first reading and is upserted in place; its fidelity is
//    recomputed from the captured altitude span vs. the layer's
//    effective span.
//
//  * Long-Term Study (INTERPOLATED): the file records `(start_ut,
//    end_ut)` once on creation and the fidelity is computed at read
//    time from current UT. No per-tick update needed — works on
//    unloaded vessels for free. `Update(nowUT)` fires at slice
//    boundaries (via the M1 ValidUntil scheduler) to roll over to
//    the next slice's file.
//
// User toggles `AtmEnabled` / `LtsEnabled` gate file creation +
// updates. Disabling stops updates; the file persists as-is.
public class Thermometer : VirtualComponent {
  // Prefab — set by ComponentFactory from cfg. Not persisted.
  public double EcRate;

  // Player-facing name stamped onto every produced file. Set by the
  // mod-side PartModule from `part.partInfo.title`. Falls back to the
  // class name when the mod side hasn't initialised yet (tests).
  public string InstrumentName = "Thermometer";

  // Experiments this instrument can run. Surfaced to the UI via the
  // 'IN' telemetry frame.
  public static readonly string[] SupportedExperiments = new[] {
    AtmosphericProfileExperiment.ExperimentId,
    LongTermStudyExperiment.ExperimentId,
  };

  // User-controlled enables. Default OFF — players opt each experiment
  // in via the SCI tab toggle.
  public bool AtmEnabled = false;
  public bool LtsEnabled = false;

  // Derived per tick by NovaThermometerModule. Not persisted; the
  // device demand uses these to gate EC consumption to "actually
  // observing right now".
  public bool   AtmActive;
  public bool   LtsActive;
  public string AtmCurrentLayer;     // for find-file-for-current-layer
  public string LtsCurrentSubjectId; // for slice-boundary scheduling

  internal Device device;

  public double Satisfaction => device.Satisfaction;
  public double Activity     => device.Activity;
  public double ActualEcRate => EcRate * Activity;

  public override void OnBuildSystems(VesselSystems systems, StagingFlowSystem.Node node) {
    device = systems.AddDevice(node,
        inputs: new[] { (Resource.ElectricCharge, EcRate) });
    device.Demand = (AtmActive || LtsActive) ? 1.0 : 0.0;
  }

  public override void OnPreSolve() {
    device.Demand = (AtmActive || LtsActive) ? 1.0 : 0.0;
  }

  // Direct-measurement update for atm-profile. Called each tick by
  // NovaThermometerModule.FixedUpdate when active in an applicable
  // layer. Creates the file on first call (reserving its byte cost),
  // upserts on subsequent calls. Extends the file's altitude bounds
  // to include the current sample, and recomputes the cached fidelity
  // snapshot from altitude coverage of the layer's effective span.
  public void WriteAtmReading(string bodyName, string layerName,
                              double altitudeM, double nowUT) {
    var layer = AtmosphericProfileExperiment.LayerByName(bodyName, layerName);
    if (!layer.HasValue) return;
    var bottomAlt = AtmosphericProfileExperiment.LayerBottomAlt(bodyName, layerName);
    if (!bottomAlt.HasValue) return;

    var subject = new SubjectKey(
        AtmosphericProfileExperiment.ExperimentId, bodyName, layerName);
    string subjectId = subject.ToString();

    var storage = FindOrAcquireStorageFor(subjectId, AtmosphericProfileExperiment.FileSizeBytes);
    if (storage == null) return;

    var existing = storage.FindBySubject(subjectId);
    var file = existing ?? new ScienceFile {
      SubjectId       = subjectId,
      ExperimentId    = subject.ExperimentId,
      ProducedAt      = nowUT,
      Instrument      = InstrumentName,
      RecordedMinAltM = altitudeM,
      RecordedMaxAltM = altitudeM,
    };

    if (existing != null) {
      file.RecordedMinAltM = Math.Min(file.RecordedMinAltM, altitudeM);
      file.RecordedMaxAltM = Math.Max(file.RecordedMaxAltM, altitudeM);
    }

    file.Fidelity = AtmosphericProfileExperiment.FidelityFromAltCoverage(
        file.RecordedMinAltM, file.RecordedMaxAltM,
        bottomAlt.Value, layer.Value.topAltMeters);
    // Mark complete the moment the layer is fully covered. Eligibility
    // for transmission to KSC is gated on this flag — partial profiles
    // stay local until the player completes the layer or discards the
    // file. A re-entered layer would reset min/max and re-derive
    // fidelity downward, but is_complete latches to true once set
    // (the file has nothing more to learn).
    if (file.Fidelity >= 1.0) file.IsComplete = true;

    storage.Upsert(file, AtmosphericProfileExperiment.FileSizeBytes);
  }

  // Interpolated-measurement creation for lts. Idempotent: if a file
  // for the subject already lives in storage, leaves it alone (the
  // existing start_ut is canonical — re-creating would erase any
  // earlier-than-now start). Called by NovaThermometerModule on
  // enable / situation change, and by the M1 scheduler at slice
  // boundaries.
  public void EnsureLtsFile(SubjectKey subject, double startUT, double endUT, double sliceDuration) {
    var subjectId = subject.ToString();
    var storage = FindOrAcquireStorageFor(subjectId, LongTermStudyExperiment.FileSizeBytes);
    if (storage == null) return;

    if (storage.HasSubject(subjectId)) return;

    storage.Upsert(new ScienceFile {
      SubjectId             = subjectId,
      ExperimentId          = subject.ExperimentId,
      ProducedAt            = startUT,
      Instrument            = InstrumentName,
      StartUt               = startUT,
      EndUt                 = endUT,
      SliceDurationSeconds  = sliceDuration,
      // Cached snapshot — telemetry recomputes on emit, but save-cli
      // and offline readers see a useful value too.
      Fidelity              = 0,
    }, LongTermStudyExperiment.FileSizeBytes);
  }

  // Slice-boundary rollover. Fired by the M1 scheduler when ValidUntil
  // expires — both for loaded and unloaded vessels. Creates the next
  // slice's file (if enabled+applicable) and re-arms ValidUntil.
  public override void Update(double nowUT) {
    // Promote any LTS file whose slice has elapsed to complete. Cheap
    // scan; eligible only when end_ut ≤ nowUT, which is exactly the
    // slice-boundary case but also catches stale files surviving from
    // past sessions.
    MarkElapsedLtsFilesComplete(nowUT);

    if (!LtsEnabled) {
      ValidUntil = double.PositiveInfinity;
      return;
    }

    var ctx = Vessel?.Context;
    if (ctx == null || ctx.Situation == Situation.None || ctx.BodyYearSeconds <= 0) {
      ValidUntil = double.PositiveInfinity;
      return;
    }

    var subject = LongTermStudyExperiment.SubjectFor(
        ctx.BodyName, ctx.Situation, nowUT, ctx.BodyYearSeconds);
    var sliceEnd = LongTermStudyExperiment.NextSliceBoundary(nowUT, ctx.BodyYearSeconds);
    var sliceDur = LongTermStudyExperiment.SliceDurationFor(ctx.BodyYearSeconds);
    var sliceStart = sliceEnd - sliceDur;

    EnsureLtsFile(subject, sliceStart, sliceEnd, sliceDur);
    LtsCurrentSubjectId = subject.ToString();
    LtsActive = true;
    ValidUntil = sliceEnd;
  }

  // Latch fidelity = 1 + is_complete on every LTS file in this vessel's
  // storage whose slice end has passed. The file's interpolated fidelity
  // is normally a UT-derived read; setting the cached snapshot here gives
  // offline tooling and the transmission queue a stable view.
  private void MarkElapsedLtsFilesComplete(double nowUT) {
    if (Vessel == null) return;
    foreach (var s in Vessel.AllComponents().OfType<DataStorage>()) {
      foreach (var f in s.Files) {
        if (f.ExperimentId != LongTermStudyExperiment.ExperimentId) continue;
        if (f.IsComplete) continue;
        if (f.EndUt > nowUT) continue;
        f.Fidelity = 1.0;
        f.IsComplete = true;
      }
    }
  }

  // Drop any existing file for the given subject from every storage
  // on the vessel. Called when re-enabling an experiment in a regime
  // where a prior in-progress file lives — fidelity tracks an
  // unbroken observation, so the partial prior data is discarded.
  public void DiscardFile(string subjectId) {
    if (Vessel == null || string.IsNullOrEmpty(subjectId)) return;
    foreach (var s in Vessel.AllComponents().OfType<DataStorage>())
      s.RemoveBySubject(subjectId);
  }

  // Walks the vessel for the storage that holds an existing file for
  // this subject (so updates always land in the same place), or the
  // first one with capacity if there's no existing file. Returns null
  // when neither exists — the caller logs and drops.
  private DataStorage FindOrAcquireStorageFor(string subjectId, long sizeBytes) {
    if (Vessel == null) return null;
    DataStorage withCapacity = null;
    foreach (var s in Vessel.AllComponents().OfType<DataStorage>()) {
      if (s.HasSubject(subjectId)) return s;
      if (withCapacity == null && s.CanDeposit(sizeBytes)) withCapacity = s;
    }
    if (withCapacity == null)
      Vessel.Log?.Invoke($"[Science] No storage with capacity for {subjectId}; dropped");
    return withCapacity;
  }

  public override void Load(PartState state) {
    if (state.Thermometer == null) return;
    var t = state.Thermometer;
    AtmEnabled = t.AtmEnabled;
    LtsEnabled = t.LtsEnabled;
    // Don't pre-arm ValidUntil here — the next Tick will figure it
    // out via Update(nowUT).
  }

  public override void Save(PartState state) {
    state.Thermometer = new ThermometerState {
      AtmEnabled = AtmEnabled,
      LtsEnabled = LtsEnabled,
    };
  }
}
