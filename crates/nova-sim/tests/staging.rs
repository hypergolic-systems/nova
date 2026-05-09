//! End-to-end engine + tank scenarios mirroring
//! `mod/Nova.Tests/Components/RcsTests.cs`. Builds vessels in pure
//! Rust, no KSP / FFI / proto.

use approx::assert_relative_eq;
use nova_sim::components::{Component, Engine, TankVolume};
use nova_sim::fixtures::ids;
use nova_sim::orbit::OrbitalElements;
use nova_sim::resource::Resource;
use nova_sim::{Vessel, VesselId};

/// Standard test vessel: a pod (root) at part id 1 with the given
/// extra parts dangling off it. Mirrors the C# RcsTests pattern of
/// building a pod + tank + engine triple.
fn pod_with_parts(extras: Vec<(u32, &str, f64, Vec<Component>)>) -> Vessel {
    let mut vessel = Vessel::new(
        VesselId(1),
        "TestVessel",
        ids::KERBIN,
        OrbitalElements::circular(700_000.0 + 600_000.0),
    );
    vessel.add_part(1, "pod", 100.0, vec![]);
    for (id, name, dry, components) in extras {
        vessel.add_part(id, name, dry, components);
        vessel.set_parent(id, 1);
    }
    vessel
}

const G0: f64 = 9.806_65;

// ── Mirror RcsTests.cs scenarios ──────────────────────────────────────

#[test]
fn engine_consumes_hydrazine_at_full_throttle() {
    let tank = TankVolume::new(100.0, 10_000.0).add_tank(Resource::Hydrazine, 100.0);
    let mut engine = Engine::new(1.0, 220.0, vec![(Resource::Hydrazine, 1.0)]);
    engine.throttle = 1.0;

    let mut vessel = pod_with_parts(vec![
        (2, "tank", 50.0, vec![Component::TankVolume(tank)]),
        (3, "rcs", 20.0, vec![Component::Engine(engine)]),
    ]);
    let ephem = nova_sim::fixtures::kerbol_ephemeris();
    let ctx = nova_sim::WorldContext::new(&ephem);
    vessel.initialize_solver(&ctx, 0.0);
    vessel.solve(&ctx);

    let engine_ref = match &vessel.part(3).components[0] {
        Component::Engine(e) => e,
        _ => unreachable!(),
    };
    assert!(engine_ref.satisfaction(vessel.systems()) > 0.99,
            "expected full satisfaction with tank available");
}

#[test]
fn engine_starves_without_fuel() {
    let mut engine = Engine::new(1.0, 220.0, vec![(Resource::Hydrazine, 1.0)]);
    engine.throttle = 1.0;

    // No tank — just pod and engine.
    let mut vessel = pod_with_parts(vec![
        (2, "rcs", 20.0, vec![Component::Engine(engine)]),
    ]);
    let ephem = nova_sim::fixtures::kerbol_ephemeris();
    let ctx = nova_sim::WorldContext::new(&ephem);
    vessel.initialize_solver(&ctx, 0.0);
    vessel.solve(&ctx);

    let engine_ref = match &vessel.part(2).components[0] {
        Component::Engine(e) => e,
        _ => unreachable!(),
    };
    assert_relative_eq!(engine_ref.satisfaction(vessel.systems()), 0.0);
    assert_relative_eq!(engine_ref.normalized_output(vessel.systems()), 0.0);
}

#[test]
fn engine_at_throttle_zero_makes_no_demand() {
    let tank = TankVolume::new(100.0, 10_000.0).add_tank(Resource::Hydrazine, 100.0);
    let mut engine = Engine::new(1.0, 220.0, vec![(Resource::Hydrazine, 1.0)]);
    engine.throttle = 0.0;

    let mut vessel = pod_with_parts(vec![
        (2, "tank", 50.0, vec![Component::TankVolume(tank)]),
        (3, "rcs", 20.0, vec![Component::Engine(engine)]),
    ]);
    let ephem = nova_sim::fixtures::kerbol_ephemeris();
    let ctx = nova_sim::WorldContext::new(&ephem);
    vessel.initialize_solver(&ctx, 0.0);
    vessel.solve(&ctx);

    let engine_ref = match &vessel.part(3).components[0] {
        Component::Engine(e) => e,
        _ => unreachable!(),
    };
    assert_relative_eq!(engine_ref.normalized_output(vessel.systems()), 0.0);

    // Tank rate should be zero — no demand was placed on it.
    let tank_ref = match &vessel.part(2).components[0] {
        Component::TankVolume(t) => t,
        _ => unreachable!(),
    };
    let bid = tank_ref.buffer_ids()[0];
    assert_relative_eq!(vessel.systems().staging.buffer(bid).rate(), 0.0);
}

