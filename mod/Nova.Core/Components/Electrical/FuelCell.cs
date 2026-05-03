using System;
using System.Linq;
using Nova.Core.Persistence.Protos;
using Nova.Core.Resources;
using Nova.Core.Systems;

namespace Nova.Core.Components.Electrical;

// PEM-style fuel cell, buffer-pattern. The component-internal manifold
// is an `Accumulator` storing total "mix" volume (LH₂ + LOx combined,
// in litres). Per-resource production rates (Lh2Rate, LoxRate at full
// activity) imply a fixed volumetric mix ratio, so one number tracks
// both reactants in lockstep. See `docs/lp_hygiene.md` for why the
// µL/s reactant rates stay off the LP.
//
// The Accumulator owns:
//   • Refill side — Staging Consumer with two coupled inputs
//     (LH₂ + LOx) at the configured volumetric proportions. Refill
//     hysteresis (10 / 100% manifold fill) is internal.
//   • Lerp state — manifold contents over time, with refill activity
//     and production drain rate combined into a signed Rate.
//
// FuelCell owns:
//   • Production side — vessel-wide EC producer Device, gated by
//     IsActive (battery SoC hysteresis) AND manifold-non-empty.
//   • Production drain → pushed onto Manifold.TapRate post-solve so
//     the manifold lerps with the right drain.
//   • Production ValidUntil — soonest of SoC threshold flip or
//     manifold-empty; production refreshes on either event.
//
// IsActive and the manifold contents persist across save/load via
// FuelCellState. RefillActive lives on Manifold but is also persisted
// here so old-style snapshots round-trip.
public class FuelCell : VirtualComponent {
  // Production hysteresis bands (vessel-wide battery SoC).
  private const double SocOnThreshold  = 0.20;
  private const double SocOffThreshold = 0.80;

  // Config (from prefab MODULE — populated by ComponentFactory.CreateFuelCell).
  public double Lh2Rate;             // L/s of LH₂ at full production
  public double LoxRate;             // L/s of LOx at full production
  public double EcOutput;            // W (EC/s) at full production
  public double RefillRate;          // mix-L/s drawn from main tanks when refill is active

  // Persisted state.
  public bool IsActive;
  public bool RefillActive;              // mirrored to/from Manifold.RefillActive
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

  internal ProcessFlowSystem.Device production;

  public override VirtualComponent Clone() {
    return new FuelCell {
      Lh2Rate = Lh2Rate,
      LoxRate = LoxRate,
      EcOutput = EcOutput,
      RefillRate = RefillRate,
      IsActive = IsActive,
      RefillActive = RefillActive,
      Manifold = new Accumulator {
        Capacity = Manifold.Capacity,
        Contents = Manifold.Contents,
      },
    };
  }

  public override void OnBuildSystems(VesselSystems systems, StagingFlowSystem.Node node) {
    // Manifold owns the refill side: a coupled-input Staging Consumer
    // with two propellants at the configured mix proportions. The
    // staging solver's coupling pass guarantees refill stops drawing
    // either propellant if the other can't be supplied.
    // Push the persisted RefillActive into Manifold's runtime state
    // before configuring the refill device — Configure* reads
    // RefillActive to set the initial Throttle.
    Manifold.RefillActive = RefillActive;
    Manifold.ConfigureStagingRefill(systems, node,
        (Resource.LiquidHydrogen, RefillRate * Lh2Frac),
        (Resource.LiquidOxygen,   RefillRate * LoxFrac));

    // Production: vessel-wide EC producer. Activity is gated each tick
    // by OnPreSolve (IsActive + manifold-non-empty).
    production = systems.Process.AddDevice(ProcessFlowSystem.Priority.Low);
    production.AddOutput(Resource.ElectricCharge, EcOutput);
    production.Demand = (IsActive && !Manifold.IsEmpty) ? 1.0 : 0.0;
  }

  // Pre-solve: aggregate vessel-wide battery SoC and apply production
  // hysteresis (IsActive). Manifold owns its own refill hysteresis and
  // pushes Throttle automatically. The "no batteries" branch keeps the
  // cell on continuously — without a SoC signal there's no reason to
  // throttle.
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

    production.Demand = (IsActive && !Manifold.IsEmpty) ? 1.0 : 0.0;

    Manifold.OnPreSolve();
    // Hysteresis flip happened inside Manifold.OnPreSolve — sync the
    // persisted handle so external reads + Save see runtime state.
    RefillActive = Manifold.RefillActive;
  }

  // Post-solve: feed production drain into Manifold.TapRate (lerp
  // captures it forward), let the Manifold refresh its own net Rate +
  // refill ValidUntil, then forecast production's own ValidUntil.
  // cmp.ValidUntil bubbles up the soonest of (production flip,
  // manifold refill flip).
  public override void OnPostSolve() {
    if (production == null) return;

    // Drain side → TapRate. Manifold.OnPostSolve picks up its own
    // refill activity and rebaselines to "now".
    Manifold.TapRate = production.Activity * ProductionDrainRate;
    Manifold.OnPostSolve();

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

    // Manifold-empty time. Refill is much faster than production
    // reactant draw (~200×) so this only matters when the main tank
    // is empty and refill is forced to 0.
    double dtMfdEmpty = double.PositiveInfinity;
    if (production.Activity > 1e-9 && Manifold.Rate < -1e-12 && Manifold.Contents > 0)
      dtMfdEmpty = Manifold.Contents / -Manifold.Rate;

    double dtProdFlip = Math.Min(dtSocFlip, dtMfdEmpty);
    ValidUntilSeconds = dtProdFlip;
    double now = Vessel.Systems.Clock.UT;
    production.ValidUntil = double.IsPositiveInfinity(dtProdFlip)
      ? double.PositiveInfinity
      : now + dtProdFlip;

    // Bubble up to cmp.ValidUntil — soonest of production-flip and
    // manifold-refill-flip (the latter owned by the Accumulator).
    double earliest = Math.Min(production.ValidUntil, Manifold.ValidUntil);
    ValidUntil = earliest;
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
