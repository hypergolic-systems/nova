//! Mirror of `mod/Nova.Tests/Components/FuelCellTests.cs`. Skips the
//! two Save/Load tests (proto persistence is out of scope on the
//! Rust side until the persistence milestone lands).

use approx::assert_relative_eq;
use nova_sim::components::{Battery, Component, FuelCell, TankVolume};
use nova_sim::fixtures::ids;
use nova_sim::orbit::OrbitalElements;
use nova_sim::resource::Resource;
use nova_sim::world::{Vessel, VesselId};

const SMALL_LH2_RATE: f64 = 4.96e-4;
const SMALL_LOX_RATE: f64 = 2.31e-4;
const SMALL_EC_OUTPUT: f64 = 2500.0;
const SMALL_MANIFOLD_CAP: f64 = 1.466;
const SMALL_REFILL_RATE: f64 = 0.1466;

fn make_fuel_cell() -> FuelCell {
    FuelCell::new(
        SMALL_LH2_RATE,
        SMALL_LOX_RATE,
        SMALL_EC_OUTPUT,
        SMALL_REFILL_RATE,
        SMALL_MANIFOLD_CAP,
    )
}

fn make_battery(capacity: f64, contents: f64) -> Battery {
    // Big in/out limits so the small fuel cell's full output isn't
    // throttled by the buffer flow caps. Mirrors C# `MakeBattery`.
    Battery::new(capacity).with_contents(contents).with_flow_limits(1.0e6, 1.0e6)
}

fn make_tank(resource: Resource, capacity: f64) -> TankVolume {
    TankVolume::new(capacity, 10_000.0).add_tank(resource, capacity)
}

/// Build a single-part vessel with the given components, in low
/// Kerbin orbit. Run `initialize_solver(0)` so the vessel is ready
/// for `vessel.tick(&nova_sim::fixtures::kerbol_ctx(),...)`.
fn build_vessel(components: Vec<Component>) -> Vessel {
    let mut v = Vessel::new(
        VesselId(1),
        "test",
        ids::KERBIN,
        OrbitalElements::circular(700_000.0 + 600_000.0),
    );
    v.add_part(1, "core", 1.0, components);
    v.initialize_solver(&nova_sim::fixtures::kerbol_ctx(), 0.0);
    v
}

/// Find the FuelCell on the vessel's first part and return a clone of
/// its current state. Used to read post-tick state cleanly.
fn fuel_cell_of(vessel: &Vessel) -> &FuelCell {
    for c in &vessel.part(1).components {
        if let Component::FuelCell(fc) = c {
            return fc;
        }
    }
    panic!("no FuelCell on part 1");
}

// ── Construction + factory ────────────────────────────────────────

#[test]
fn proportions_derived_from_rates() {
    let fc = make_fuel_cell();
    let sum = SMALL_LH2_RATE + SMALL_LOX_RATE;
    assert_relative_eq!(fc.lh2_frac(), SMALL_LH2_RATE / sum, epsilon = 1e-9);
    assert_relative_eq!(fc.lox_frac(), SMALL_LOX_RATE / sum, epsilon = 1e-9);
    assert_relative_eq!(fc.lh2_frac() + fc.lox_frac(), 1.0, epsilon = 1e-9);
    assert_relative_eq!(fc.production_drain_rate(), sum, epsilon = 1e-9);
}

// ── Production hysteresis ────────────────────────────────────────

#[test]
fn hysteresis_turns_on_below_twenty_percent() {
    let fc = make_fuel_cell();
    let battery = make_battery(100_000.0, 15_000.0); // SoC = 0.15
    let mut vessel = build_vessel(vec![
        Component::FuelCell(fc),
        Component::Battery(battery),
        Component::TankVolume(make_tank(Resource::LiquidHydrogen, 10.0)),
        Component::TankVolume(make_tank(Resource::LiquidOxygen, 10.0)),
    ]);

    vessel.tick(&nova_sim::fixtures::kerbol_ctx(),1.0);

    assert!(fuel_cell_of(&vessel).is_active, "SoC=0.15 should flip the cell ON");
}

