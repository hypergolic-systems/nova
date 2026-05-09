//! Battery + Vessel::tick integration. Ensures the Process LP runs
//! every tick alongside the staging solver, ProcessFlowSystem's
//! `max_tick_dt` is merged into the tick driver's dt clamp, and the
//! Battery component's Buffer drains correctly across multiple ticks.
//!
//! This is the M4-tick-driver story for Uniform resources — a
//! mirror of `tick.rs::engine_burn_drains_tank_to_empty_at_predicted_ut`
//! but on the EC bus instead of the propellant bus.

use approx::assert_relative_eq;
use nova_sim::components::{Battery, Component};
use nova_sim::fixtures::ids;
use nova_sim::orbit::OrbitalElements;
use nova_sim::resource::Resource;
use nova_sim::systems::process::Priority;
use nova_sim::{Vessel, VesselId};

/// Build a vessel with a single pod that hosts the supplied components,
/// in a low Kerbin orbit. Mirrors the staging-side `pod_with_parts`
/// fixture pattern.
fn pod_with_components(components: Vec<Component>) -> Vessel {
    let mut v = Vessel::new(
        VesselId(1),
        "TestVessel",
        ids::KERBIN,
        OrbitalElements::circular(700_000.0 + 600_000.0),
    );
    v.add_part(1, "pod", 100.0, components);
    v
}

#[test]
fn battery_drains_under_consumer_over_tick() {
    // Battery 100 EC + a synthetic Process consumer at 10 EC/s.
    // After 5 s, contents should be 50 EC (linear lerp).
    let mut v = pod_with_components(vec![Component::Battery(
        Battery::new(100.0).with_flow_limits(1000.0, 1000.0),
    )]);
    v.initialize_solver(0.0);

    // Inject a consumer device into the Process system. Components
    // that consume EC will be ported in their own milestones; for
    // this test we model the load directly.
    let sys = v.systems.as_mut().unwrap();
    let consumer = sys.process.add_device(Priority::Low);
    sys.process
        .device_mut(consumer)
        .add_input(Resource::ElectricCharge, 10.0);
    sys.process.device_mut(consumer).demand = 1.0;

    v.tick(5.0);

    let bid = match &v.part(1).components[0] {
        Component::Battery(b) => b.buffer_id().unwrap(),
        _ => unreachable!(),
    };
    assert_relative_eq!(
        v.systems().process.buffer(bid).contents(),
        50.0,
        max_relative = 1e-6,
    );
}

#[test]
fn battery_drain_lands_on_empty_event_boundary() {
    // 100 EC battery, 10 EC/s consumer → empties at t=10 s.
    // Tick to t=20 s: at t=10 the LP re-solves with empty buffer
    // (supply UB drops to 0), consumer activity drops to 0, contents
    // stay at 0 for the remaining 10 s.
    let mut v = pod_with_components(vec![Component::Battery(
        Battery::new(100.0).with_flow_limits(1000.0, 1000.0),
    )]);
    v.initialize_solver(0.0);

    let sys = v.systems.as_mut().unwrap();
    let consumer = sys.process.add_device(Priority::Low);
    sys.process
        .device_mut(consumer)
        .add_input(Resource::ElectricCharge, 10.0);
    sys.process.device_mut(consumer).demand = 1.0;

    v.tick(20.0);

    let bid = match &v.part(1).components[0] {
        Component::Battery(b) => b.buffer_id().unwrap(),
        _ => unreachable!(),
    };
    assert_relative_eq!(
        v.systems().process.buffer(bid).contents(),
        0.0,
        epsilon = 1e-6,
    );
    // Clock should have advanced fully to 20.0 (event-driven loop
    // doesn't get stuck on the empty boundary).
    assert_relative_eq!(v.systems().clock.ut(), 20.0, max_relative = 1e-9);

    // After the empty event, consumer activity is 0 (no supply).
    assert_relative_eq!(
        v.systems().process.device(consumer).activity,
        0.0,
        epsilon = 1e-6,
    );
}

#[test]
fn battery_fills_from_excess_producer_over_tick() {
    // 50/100 battery, +5 EC/s net (producer 10, consumer 5) → full at t=10 s.
    let mut v = pod_with_components(vec![Component::Battery(
        Battery::new(100.0)
            .with_contents(50.0)
            .with_flow_limits(1000.0, 1000.0),
    )]);
    v.initialize_solver(0.0);

    let sys = v.systems.as_mut().unwrap();
    let producer = sys.process.add_device(Priority::Low);
    sys.process
        .device_mut(producer)
        .add_output(Resource::ElectricCharge, 10.0);
    sys.process.device_mut(producer).demand = 1.0;

    let consumer = sys.process.add_device(Priority::Low);
    sys.process
        .device_mut(consumer)
        .add_input(Resource::ElectricCharge, 5.0);
    sys.process.device_mut(consumer).demand = 1.0;

    v.tick(8.0);

    let bid = match &v.part(1).components[0] {
        Component::Battery(b) => b.buffer_id().unwrap(),
        _ => unreachable!(),
    };
    // After 8 s of net +5 EC/s, contents = 50 + 40 = 90.
    assert_relative_eq!(
        v.systems().process.buffer(bid).contents(),
        90.0,
        max_relative = 1e-6,
    );
}

#[test]
fn battery_and_engine_tick_run_both_solvers() {
    // Sanity: both staging (engines, tanks) and process (battery)
    // solve from the same Vessel::tick. Battery doesn't interact
    // with engines but should be processed without disturbing the
    // staging solver. No assertion on contents — just no panics
    // and clock advances.
    use nova_sim::components::{Engine, TankVolume};

    let mut v = Vessel::new(
        VesselId(2),
        "MixedVessel",
        ids::KERBIN,
        OrbitalElements::circular(700_000.0 + 600_000.0),
    );
    v.add_part(1, "pod", 100.0, vec![Component::Battery(Battery::new(100.0))]);
    let tank = TankVolume::new(50.0, 1.0e9).add_tank(Resource::Hydrazine, 50.0);
    v.add_part(2, "tank", 0.0, vec![Component::TankVolume(tank)]);
    v.set_parent(2, 1);
    let mut engine = Engine::new(1.0, 220.0, vec![(Resource::Hydrazine, 1.0)]);
    engine.throttle = 1.0;
    v.add_part(3, "engine", 0.0, vec![Component::Engine(engine)]);
    v.set_parent(3, 1);

    v.initialize_solver(0.0);
    v.tick(1.0);

    assert_relative_eq!(v.systems().clock.ut(), 1.0, max_relative = 1e-9);
}
