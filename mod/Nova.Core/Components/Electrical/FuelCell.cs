using System;
using System.Linq;
using Nova.Core.Persistence.Protos;
using Nova.Core.Resources;
using Nova.Core.Systems;

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
// Self-orchestrated via the OnPreSolve / OnPostSolve / OnAdvance hooks:
//   - Production ON  when vessel-wide battery SoC <  20% (or no batteries)
//                OFF when vessel-wide battery SoC >  80%
//   - Refill     ON  when manifold fill < 10%
//                OFF when manifold reaches 100%
//
// `IsActive`, `RefillActive`, and the manifold contents persist across
// save/load via FuelCellState. `ValidUntilSeconds` is OnPostSolve's
// forecast for the production device's next state flip — the soonest of
// SoC threshold crossing or manifold-empty event.
public class FuelCell : VirtualComponent {
  // Hysteresis bands. Production tracks vessel-wide battery SoC;
  // refill tracks the component-internal manifold fraction.
  private const double SocOnThreshold     = 0.20;
  private const double SocOffThreshold    = 0.80;
  private const double RefillOnThreshold  = 0.10;
  private const double RefillOffThreshold = 1.00;

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

  // Refill is a coupled-input consumer (LH₂ + LOx in fixed ratio) on
  // the staging system. The solver's coupling pass guarantees the
  // refill stops drawing either propellant if the other can't be
  // supplied.
  internal StagingFlowSystem.Consumer refill;
  internal ProcessFlowSystem.Device production;

  // Achieved fraction of the requested mix refill rate (0..1).
  public double RefillActivity => refill?.Activity ?? 0;

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

  public override void OnBuildSystems(VesselSystems systems, StagingFlowSystem.Node node) {
    // Refill: one coupled-input consumer with two propellants at fixed
    // mix-proportions. Throttle goes 0 ↔ 1 with the hysteresis flag;
    // the staging solver's coupling pass handles bottleneck scaling
    // automatically.
    refill = systems.Staging.RegisterConsumer(node);
    refill.AddInput(Resource.LiquidHydrogen, RefillRate * Lh2Frac);
    refill.AddInput(Resource.LiquidOxygen,   RefillRate * LoxFrac);

    // Production: vessel-wide EC producer. Activity is gated each tick
    // by OnPreSolve (IsActive + manifold-non-empty).
    production = systems.Process.AddDevice(ProcessFlowSystem.Priority.Low);
    production.AddOutput(Resource.ElectricCharge, EcOutput);
    production.Demand = (IsActive && !Manifold.IsEmpty) ? 1.0 : 0.0;

    // Wire the manifold's clock + rebaseline to "now". Whatever the
    // accumulator's prior BaselineUT was (default 0 from fresh
    // construction, or save-load left-over), reset it so the lerp
    // evaluates against the live clock from here on. Rate stays at 0
    // until the first OnPostSolve.
    Manifold.Clock = systems.Clock;
    Manifold.BaselineUT = systems.Clock.UT;
  }

  // Pre-solve: aggregate vessel-wide battery SoC, apply production
  // hysteresis (IsActive) and refill hysteresis (RefillActive), then push
  // the resulting Demand/Throttle into the LP for the upcoming solve.
  // The "no batteries" branch keeps the cell on continuously — without
  // a SoC signal there's no reason to throttle.
  public override void OnPreSolve() {
    if (production == null) return;

    double contents = 0, capacity = 0;
    foreach (var b in Vessel.AllComponents().OfType<Battery>()) {
      contents += b.Buffer.Contents;
      capacity += b.Buffer.Capacity;
    }
    bool noBatteries = capacity < 1e-9;
    double soc = noBatteries ? 0 : contents / capacity;

    if (noBatteries) {
      IsActive = true;
    } else if (IsActive && soc > SocOffThreshold) {
      IsActive = false;
    } else if (!IsActive && soc < SocOnThreshold) {
      IsActive = true;
    }

    double frac = Manifold.FillFraction;
    if (RefillActive && frac >= RefillOffThreshold) {
      RefillActive = false;
    } else if (!RefillActive && frac < RefillOnThreshold) {
      RefillActive = true;
    }

    production.Demand = (IsActive && !Manifold.IsEmpty) ? 1.0 : 0.0;
    if (refill != null) refill.Throttle = RefillActive ? 1.0 : 0.0;
  }

  // Post-solve: push the just-solved net flow into Manifold.Rate (lerp
  // takes it forward from here), then forecast valid-until times for
  // the production device and the component (refill flip). Production
  // flips on either SoC threshold crossing OR manifold drying out;
  // refill flips on manifold hitting the opposite threshold.
  public override void OnPostSolve() {
    if (production == null) return;

    // Net manifold flow: refill fills, production drains. Setter
    // rebaselines the lerp at the current clock UT.
    Manifold.Rate = RefillActivity      * RefillRate
                  - production.Activity * ProductionDrainRate;

    double contents = 0, capacity = 0, batteryRate = 0;
    foreach (var b in Vessel.AllComponents().OfType<Battery>()) {
      contents += b.Buffer.Contents;
      capacity += b.Buffer.Capacity;
      batteryRate += b.Buffer.Rate;  // signed: + = charging, − = draining
    }
    bool noBatteries = capacity < 1e-9;

    // SoC threshold flip.
    double dtSocFlip = double.PositiveInfinity;
    if (!noBatteries && Math.Abs(batteryRate) > 1e-9) {
      if (IsActive && batteryRate > 0) {
        double remaining = SocOffThreshold * capacity - contents;
        if (remaining > 0) dtSocFlip = remaining / batteryRate;
      } else if (!IsActive && batteryRate < 0) {
        double remaining = contents - SocOnThreshold * capacity;
        if (remaining > 0) dtSocFlip = remaining / (-batteryRate);
      }
    }

    // Manifold-empty time. Refill is so much faster than production
    // reactant draw (~200×) that this only matters when the main tank is
    // empty and refill is forced to 0.
    double dtMfdEmpty = double.PositiveInfinity;
    if (production.Activity > 1e-9) {
      double netDrain = production.Activity * ProductionDrainRate
                      - RefillActivity      * RefillRate;
      if (netDrain > 1e-12 && Manifold.Contents > 0)
        dtMfdEmpty = Manifold.Contents / netDrain;
    }

    double dtProdFlip = Math.Min(dtSocFlip, dtMfdEmpty);
    ValidUntilSeconds = dtProdFlip;
    double now = Vessel.Systems.Clock.UT;
    production.ValidUntil = double.IsPositiveInfinity(dtProdFlip)
      ? double.PositiveInfinity
      : now + dtProdFlip;

    // Refill flip → component-level ValidUntil (no per-demand ValidUntil
    // on the coupled refill consumer; bubbles through as cmp.ValidUntil).
    double netFill = RefillActivity      * RefillRate
                   - production.Activity * ProductionDrainRate;
    double dtRefillFlip = RefillActive
      ? Manifold.TimeToFraction(RefillOffThreshold, netFill)
      : Manifold.TimeToFraction(RefillOnThreshold,  netFill);

    double dtCmp = Math.Min(dtProdFlip, dtRefillFlip);
    ValidUntil = double.IsPositiveInfinity(dtCmp)
      ? double.PositiveInfinity
      : now + dtCmp;
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