#[test]
fn hysteresis_turns_off_above_eighty_percent() {
    let fc = make_fuel_cell().with_active(true);
    let battery = make_battery(100_000.0, 85_000.0); // SoC = 0.85
    let mut vessel = build_vessel(vec![
        Component::FuelCell(fc),
        Component::Battery(battery),
        Component::TankVolume(make_tank(Resource::LiquidHydrogen, 10.0)),
        Component::TankVolume(make_tank(Resource::LiquidOxygen, 10.0)),
    ]);

    vessel.tick(&nova_sim::fixtures::kerbol_ctx(),1.0);

    assert!(!fuel_cell_of(&vessel).is_active, "SoC=0.85 should flip the cell OFF");
}

#[test]
fn hysteresis_holds_in_band_above_on_threshold() {
    // OFF + SoC=0.5 should stay OFF.
    let fc = make_fuel_cell();
    let battery = make_battery(100_000.0, 50_000.0);
    let mut vessel = build_vessel(vec![
        Component::FuelCell(fc),
        Component::Battery(battery),
        Component::TankVolume(make_tank(Resource::LiquidHydrogen, 10.0)),
        Component::TankVolume(make_tank(Resource::LiquidOxygen, 10.0)),
    ]);

    vessel.tick(&nova_sim::fixtures::kerbol_ctx(),1.0);

    assert!(!fuel_cell_of(&vessel).is_active);
}

#[test]
fn hysteresis_holds_in_band_below_off_threshold() {
    // ON + SoC=0.5 should stay ON.
    let fc = make_fuel_cell().with_active(true);
    let battery = make_battery(100_000.0, 50_000.0);
    let mut vessel = build_vessel(vec![
        Component::FuelCell(fc),
        Component::Battery(battery),
        Component::TankVolume(make_tank(Resource::LiquidHydrogen, 10.0)),
        Component::TankVolume(make_tank(Resource::LiquidOxygen, 10.0)),
    ]);

    vessel.tick(&nova_sim::fixtures::kerbol_ctx(),1.0);

    assert!(fuel_cell_of(&vessel).is_active);
}

#[test]
fn hysteresis_no_batteries_forces_active() {
    let fc = make_fuel_cell();
    let mut vessel = build_vessel(vec![
        Component::FuelCell(fc),
        Component::TankVolume(make_tank(Resource::LiquidHydrogen, 10.0)),
        Component::TankVolume(make_tank(Resource::LiquidOxygen, 10.0)),
    ]);

    vessel.tick(&nova_sim::fixtures::kerbol_ctx(),1.0);

    assert!(
        fuel_cell_of(&vessel).is_active,
        "with no batteries to gate against, the cell runs continuously"
    );
}

// ── Refill hysteresis ────────────────────────────────────────────

#[test]
fn refill_turns_on_when_manifold_low() {
    let fc = make_fuel_cell()
        .with_manifold_contents(0.05 * SMALL_MANIFOLD_CAP); // 5%
    let battery = make_battery(1000.0, 500.0);
    let mut vessel = build_vessel(vec![
        Component::FuelCell(fc),
        Component::Battery(battery),
        Component::TankVolume(make_tank(Resource::LiquidHydrogen, 10.0)),
        Component::TankVolume(make_tank(Resource::LiquidOxygen, 10.0)),
    ]);

    vessel.tick(&nova_sim::fixtures::kerbol_ctx(),1.0);

    assert!(
        fuel_cell_of(&vessel).refill_active,
        "manifold below 10% should flip refill ON"
    );
}

#[test]
fn refill_turns_off_when_manifold_full() {
    let fc = make_fuel_cell()
        .with_refill_active(true)
        .with_manifold_contents(SMALL_MANIFOLD_CAP);
    let battery = make_battery(1000.0, 500.0);
    let mut vessel = build_vessel(vec![
        Component::FuelCell(fc),
        Component::Battery(battery),
        Component::TankVolume(make_tank(Resource::LiquidHydrogen, 10.0)),
        Component::TankVolume(make_tank(Resource::LiquidOxygen, 10.0)),
    ]);

    vessel.tick(&nova_sim::fixtures::kerbol_ctx(),1.0);

    assert!(
        !fuel_cell_of(&vessel).refill_active,
        "manifold at capacity should flip refill OFF"
    );
}

