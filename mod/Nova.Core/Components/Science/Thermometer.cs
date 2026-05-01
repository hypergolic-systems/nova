using Nova.Core.Persistence.Protos;
using Nova.Core.Resources;
using Nova.Core.Science;

namespace Nova.Core.Components.Science;

// Stock-thermometer instrument. Runs the AtmosphericProfile and
// LongTermStudy experiments. M2 wires the EC device + atm-profile
// emit path; M3 fills in long-term study accumulation in Update().
//
// Pure bookkeeping — no live KSP types. The mod-side
// NovaThermometerModule pushes state in (IsActive, current observable)
// and triggers EmitFile when atm-profile transitions.
public class Thermometer : VirtualComponent {

  // --- Structure (cfg-declared) ---
  public double EcRate;        // EC consumed per second when active

  // --- Mutable runtime state ---
  public bool IsActive;        // PartModule sets this each frame

  // --- Long-term study accumulator (M3) ---
  public string ActiveSubjectId;
  public double AccumulatedActiveSeconds;
  public double LastUpdateUT;
  public double LastKnownSatisfaction;
  public Situation LastKnownSituation;
  public uint   LastKnownBody;
  public double LastKnownBodyYearSeconds;

  // LP device. Drains EC at EcRate * Demand. PartModule gates Demand
  // via IsActive in OnPreSolve.
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
    ActiveSubjectId          = t.ActiveSubjectId;
    AccumulatedActiveSeconds = t.AccumulatedActiveSeconds;
    LastUpdateUT             = t.LastUpdateUt;
    LastKnownSatisfaction    = t.LastKnownSatisfaction;
    LastKnownSituation       = (Situation)t.LastKnownSituation;
    LastKnownBody            = t.LastKnownBody;
  }

  public override void Save(PartState state) {
    state.Thermometer = new ThermometerState {
      ActiveSubjectId          = ActiveSubjectId ?? "",
      AccumulatedActiveSeconds = AccumulatedActiveSeconds,
      LastUpdateUt             = LastUpdateUT,
      LastKnownSatisfaction    = LastKnownSatisfaction,
      LastKnownSituation       = (int)LastKnownSituation,
      LastKnownBody            = LastKnownBody,
    };
  }

  public override VirtualComponent Clone() {
    return new Thermometer {
      EcRate = EcRate,
      IsActive = IsActive,
      ActiveSubjectId = ActiveSubjectId,
      AccumulatedActiveSeconds = AccumulatedActiveSeconds,
      LastUpdateUT = LastUpdateUT,
      LastKnownSatisfaction = LastKnownSatisfaction,
      LastKnownSituation = LastKnownSituation,
      LastKnownBody = LastKnownBody,
      LastKnownBodyYearSeconds = LastKnownBodyYearSeconds,
    };
  }
}
