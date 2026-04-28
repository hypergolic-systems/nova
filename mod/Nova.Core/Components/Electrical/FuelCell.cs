using Nova.Core.Persistence.Protos;
using Nova.Core.Resources;

namespace Nova.Core.Components.Electrical;

// PEM-style fuel cell. Modelled as two coupled LP devices, mirroring
// Engine + alternator:
//
//   • `device`  consumes LH₂ + LOx at full-demand volumetric rates.
//   • `output`  produces EC at the rated wattage, with `parent = device`
//                so its activity is gated by the input device.
//
// We split rather than declaring a single hybrid device because the
// coefficient ratio between µL/s reactant flow and kW EC output (~10⁶)
// trips GLOP's numerical tolerances and returns ABNORMAL. The Engine
// alternator pattern keeps each device's conservation coefficients
// at homogeneous scale.
//
// Auto-controlled by VirtualVessel.UpdateFuelCellDevices:
//   - ON  when vessel-wide battery SoC <  20% (or no batteries present)
//   - OFF when vessel-wide battery SoC >  80%
//   - holds state otherwise (hysteresis)
//
// `IsActive` persists across save/load so the half-on-half-off state of
// the hysteresis loop survives a quickload. `ValidUntilSeconds` is the
// orchestrator's forecast of when the next state flip will fire given
// the current vessel-wide net charging rate; it's also written into
// Device.ValidUntil so the solver re-evaluates at the threshold crossing.
public class FuelCell : VirtualComponent {
  // Config (from prefab MODULE — populated by ComponentFactory.CreateFuelCell).
  public double Lh2Rate;   // L/s of LH₂ at full demand
  public double LoxRate;   // L/s of LOx at full demand
  public double EcOutput;  // W (EC/s) at full demand

  // Hysteresis state. Set by VirtualVessel each tick; persisted in
  // FuelCellState so we don't lose the band on a quickload.
  public bool IsActive;

  // Seconds until the next predicted ON↔OFF flip given the current
  // vessel-wide net charge rate. +∞ when no transition is reachable
  // from the current rate (e.g. fuel cell is OFF and batteries are
  // charging, so SoC won't drop to 20% from this state alone).
  public double ValidUntilSeconds = double.PositiveInfinity;

  // Live W the cell is actually delivering, post-LP-throttle. Reads
  // through the output device so telemetry never sees a stale value.
  // Distinct from `device.Activity * EcOutput`, which is the *capacity*
  // at the current input draw — the LP throttles `output.Activity`
  // below capacity when there's no EC sink.
  public double CurrentOutput => output != null ? output.Activity * EcOutput : 0;

  internal ResourceSolver.Device device;
  internal ResourceSolver.Device output;

  public override VirtualComponent Clone() {
    return new FuelCell {
      Lh2Rate = Lh2Rate,
      LoxRate = LoxRate,
      EcOutput = EcOutput,
      IsActive = IsActive,
    };
  }

  public override void OnBuildSolver(ResourceSolver solver, ResourceSolver.Node node) {
    device = node.AddDevice(ResourceSolver.Priority.Low);
    device.AddInput(Resource.LiquidHydrogen, Lh2Rate);
    device.AddInput(Resource.LiquidOxygen, LoxRate);

    output = node.AddDevice(ResourceSolver.Priority.Low);
    output.AddOutput(Resource.ElectricCharge, EcOutput);
    output.AddParent(device);
    // Demand=1 keeps sum-max trying to drive output up; the parent
    // constraint caps it at input activity, and conservation drops it
    // back down when there's no EC sink.
    output.Demand = 1.0;

    // Initial demand reflects the persisted state. VirtualVessel's
    // per-tick orchestrator will override on the next solve once it
    // sees the live SoC.
    device.Demand = IsActive ? 1.0 : 0.0;
  }

  public override void SaveStructure(PartStructure ps) {
    ps.FuelCell = new FuelCellStructure {
      Lh2Rate = Lh2Rate,
      LoxRate = LoxRate,
      EcOutput = EcOutput,
    };
  }

  public override void LoadStructure(PartStructure ps) {
    if (ps.FuelCell == null) return;
    Lh2Rate = ps.FuelCell.Lh2Rate;
    LoxRate = ps.FuelCell.LoxRate;
    EcOutput = ps.FuelCell.EcOutput;
  }

  public override void Save(PartState state) {
    state.FuelCell = new FuelCellState { IsActive = IsActive };
  }

  public override void Load(PartState state) {
    if (state.FuelCell == null) return;
    IsActive = state.FuelCell.IsActive;
  }
}