#[test]
fn refill_holds_in_band() {
    // 50% manifold, refill OFF — neither threshold crossed, stays OFF.
    let fc = make_fuel_cell()
        .with_manifold_contents(0.5 * SMALL_MANIFOLD_CAP);
    let battery = make_battery(1000.0, 500.0);
    let mut vessel = build_vessel(vec![
        Component::FuelCell(fc),
        Component::Battery(battery),
        Component::TankVolume(make_tank(Resource::LiquidHydrogen, 10.0)),
        Component::TankVolume(make_tank(Resource::LiquidOxygen, 10.0)),
    ]);

    vessel.tick(&nova_sim::fixtures::kerbol_ctx(),1.0);

    assert!(!fuel_cell_of(&vessel).refill_active);
}

// ── Manifold integration ─────────────────────────────────────────

#[test]
fn manifold_drains_while_producing() {
    // ON cell + thirsty battery + plenty of EC sink. No main tanks →
    // refill can't run → manifold drains in isolation.
    let fc = make_fuel_cell().with_active(true);
    let battery = make_battery(100_000.0, 10_000.0);
    let mut vessel = build_vessel(vec![
        Component::FuelCell(fc),
        Component::Battery(battery),
    ]);

    // First tick establishes activities; integration before that runs
    // with default zero rates and would no-op the manifold.
    vessel.tick(&nova_sim::fixtures::kerbol_ctx(),0.001);
    let initial = fuel_cell_of(&vessel).manifold.contents();
    vessel.tick(&nova_sim::fixtures::kerbol_ctx(),60.001);
    let later = fuel_cell_of(&vessel).manifold.contents();

    assert!(
        later < initial,
        "expected manifold drain, got {} → {}",
        initial,
        later
    );
}

#[test]
fn manifold_refills_when_low() {
    // 5% manifold, cell OFF (no production), main tanks full.
    // Refill should trip on, fill manifold, trip off near capacity.
    let fc = make_fuel_cell().with_manifold_contents(0.05 * SMALL_MANIFOLD_CAP);
    // Battery above 80% so production stays OFF.
    let battery = make_battery(1000.0, 900.0);
    let mut vessel = build_vessel(vec![
        Component::FuelCell(fc),
        Component::Battery(battery),
        Component::TankVolume(make_tank(Resource::LiquidHydrogen, 10.0)),
        Component::TankVolume(make_tank(Resource::LiquidOxygen, 10.0)),
    ]);

    // Warmup so the first integration uses the post-solve refill rate.
    vessel.tick(&nova_sim::fixtures::kerbol_ctx(),0.001);
    vessel.tick(&nova_sim::fixtures::kerbol_ctx(),20.001); // 20 s — well past the ~9.5 s refill window

    let contents = fuel_cell_of(&vessel).manifold.contents();
    assert!(
        contents > 0.9 * SMALL_MANIFOLD_CAP,
        "expected near-full manifold, got {}",
        contents
    );
}

#[test]
fn manifold_starvation_stops_production() {
    // Empty manifold, no main tanks → refill can't fill, production
    // can't continue. CurrentOutput should be zero.
    let fc = make_fuel_cell().with_active(true).with_manifold_contents(0.0);
    let battery = make_battery(1000.0, 100.0); // SoC=0.1, wants on
    let mut vessel = build_vessel(vec![
        Component::FuelCell(fc),
        Component::Battery(battery),
    ]);

    vessel.tick(&nova_sim::fixtures::kerbol_ctx(),1.0);

    let fc_after = fuel_cell_of(&vessel);
    assert!(fc_after.is_active, "cell wants to be ON (SoC=0.1)");
    assert_relative_eq!(
        fc_after.current_output(),
        0.0,
        epsilon = 1e-6
    );
}

// ── Forecast (post-solve, on converged rates) ────────────────────

#[test]
fn valid_until_charging_projects_to_off_threshold() {
    // Cell is ON, battery below 80%. Pure cell + battery vessel — the
    // LP converges with battery rate ≈ EcOutput, and
    // valid_until_seconds = (0.8·capacity − contents) / rate.
    let ec = 1000.0;
    let mut fc = make_fuel_cell().with_active(true);
    fc.ec_output = ec;
    let battery = make_battery(10_000.0, 5_000.0); // SoC=0.5
    let mut vessel = build_vessel(vec![
        Component::FuelCell(fc),
        Component::Battery(battery),
        Component::TankVolume(make_tank(Resource::LiquidHydrogen, 10.0)),
        Component::TankVolume(make_tank(Resource::LiquidOxygen, 10.0)),
    ]);

    vessel.tick(&nova_sim::fixtures::kerbol_ctx(),1.0);

    let fc_after = fuel_cell_of(&vessel);
    assert!(fc_after.is_active);
    // Expect: rate ≈ +1000 W charging, remaining = 0.8·10000 − 5000 = 3000.
    // dt ≈ 3000 / 1000 = 3.0 s. Allow a small fudge for LP conservatism.
    assert!(
        fc_after.valid_until_seconds > 2.5 && fc_after.valid_until_seconds < 3.5,
        "expected ~3.0 s, got {}",
        fc_after.valid_until_seconds
    );
}

