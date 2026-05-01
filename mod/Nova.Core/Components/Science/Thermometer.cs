using System;
using Nova.Core.Persistence.Protos;
using Nova.Core.Resources;
using Nova.Core.Science;

namespace Nova.Core.Components.Science;

// Stock-thermometer instrument. Runs the AtmosphericProfile and
// LongTermStudy experiments. Pure bookkeeping — no live KSP types.
//
// Atmospheric profile is driven entirely by the mod-side PartModule
// (loaded vessels only). Long-term study uses the M1 component
// scheduler primitive: the instrument sets `ValidUntil` to the next
// slice boundary, and `Update(nowUT)` fires on both loaded and
// unloaded vessels at that time.
//
// Satisfaction tracking: the LP solver may re-solve mid-slice (e.g.
// when a battery depletes). VirtualVessel.RecordScienceSatisfaction
// calls this component after each solve to accrue at the OLD
// satisfaction value before the change, then stores the new value.
public class Thermometer : VirtualComponent {

  // --- Structure (cfg-declared) ---
  public double EcRate;        // EC consumed per second when active

  // --- Mutable runtime state ---
  public bool IsActive;        // PartModule sets this each frame

  // --- Long-term study accumulator ---
  public string    ActiveSubjectId;
  public double    AccumulatedActiveSeconds;
  public double    LastUpdateUT;
  public double    LastKnownSatisfaction;
  public Situation LastKnownSituation;
  public uint      LastKnownBody;
  public string    LastKnownBodyName;
  public double    LastKnownBodyYearSeconds;

  // Set by VirtualVessel.InitializeSolver. Routes a file into the first
  // DataStorage on the same vessel with capacity. Returning false means
  // dropped — caller logs. Callable from the scheduler (Update) and
  // from mod-side (OnSubjectChanged) without going through any queue,
  // so no in-flight files outlive a quicksave.
  internal Func<ScienceFile, long, bool> Deposit;

  internal ResourceSolver.Device device;

  public double Satisfaction => device != null ? device.Satisfaction : 0;
  public double Activity     => device != null ? device.Activity     : 0;
  public double ActualEcRate => EcRate * Activity;

  public override void OnBuildSolver(ResourceSolver solver, ResourceSolver.Node node) {
    device = node.AddDevice(ResourceSolver.Priority.Low);
    device.AddInput(Resource.ElectricCharge, EcRate);
    device.Demand = IsActive ? 1.0 : 0.0;
  }

  public override void OnPreSolve() {
    if (device != null) device.Demand = IsActive ? 1.0 : 0.0;
  }

  // Called by VirtualVessel after each LP solve. Accrues elapsed time
  // at the OLD satisfaction up to nowUT, then stamps the new
  // satisfaction. Without this, mid-slice satisfaction flips would lose
  // fidelity (we'd integrate the whole slice at whatever satisfaction
  // happened to be in effect at the slice boundary).
  public void RecordSatisfaction(double nowUT) {
    if (device == null) return;
    double effectiveSat = IsActive ? device.Satisfaction : 0;
    if (Math.Abs(effectiveSat - LastKnownSatisfaction) < 1e-9) return;
    if (!string.IsNullOrEmpty(ActiveSubjectId)) {
      double dt = nowUT - LastUpdateUT;
      if (dt > 0) {
        AccumulatedActiveSeconds += dt * LastKnownSatisfaction;
        LastUpdateUT = nowUT;
      }
    }
    LastKnownSatisfaction = effectiveSat;
  }

  // Slice rollover. Fires when ValidUntil expires — either at the next
  // slice boundary (set by ourselves) or earlier if some external code
  // shortens it (M2 atm-profile doesn't, M3 LTS only sets it forward).
  public override void Update(double nowUT) {
    if (string.IsNullOrEmpty(ActiveSubjectId)) {
      // Defensive: shouldn't be scheduled if there's no active subject.
      ValidUntil = double.PositiveInfinity;
      return;
    }
    if (LastKnownBodyYearSeconds <= 0) {
      // Body-year unknown — can't compute slice. Stop scheduling.
      ValidUntil = double.PositiveInfinity;
      return;
    }

    // Accrue any remaining elapsed time at the current satisfaction.
    double dt = nowUT - LastUpdateUT;
    if (dt > 0) {
      AccumulatedActiveSeconds += dt * LastKnownSatisfaction;
      LastUpdateUT = nowUT;
    }

    // Determine which slice we're now in vs. which one ActiveSubjectId
    // refers to. If they differ, the active slice has ended — emit a
    // file for it and advance.
    if (!SubjectKey.TryParse(ActiveSubjectId, out var active)) {
      ValidUntil = double.PositiveInfinity;
      return;
    }
    int currentSlice = LongTermStudyExperiment.SliceIndexAt(nowUT, LastKnownBodyYearSeconds);
    if (active.SliceIndex.HasValue && active.SliceIndex.Value != currentSlice) {
      EmitLtsFile(active, nowUT);
      AccumulatedActiveSeconds = 0;
      var advanced = new SubjectKey(
          LongTermStudyExperiment.ExperimentId,
          active.BodyName,
          active.Variant,
          currentSlice);
      ActiveSubjectId = advanced.ToString();
    }

    ValidUntil = LongTermStudyExperiment.NextSliceBoundary(nowUT, LastKnownBodyYearSeconds);
  }

