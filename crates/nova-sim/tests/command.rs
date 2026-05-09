//! Command + Battery integration. Mirrors the C#
//! `Nova.Tests/Components/Control/CommandTests.cs` shape on the
//! Rust side.
//!
//! Phase-1 scope: idle-draw only. Test load returns alongside Engine
//! throttle in a later PR.

use approx::assert_relative_eq;
use nova_sim::components::{Battery, Command, Component};
use nova_sim::fixtures::{ids, kerbol_ctx};
use nova_sim::orbit::OrbitalElements;
use nova_sim::{Vessel, VesselId};

fn pod_with_components(components: Vec<Component>) -> Vessel {
    let mut v = Vessel::in_orbit(
        VesselId(1),
        "TestVessel",
        ids::KERBIN,
        OrbitalElements::circular(700_000.0 + 600_000.0),
    );
    v.add_part(1, "pod", 100.0, components);
    v
}

#[test]
fn command_idle_draw_drains_battery() {
    // 100 EC battery + 1 EC/s avionics. After 5 s, contents = 95 EC.
    let mut v = pod_with_components(vec![
        Component::Battery(Battery::new(100.0).with_flow_limits(1000.0, 1000.0)),
        Component::Command(Command::new(1.0)),
    ]);
    v.initialize_solver(&kerbol_ctx(), 0.0);
    v.tick(&kerbol_ctx(), 5.0);

    let bid = match &v.part(1).components[0] {
        Component::Battery(b) => b.buffer_id().unwrap(),
        _ => unreachable!(),
    };
    assert_relative_eq!(
        v.systems().process.buffer(bid).contents(),
        95.0,
        max_relative = 1e-6,
    );
}

#[test]
fn command_idle_draw_lands_on_empty_event_boundary() {
    // 10 EC battery + 1 EC/s avionics → empties at t=10 s.
    // Tick to t=20 s: at t=10 the LP re-solves with empty buffer
    // (supply UB drops to 0), command activity drops to 0, contents
    // stay at 0 for the remaining 10 s.
    let mut v = pod_with_components(vec![
        Component::Battery(Battery::new(10.0).with_flow_limits(1000.0, 1000.0)),
        Component::Command(Command::new(1.0)),
    ]);
    v.initialize_solver(&kerbol_ctx(), 0.0);
    v.tick(&kerbol_ctx(), 20.0);

    let bid = match &v.part(1).components[0] {
        Component::Battery(b) => b.buffer_id().unwrap(),
        _ => unreachable!(),
    };
    assert_relative_eq!(
        v.systems().process.buffer(bid).contents(),
        0.0,
        epsilon = 1e-6,
    );
    assert_relative_eq!(v.systems().clock.ut(), 20.0, max_relative = 1e-9);

    let cmd = match &v.part(1).components[1] {
        Component::Command(c) => c,
        _ => unreachable!(),
    };
    assert_relative_eq!(cmd.idle_activity(v.systems()), 0.0, epsilon = 1e-6);
}

#[test]
fn command_with_no_idle_draw_skips_device() {
    // idle_draw = 0 → no device registered, no demand, battery untouched.
    let mut v = pod_with_components(vec![
        Component::Battery(Battery::new(50.0).with_flow_limits(1000.0, 1000.0)),
        Component::Command(Command::new(0.0)),
    ]);
    v.initialize_solver(&kerbol_ctx(), 0.0);
    v.tick(&kerbol_ctx(), 5.0);

    let bid = match &v.part(1).components[0] {
        Component::Battery(b) => b.buffer_id().unwrap(),
        _ => unreachable!(),
    };
    assert_relative_eq!(
        v.systems().process.buffer(bid).contents(),
        50.0,
        max_relative = 1e-6,
    );

    let cmd = match &v.part(1).components[1] {
        Component::Command(c) => c,
        _ => unreachable!(),
    };
    // No device registered → no activity to read.
    assert_relative_eq!(cmd.idle_activity(v.systems()), 0.0, epsilon = 1e-9);
}

#[test]
fn command_at_high_priority_wins_over_low_priority_load() {
    // Avionics at Priority::High should fully satisfy ahead of an
    // opportunistic Low-priority consumer when the bus is contended.
    // 5 EC battery, 1000 EC/s flow cap; avionics 5 EC/s + a 1000 EC/s
    // Low-pri load. Battery supplies 1000 EC/s peak; avionics gets its
    // full 5; the residual 995 goes to the Low load.
    use nova_sim::resource::Resource;
    use nova_sim::systems::process::Priority;

    let mut v = pod_with_components(vec![
        Component::Battery(Battery::new(5.0).with_flow_limits(1000.0, 1000.0)),
        Component::Command(Command::new(5.0)),
    ]);
    v.initialize_solver(&kerbol_ctx(), 0.0);

    // Inject a Low-priority opportunistic consumer.
    let sys = v.systems.as_mut().unwrap();
    let lo = sys.process.add_device(Priority::Low);
    sys.process
        .device_mut(lo)
        .add_input(Resource::ElectricCharge, 1000.0);
    sys.process.device_mut(lo).demand = 1.0;

    v.solve(&kerbol_ctx());

    let cmd = match &v.part(1).components[1] {
        Component::Command(c) => c,
        _ => unreachable!(),
    };
    assert_relative_eq!(cmd.idle_activity(v.systems()), 1.0, max_relative = 1e-6);
}