#[test]
fn valid_until_not_charging_returns_infinity() {
    // Cell OFF, no producers — battery rate is 0; no flip reachable.
    let fc = make_fuel_cell();
    let battery = make_battery(1000.0, 500.0);
    let mut vessel = build_vessel(vec![
        Component::FuelCell(fc),
        Component::Battery(battery),
        Component::TankVolume(make_tank(Resource::LiquidHydrogen, 10.0)),
        Component::TankVolume(make_tank(Resource::LiquidOxygen, 10.0)),
    ]);

    vessel.tick(&nova_sim::fixtures::kerbol_ctx(),1.0);

    let fc_after = fuel_cell_of(&vessel);
    assert!(!fc_after.is_active);
    assert!(fc_after.valid_until_seconds.is_infinite());
}

#[test]
fn valid_until_no_batteries_returns_infinity() {
    let fc = make_fuel_cell();
    let mut vessel = build_vessel(vec![
        Component::FuelCell(fc),
        Component::TankVolume(make_tank(Resource::LiquidHydrogen, 10.0)),
        Component::TankVolume(make_tank(Resource::LiquidOxygen, 10.0)),
    ]);

    vessel.tick(&nova_sim::fixtures::kerbol_ctx(),1.0);

    assert!(fuel_cell_of(&vessel).valid_until_seconds.is_infinite());
}

#[test]
fn valid_until_production_projects_to_manifold_empty() {
    // ON cell, full manifold, no main tanks → refill blocked, so
    // production drains the manifold in isolation. Use an oversized
    // battery so SoC-flip ≫ manifold-empty and the manifold-empty
    // term is the binding one.
    let fc = make_fuel_cell().with_active(true);
    let battery = make_battery(1.0e7, 1.0e6); // SoC=0.1, dt_soc_flip ≈ 2800 s
    let mut vessel = build_vessel(vec![
        Component::FuelCell(fc),
        Component::Battery(battery),
    ]);

    vessel.tick(&nova_sim::fixtures::kerbol_ctx(),1.0);

    let fc_after = fuel_cell_of(&vessel);
    // manifold ≈ 1.466 mix-L, drain ≈ 7.27e-4 mix-L/s, dt ≈ 2017 s.
    assert!(
        fc_after.valid_until_seconds > 1500.0
            && fc_after.valid_until_seconds < 2500.0,
        "expected ~2017 s manifold-empty horizon, got {}",
        fc_after.valid_until_seconds
    );
}

// ── Integration ──────────────────────────────────────────────────

#[test]
fn integration_battery_stays_above_on_threshold_while_cell_running() {
    // ON cell + hungry battery + ample LH2/LOx, no consumers.
    let fc = make_fuel_cell().with_active(true);
    let battery = make_battery(100_000.0, 10_000.0); // SoC = 0.10
    let mut vessel = build_vessel(vec![
        Component::FuelCell(fc),
        Component::Battery(battery),
        Component::TankVolume(make_tank(Resource::LiquidHydrogen, 10.0)),
        Component::TankVolume(make_tank(Resource::LiquidOxygen, 10.0)),
    ]);

    vessel.tick(&nova_sim::fixtures::kerbol_ctx(),1.0);

    let fc_after = fuel_cell_of(&vessel);
    assert!(fc_after.is_active, "SoC=0.10 starts ON");
    assert!(fc_after.current_output() > 0.0, "cell should be producing EC");
    // Within 10% of rated output when battery is hungry.
    assert!(
        fc_after.current_output() > 0.9 * SMALL_EC_OUTPUT,
        "expected ≈{}, got {}",
        SMALL_EC_OUTPUT,
        fc_after.current_output()
    );
}
