using Nova.Core.Persistence.Protos;
using Nova.Core.Resources;

namespace Nova.Core.Components.Electrical;

// PEM-style fuel cell, buffer-pattern. Two LP devices, none of them
// carrying the µL/s reactant rates that would foul GLOP's tolerance:
//
//   • `refill`     consumes LH₂ + LOx from main tanks at envelope-friendly
//                  ~0.1 L/s, gated by hysteresis on internal manifold fill.
//   • `production` produces EC at the rated wattage with no LP-visible
//                  inputs (Solar-style "free" producer). Activity gated
//                  by IsActive (SoC hysteresis) AND hasFuel (manifold non-empty).
//
// The manifold is a component-internal `Accumulator` storing total
// "mix" volume — LH₂ + LOx combined — in litres. The cell's per-resource
// consumption (Lh2Rate, LoxRate at full activity) implies a fixed
// volumetric mix ratio, so one number tracks both reactants in lockstep.
// Production drains the manifold at µL/s outside the LP, so conservation
// never sees those rates. See `docs/lp_hygiene.md` for the design rationale.
//
// Auto-controlled by VirtualVessel.UpdateFuelCellDevices (production
// hysteresis) and the same orchestrator's refill hysteresis:
//   - Production ON  when vessel-wide battery SoC <  20% (or no batteries)
//                OFF when vessel-wide battery SoC >  80%
//   - Refill     ON  when manifold fill < 10%
//                OFF when manifold reaches 100%
//
// `IsActive`, `RefillActive`, and the manifold contents persist across
// save/load via FuelCellState. `ValidUntilSeconds` is the orchestrator's
// forecast for the production device's next state flip — the soonest of
// SoC threshold crossing or manifold-empty event.
public class FuelCell : VirtualComponent {
  // Config (from prefab MODULE — populated by ComponentFactory.CreateFuelCell).
  public double Lh2Rate;             // L/s of LH₂ at full production
  public double LoxRate;             // L/s of LOx at full production
  public double EcOutput;            // W (EC/s) at full production
  public double RefillRate;          // mix-L/s drawn from main tanks when refill is active

  // Persisted state.
  public bool IsActive;
  public bool RefillActive;
  public Accumulator Manifold = new();   // mix-L (LH₂ + LOx combined)

  // Seconds until the next predicted production state flip given the
  // current rates. +∞ when no transition is reachable.
  public double ValidUntilSeconds = double.PositiveInfinity;

  // Live W actually delivered, post-LP-throttle.
  public double CurrentOutput => production != null ? production.Activity * EcOutput : 0;

  // Volumetric proportions of LH₂ vs LOx in the mix. Derived from per-
  // resource production rates — the manifold drains in lockstep with
  // production (since production is the only consumer), so its mix
  // composition matches the production stoichiometry.
  public double Lh2Frac => Lh2Rate / (Lh2Rate + LoxRate);
  public double LoxFrac => LoxRate / (Lh2Rate + LoxRate);

  // Combined volumetric production drain at full activity (mix-L/s).
  public double ProductionDrainRate => Lh2Rate + LoxRate;

  internal ResourceSolver.Device refill;
  internal ResourceSolver.Device production;

  public override VirtualComponent Clone() {
    return new FuelCell {
      Lh2Rate = Lh2Rate,
      LoxRate = LoxRate,
      EcOutput = EcOutput,
      RefillRate = RefillRate,
      IsActive = IsActive,
      RefillActive = RefillActive,
      Manifold = new Accumulator {
        Capacity = Manifold.Capacity, Contents = Manifold.Contents,
      },
    };
  }

  public override void OnBuildSolver(ResourceSolver solver, ResourceSolver.Node node) {
    refill = node.AddDevice(ResourceSolver.Priority.Low);
    // The two LP inputs scale in lockstep via the device's single Activity.
    // Together they consume RefillRate mix-L/s split by Lh2Frac / LoxFrac.
    refill.AddInput(Resource.LiquidHydrogen, RefillRate * Lh2Frac);
    refill.AddInput(Resource.LiquidOxygen,   RefillRate * LoxFrac);
    refill.Demand = RefillActive ? 1.0 : 0.0;

    production = node.AddDevice(ResourceSolver.Priority.Low);
    production.AddOutput(Resource.ElectricCharge, EcOutput);
    production.Demand = (IsActive && !Manifold.IsEmpty) ? 1.0 : 0.0;
  }

  public override void Save(PartState state) {
    state.FuelCell = new FuelCellState {
      IsActive = IsActive,
      RefillActive = RefillActive,
      Manifold = new AccumulatorState { Contents = Manifold.Contents },
    };
  }

  public override void Load(PartState state) {
    if (state.FuelCell == null) return;
    IsActive = state.FuelCell.IsActive;
    RefillActive = state.FuelCell.RefillActive;
    if (state.FuelCell.Manifold != null)
      Manifold.Contents = state.FuelCell.Manifold.Contents;
  }
}
