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
//                  by IsActive (SoC hysteresis) AND hasFuel (manifolds non-zero).
//
// The manifold is component-internal state — plain doubles, NOT an LP
// `Buffer`. Keeping it off-LP entirely is the whole point: production
// drains the manifold at µL/s outside the LP, conservation never sees
// those rates. See `docs/lp_hygiene.md` for the design rationale.
//
// Auto-controlled by VirtualVessel.UpdateFuelCellDevices (production
// hysteresis) and the same orchestrator's refill hysteresis:
//   - Production ON  when vessel-wide battery SoC <  20% (or no batteries)
//                OFF when vessel-wide battery SoC >  80%
//   - Refill     ON  when min manifold fill < 10%
//                OFF when both manifolds at 100%
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
  public double Lh2ManifoldCapacity; // L
  public double LoxManifoldCapacity; // L
  public double RefillRateLh2;       // L/s; LOx side scales by LoxRate/Lh2Rate

  // Hysteresis + manifold state. Persisted in FuelCellState.
  public bool IsActive;
  public bool RefillActive;
  public double Lh2ManifoldContents;
  public double LoxManifoldContents;

  // Seconds until the next predicted production state flip given the
  // current rates. +∞ when no transition is reachable.
  public double ValidUntilSeconds = double.PositiveInfinity;

  // Live W actually delivered, post-LP-throttle. Reads through the
  // production device so telemetry never sees a stale value.
  public double CurrentOutput => production != null ? production.Activity * EcOutput : 0;

  internal ResourceSolver.Device refill;
  internal ResourceSolver.Device production;

  public override VirtualComponent Clone() {
    return new FuelCell {
      Lh2Rate = Lh2Rate,
      LoxRate = LoxRate,
      EcOutput = EcOutput,
      Lh2ManifoldCapacity = Lh2ManifoldCapacity,
      LoxManifoldCapacity = LoxManifoldCapacity,
      RefillRateLh2 = RefillRateLh2,
      IsActive = IsActive,
      RefillActive = RefillActive,
      Lh2ManifoldContents = Lh2ManifoldContents,
      LoxManifoldContents = LoxManifoldContents,
    };
  }

  public override void OnBuildSolver(ResourceSolver solver, ResourceSolver.Node node) {
    refill = node.AddDevice(ResourceSolver.Priority.Low);
    refill.AddInput(Resource.LiquidHydrogen, RefillRateLh2);
    // Lock the LOx refill rate to the production stoichiometric ratio
    // so a single Activity scales both inputs together — automatic
    // "fill ingredients in lockstep" without an explicit constraint.
    refill.AddInput(Resource.LiquidOxygen, RefillRateLh2 * LoxRate / Lh2Rate);
    refill.Demand = RefillActive ? 1.0 : 0.0;

    production = node.AddDevice(ResourceSolver.Priority.Low);
    production.AddOutput(Resource.ElectricCharge, EcOutput);
    // Initial demand reflects persisted state. UpdateFuelCellDevices
    // overrides each tick once it sees live SoC + manifold state.
    bool hasFuel = Lh2ManifoldContents > 0 && LoxManifoldContents > 0;
    production.Demand = (IsActive && hasFuel) ? 1.0 : 0.0;
  }

  public override void Save(PartState state) {
    state.FuelCell = new FuelCellState {
      IsActive = IsActive,
      RefillActive = RefillActive,
      Lh2ManifoldContents = Lh2ManifoldContents,
      LoxManifoldContents = LoxManifoldContents,
    };
  }

  public override void Load(PartState state) {
    if (state.FuelCell == null) return;
    IsActive = state.FuelCell.IsActive;
    RefillActive = state.FuelCell.RefillActive;
    Lh2ManifoldContents = state.FuelCell.Lh2ManifoldContents;
    LoxManifoldContents = state.FuelCell.LoxManifoldContents;
  }
}