// ── Numeric drain-rate parity ─────────────────────────────────────────

#[test]
fn engine_drain_rate_matches_mdot_over_density_for_single_propellant() {
    // 1 kN @ 220 s, hydrazine ρ = 1.0 kg/L
    //   mdot = 1000 / (220 × 9.80665) ≈ 0.46340 kg/s
    //   volumetric flow = mdot / 1.0 = 0.46340 L/s
    let tank = TankVolume::new(100.0, 10_000.0).add_tank(Resource::Hydrazine, 100.0);
    let mut engine = Engine::new(1.0, 220.0, vec![(Resource::Hydrazine, 1.0)]);
    engine.throttle = 1.0;

    let mut vessel = pod_with_parts(vec![
        (2, "tank", 50.0, vec![Component::TankVolume(tank)]),
        (3, "rcs", 20.0, vec![Component::Engine(engine)]),
    ]);
    let ephem = nova_sim::fixtures::kerbol_ephemeris();
    let ctx = nova_sim::WorldContext::new(&ephem);
    vessel.initialize_solver(&ctx, 0.0);
    vessel.solve(&ctx);

    let tank_ref = match &vessel.part(2).components[0] {
        Component::TankVolume(t) => t,
        _ => unreachable!(),
    };
    let bid = tank_ref.buffer_ids()[0];

    let expected_drain = 1000.0 / (220.0 * G0) / Resource::Hydrazine.density();
    assert_relative_eq!(
        -vessel.systems().staging.buffer(bid).rate(),
        expected_drain,
        max_relative = 1e-12,
    );
}

#[test]
fn kerolox_engine_drains_propellants_in_2_to_3_volumetric_ratio() {
    let tank = TankVolume::new(500.0, 10_000.0)
        .add_tank(Resource::Rp1, 200.0)
        .add_tank(Resource::LiquidOxygen, 300.0);
    let mut engine = Engine::new(
        240.0,
        310.0,
        vec![(Resource::Rp1, 2.0), (Resource::LiquidOxygen, 3.0)],
    );
    engine.throttle = 1.0;

    let mut vessel = pod_with_parts(vec![
        (2, "tank", 500.0, vec![Component::TankVolume(tank)]),
        (3, "engine", 1500.0, vec![Component::Engine(engine)]),
    ]);
    let ephem = nova_sim::fixtures::kerbol_ephemeris();
    let ctx = nova_sim::WorldContext::new(&ephem);
    vessel.initialize_solver(&ctx, 0.0);
    vessel.solve(&ctx);

    let tank_ref = match &vessel.part(2).components[0] {
        Component::TankVolume(t) => t,
        _ => unreachable!(),
    };
    let b_rp1 = tank_ref.buffer_ids()[0];
    let b_lox = tank_ref.buffer_ids()[1];

    let r_rp1 = -vessel.systems().staging.buffer(b_rp1).rate();
    let r_lox = -vessel.systems().staging.buffer(b_lox).rate();
    assert!(r_rp1 > 0.0 && r_lox > 0.0);
    assert_relative_eq!(r_rp1 / r_lox, 2.0 / 3.0, max_relative = 1e-12);
}

