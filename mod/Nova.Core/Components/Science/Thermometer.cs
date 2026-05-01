using System;
using System.Linq;
using Nova.Core.Persistence.Protos;
using Nova.Core.Resources;
using Nova.Core.Science;

namespace Nova.Core.Components.Science;

// Stock-thermometer instrument. Runs the AtmosphericProfile and
// LongTermStudy experiments.
//
// Atmospheric profile is loaded-vessel-only: the mod-side PartModule
// edge-detects layer crossings and calls EmitAtmFile.
//
// Long-term study is event-driven via the M1 component scheduler:
// ValidUntil = next slice boundary; Update fires there on both loaded
// and unloaded vessels.
public class Thermometer : VirtualComponent {
  // Prefab — set by ComponentFactory from cfg. Not persisted.
  public double EcRate;

  // Player-facing name stamped onto every emitted file. Set by the
  // mod-side PartModule from `part.partInfo.title` so the UI can show
  // "2HOT Thermometer" without needing to round-trip through the
  // structure topic. Falls back to the class name when the mod side
  // hasn't initialised yet (tests).
  public string InstrumentName = "Thermometer";

  // Experiments this instrument can run. Surfaced to the UI via the
  // 'IN' telemetry frame so the SCI tab's Instruments section shows
  // the player what each device is capable of producing. Order is
  // stable for deterministic UI rendering.
  public static readonly string[] SupportedExperiments = new[] {
    AtmosphericProfileExperiment.ExperimentId,
    LongTermStudyExperiment.ExperimentId,
  };

  // State.
  public bool   AtmActive;
  public bool   LtsActive;
  public string LtsSubjectId;
  public double LtsAccumulatedSeconds;
  public double LtsLastUpdateUT;

  internal ResourceSolver.Device device;

  public double Satisfaction => device.Satisfaction;
  public double Activity     => device.Activity;
  public double ActualEcRate => EcRate * Activity;

  public override void OnBuildSolver(ResourceSolver solver, ResourceSolver.Node node) {
    device = node.AddDevice(ResourceSolver.Priority.Low);
    device.AddInput(Resource.ElectricCharge, EcRate);
    device.Demand = (AtmActive || LtsActive) ? 1.0 : 0.0;
  }

  public override void OnPreSolve() {
    device.Demand = (AtmActive || LtsActive) ? 1.0 : 0.0;
  }

  // Mod-side hook: a layer crossing was detected. Write a fidelity-1.0
  // file to the first DataStorage on the vessel that has capacity.
  public void EmitAtmFile(SubjectKey subject, double nowUT) {
    Deposit(new ScienceFile {
      SubjectId    = subject.ToString(),
      ExperimentId = subject.ExperimentId,
      Fidelity     = 1.0,
      ProducedAt   = nowUT,
      Instrument   = InstrumentName,
    }, AtmosphericProfileExperiment.FileSizeBytes);
  }

  // Mod-side hook: long-term study became applicable in this body+
  // situation, OR the situation/body just changed mid-study. Finalises
  // the outgoing slice (partial-fidelity emit) and starts a fresh
  // accumulator on the new subject. Pass null to exit LTS entirely.
  public void StartOrSwitchLts(SubjectKey? newSubject, double nowUT) {
    if (LtsActive)
      FinaliseAndEmitCurrentSlice(nowUT);

    if (newSubject.HasValue) {
      LtsActive             = true;
      LtsSubjectId          = newSubject.Value.ToString();
      LtsAccumulatedSeconds = 0;
      LtsLastUpdateUT       = nowUT;
      ValidUntil            = LongTermStudyExperiment.NextSliceBoundary(
                                  nowUT, Vessel.Context.BodyYearSeconds);
    } else {
      LtsActive    = false;
      LtsSubjectId = null;
      ValidUntil   = double.PositiveInfinity;
    }
  }

  // Slice rollover. Fired by VirtualVessel.Tick when ValidUntil expires.
  public override void Update(double nowUT) {
    FinaliseAndEmitCurrentSlice(nowUT);

    // Begin the next slice on the same body+situation.
    var c = Vessel.Context;
    var next = LongTermStudyExperiment.SubjectFor(
        c.BodyName, c.Situation, nowUT, c.BodyYearSeconds);
    LtsSubjectId          = next.ToString();
    LtsAccumulatedSeconds = 0;
    LtsLastUpdateUT       = nowUT;
    ValidUntil            = LongTermStudyExperiment.NextSliceBoundary(
                                nowUT, Vessel.Context.BodyYearSeconds);
  }

  private void FinaliseAndEmitCurrentSlice(double nowUT) {
    double dt = nowUT - LtsLastUpdateUT;
    LtsAccumulatedSeconds += dt * device.Satisfaction;

    double sliceDuration = LongTermStudyExperiment.SliceDurationFor(Vessel.Context.BodyYearSeconds);
    double fidelity = Math.Min(1.0, Math.Max(0.0, LtsAccumulatedSeconds / sliceDuration));

    Deposit(new ScienceFile {
      SubjectId    = LtsSubjectId,
      ExperimentId = LongTermStudyExperiment.ExperimentId,
      Fidelity     = fidelity,
      ProducedAt   = nowUT,
      Instrument   = InstrumentName,
    }, LongTermStudyExperiment.FileSizeBytes);
  }

  private void Deposit(ScienceFile file, long sizeBytes) {
    var storage = Vessel.AllComponents().OfType<DataStorage>()
        .FirstOrDefault(s => s.CanDeposit(sizeBytes));
    if (storage == null) {
      Vessel.Log?.Invoke($"[Science] No storage with capacity for {file.SubjectId}; dropped");
      return;
    }
    storage.Deposit(file, sizeBytes);
  }

  public override void Load(PartState state) {
    if (state.Thermometer == null) return;
    var t = state.Thermometer;
    AtmActive             = t.AtmActive;
    LtsActive             = t.LtsActive;
    LtsSubjectId          = t.LtsSubjectId;
    LtsAccumulatedSeconds = t.LtsAccumulatedSeconds;
    LtsLastUpdateUT       = t.LtsLastUpdateUt;
    if (LtsActive)
      ValidUntil = LongTermStudyExperiment.NextSliceBoundary(
          LtsLastUpdateUT, Vessel.Context.BodyYearSeconds);
  }

  public override void Save(PartState state) {
    state.Thermometer = new ThermometerState {
      AtmActive             = AtmActive,
      LtsActive             = LtsActive,
      LtsSubjectId          = LtsSubjectId ?? "",
      LtsAccumulatedSeconds = LtsAccumulatedSeconds,
      LtsLastUpdateUt       = LtsLastUpdateUT,
    };
  }
}