  // Called by the mod-side PartModule when the live observable subject
  // changes (situation/body change, or LTS first becomes applicable).
  // Finalises the old subject (partial-fidelity file) and starts a
  // fresh accumulator on the new one. Pass null to exit LTS entirely.
  public void OnSubjectChanged(
      SubjectKey? newSubject,
      Situation newSituation,
      uint newBodyId,
      string newBodyName,
      double newBodyYearSeconds,
      double nowUT) {

    // Accrue any elapsed time at the current satisfaction onto the
    // outgoing subject, then emit if there is one.
    if (!string.IsNullOrEmpty(ActiveSubjectId)) {
      double dt = nowUT - LastUpdateUT;
      if (dt > 0)
        AccumulatedActiveSeconds += dt * LastKnownSatisfaction;
      if (SubjectKey.TryParse(ActiveSubjectId, out var oldKey))
        EmitLtsFile(oldKey, nowUT);
    }

    AccumulatedActiveSeconds = 0;
    LastUpdateUT             = nowUT;
    LastKnownSituation       = newSituation;
    LastKnownBody            = newBodyId;
    LastKnownBodyName        = newBodyName;
    LastKnownBodyYearSeconds = newBodyYearSeconds;

    // Optimistic seed: assume the LP will fully satisfy us until proven
    // otherwise. RecordSatisfaction (post-Solve) corrects if reality
    // differs. Without this, we'd accrue at 0 until the first solve
    // even when EC is plentiful — losing fidelity on every subject
    // start.
    LastKnownSatisfaction = 1.0;

    if (newSubject.HasValue && newBodyYearSeconds > 0) {
      ActiveSubjectId = newSubject.Value.ToString();
      ValidUntil = LongTermStudyExperiment.NextSliceBoundary(nowUT, newBodyYearSeconds);
    } else {
      ActiveSubjectId = null;
      ValidUntil = double.PositiveInfinity;
    }
  }

  private void EmitLtsFile(SubjectKey subject, double nowUT) {
    double sliceDuration = LastKnownBodyYearSeconds / LongTermStudyExperiment.SlicesPerYear;
    double fidelity = sliceDuration > 0
        ? Math.Min(1.0, Math.Max(0.0, AccumulatedActiveSeconds / sliceDuration))
        : 0;
    var file = new ScienceFile {
      SubjectId    = subject.ToString(),
      ExperimentId = subject.ExperimentId,
      Fidelity     = fidelity,
      ProducedAt   = nowUT,
    };
    long sizeBytes = ExperimentRegistry.Instance?.Get(subject.ExperimentId)?.FileSizeBytes ?? 5_000;
    Deposit?.Invoke(file, sizeBytes);
  }

  public override void LoadStructure(PartStructure ps) {
    if (ps.Thermometer == null) return;
    EcRate = ps.Thermometer.EcRate;
  }

  public override void SaveStructure(PartStructure ps) {
    ps.Thermometer = new ThermometerStructure { EcRate = EcRate };
  }

  public override void Load(PartState state) {
    if (state.Thermometer == null) return;
    var t = state.Thermometer;
    ActiveSubjectId          = string.IsNullOrEmpty(t.ActiveSubjectId) ? null : t.ActiveSubjectId;
    AccumulatedActiveSeconds = t.AccumulatedActiveSeconds;
    LastUpdateUT             = t.LastUpdateUt;
    LastKnownSatisfaction    = t.LastKnownSatisfaction;
    LastKnownSituation       = (Situation)t.LastKnownSituation;
    LastKnownBody            = t.LastKnownBody;
    LastKnownBodyName        = t.LastKnownBodyName;
    LastKnownBodyYearSeconds = t.LastKnownBodyYearSeconds;
    // Restore the slice-rollover schedule. If we were running an LTS
    // accumulator, pick up at the next boundary.
    if (!string.IsNullOrEmpty(ActiveSubjectId) && LastKnownBodyYearSeconds > 0)
      ValidUntil = LongTermStudyExperiment.NextSliceBoundary(LastUpdateUT, LastKnownBodyYearSeconds);
    else
      ValidUntil = double.PositiveInfinity;
  }

  public override void Save(PartState state) {
    state.Thermometer = new ThermometerState {
      ActiveSubjectId          = ActiveSubjectId ?? "",
      AccumulatedActiveSeconds = AccumulatedActiveSeconds,
      LastUpdateUt             = LastUpdateUT,
      LastKnownSatisfaction    = LastKnownSatisfaction,
      LastKnownSituation       = (int)LastKnownSituation,
      LastKnownBody            = LastKnownBody,
      LastKnownBodyName        = LastKnownBodyName ?? "",
      LastKnownBodyYearSeconds = LastKnownBodyYearSeconds,
    };
  }

  public override VirtualComponent Clone() {
    var c = new Thermometer {
      EcRate                   = EcRate,
      IsActive                 = IsActive,
      ActiveSubjectId          = ActiveSubjectId,
      AccumulatedActiveSeconds = AccumulatedActiveSeconds,
      LastUpdateUT             = LastUpdateUT,
      LastKnownSatisfaction    = LastKnownSatisfaction,
      LastKnownSituation       = LastKnownSituation,
      LastKnownBody            = LastKnownBody,
      LastKnownBodyName        = LastKnownBodyName,
      LastKnownBodyYearSeconds = LastKnownBodyYearSeconds,
      ValidUntil               = ValidUntil,
    };
    return c;
  }
}