#[test]
fn kerolox_engine_starves_when_lox_tank_is_empty() {
    let tank = TankVolume::new(500.0, 10_000.0)
        .add_tank(Resource::Rp1, 200.0)
        .add_tank_with_contents(Resource::LiquidOxygen, 300.0, 0.0);
    let mut engine = Engine::new(
        240.0,
        310.0,
        vec![(Resource::Rp1, 2.0), (Resource::LiquidOxygen, 3.0)],
    );
    engine.throttle = 1.0;

    let mut vessel = pod_with_parts(vec![
        (2, "tank", 500.0, vec![Component::TankVolume(tank)]),
        (3, "engine", 1500.0, vec![Component::Engine(engine)]),
    ]);
    let ephem = nova_sim::fixtures::kerbol_ephemeris();
    let ctx = nova_sim::WorldContext::new(&ephem);
    vessel.initialize_solver(&ctx, 0.0);
    vessel.solve(&ctx);

    let engine_ref = match &vessel.part(3).components[0] {
        Component::Engine(e) => e,
        _ => unreachable!(),
    };
    assert_relative_eq!(engine_ref.normalized_output(vessel.systems()), 0.0);

    // Both tanks should be undisturbed — coupling drove activity to 0.
    let tank_ref = match &vessel.part(2).components[0] {
        Component::TankVolume(t) => t,
        _ => unreachable!(),
    };
    let b_rp1 = tank_ref.buffer_ids()[0];
    assert_relative_eq!(vessel.systems().staging.buffer(b_rp1).rate(), 0.0);
}

// ── Lerp-driven multi-tick drain ──────────────────────────────────────

#[test]
fn tank_contents_drain_over_time_via_lerp() {
    let tank = TankVolume::new(100.0, 10_000.0).add_tank(Resource::Hydrazine, 100.0);
    let mut engine = Engine::new(1.0, 220.0, vec![(Resource::Hydrazine, 1.0)]);
    engine.throttle = 1.0;

    let mut vessel = pod_with_parts(vec![
        (2, "tank", 50.0, vec![Component::TankVolume(tank)]),
        (3, "rcs", 20.0, vec![Component::Engine(engine)]),
    ]);
    let ephem = nova_sim::fixtures::kerbol_ephemeris();
    let ctx = nova_sim::WorldContext::new(&ephem);
    vessel.initialize_solver(&ctx, 0.0);
    vessel.solve(&ctx);

    let drain = 1000.0 / (220.0 * G0) / Resource::Hydrazine.density();
    let bid = match &vessel.part(2).components[0] {
        Component::TankVolume(t) => t.buffer_ids()[0],
        _ => unreachable!(),
    };

    let initial = vessel.systems().staging.buffer(bid).contents();
    assert_relative_eq!(initial, 100.0);

    // Advance the clock 10s; lerp should reflect drain × dt.
    vessel.systems().clock.advance(10.0);
    let after_10 = vessel.systems().staging.buffer(bid).contents();
    assert_relative_eq!(after_10, 100.0 - drain * 10.0, max_relative = 1e-12);
}

// ── Half-throttle propellant drain ────────────────────────────────────

#[test]
fn engine_at_half_throttle_drains_at_half_rate() {
    let tank = TankVolume::new(100.0, 10_000.0).add_tank(Resource::Hydrazine, 100.0);
    let mut engine = Engine::new(1.0, 220.0, vec![(Resource::Hydrazine, 1.0)]);
    engine.throttle = 0.5;

    let mut vessel = pod_with_parts(vec![
        (2, "tank", 50.0, vec![Component::TankVolume(tank)]),
        (3, "rcs", 20.0, vec![Component::Engine(engine)]),
    ]);
    let ephem = nova_sim::fixtures::kerbol_ephemeris();
    let ctx = nova_sim::WorldContext::new(&ephem);
    vessel.initialize_solver(&ctx, 0.0);
    vessel.solve(&ctx);

    let engine_ref = match &vessel.part(3).components[0] {
        Component::Engine(e) => e,
        _ => unreachable!(),
    };
    assert_relative_eq!(engine_ref.normalized_output(vessel.systems()), 0.5);

    let bid = match &vessel.part(2).components[0] {
        Component::TankVolume(t) => t.buffer_ids()[0],
        _ => unreachable!(),
    };
    let full_drain = 1000.0 / (220.0 * G0) / Resource::Hydrazine.density();
    assert_relative_eq!(
        -vessel.systems().staging.buffer(bid).rate(),
        full_drain * 0.5,
        max_relative = 1e-12,
    );
}
