//! End-to-end test of `World::tick` driving both per-vessel
//! solvers and the world-level comms graph. Mirrors the M4
//! `engine_burn_drains_tank_to_empty_at_predicted_ut` story but on
//! the EC + comms side: a vessel-with-antenna sends a Packet to KSC
//! over a 100-second tick window; comms solves at every horizon
//! boundary, packet bytes accrue across iterations.

use approx::assert_relative_eq;
use nova_sim::comms::{ksc, Antenna, EndpointId, Job, JobStatus};
use nova_sim::components::{Comms, Component};
use nova_sim::ephem::BodyId;
use nova_sim::fixtures::{ids, kerbol_bodies};
use nova_sim::orbit::OrbitalElements;
use nova_sim::world::{Vessel, VesselId, World};

fn knee(max_rate: f64, ref_distance: f64) -> Antenna {
    Antenna { tx_power: 1.0, gain: 1.0, max_rate, ref_distance }
}

fn vessel_with_antenna(
    id: u32,
    name: &str,
    parent: BodyId,
    orbit: OrbitalElements,
    antenna: Antenna,
) -> Vessel {
    let mut v = Vessel::new(VesselId(id), name, parent, orbit);
    v.add_part(1, "core", 100.0, vec![Component::Comms(Comms::new(antenna))]);
    v
}

#[test]
fn world_tick_runs_initial_solve_and_advances_to_target_ut() {
    let mut world = World::builder()
        .bodies(kerbol_bodies())
        .vessel(vessel_with_antenna(
            1,
            "Sat",
            ids::KERBIN,
            OrbitalElements::circular(600_000.0 + 5_000_000.0),
            knee(1000.0, 1.0e10),
        ))
        .build();
    let _ksc_id = world.comms.add_ground_station(ksc(ids::KERBIN));
    world.vessels[0].initialize_solver(0.0);

    world.tick(100.0);

    // Comms simulation_time advanced to 100.0.
    assert_relative_eq!(world.comms.simulation_time().unwrap(), 100.0, max_relative = 1e-9);
    // Vessel clock kept up.
    assert_relative_eq!(world.vessel(VesselId(1)).systems().clock.ut(), 100.0, max_relative = 1e-9);
}

#[test]
fn world_tick_packet_drains_over_time() {
    // Vessel + KSC both with knee antennas (full ceiling at 5 Mm
    // separation). Packet from vessel → KSC at 1000 bps. After 5 s,
    // 5000 bytes delivered.
    let mut world = World::builder()
        .bodies(kerbol_bodies())
        .vessel(vessel_with_antenna(
            1,
            "Sat",
            ids::KERBIN,
            OrbitalElements::circular(600_000.0 + 5_000_000.0),
            knee(1000.0, 1.0e10),
        ))
        .build();
    world.vessels[0].initialize_solver(0.0);

    let ksc_id = world.comms.add_ground_station(ksc(ids::KERBIN));
    let sat_id = EndpointId::Vessel(VesselId(1));
    let pid = world.comms.submit(Job::packet(sat_id, ksc_id, 100_000));

    world.tick(5.0);

    let p = world.comms.job(pid).unwrap();
    if let Job::Packet { delivered_bytes, .. } = p {
        // 1000 bps × 5 s = 5000.
        assert_eq!(*delivered_bytes, 5000);
    } else {
        panic!("not a Packet");
    }
    assert_eq!(p.status(), JobStatus::Active);
}

#[test]
fn world_tick_packet_completes_at_predicted_boundary() {
    // 1000-byte packet at 1000 bps → 1 s exactly. After tick(2.0),
    // packet should be Completed (the 1-s boundary lands on a
    // re-solve, no overshoot beyond TotalBytes).
    let mut world = World::builder()
        .bodies(kerbol_bodies())
        .vessel(vessel_with_antenna(
            1,
            "Sat",
            ids::KERBIN,
            OrbitalElements::circular(600_000.0 + 5_000_000.0),
            knee(1000.0, 1.0e10),
        ))
        .build();
    world.vessels[0].initialize_solver(0.0);

    let ksc_id = world.comms.add_ground_station(ksc(ids::KERBIN));
    let sat_id = EndpointId::Vessel(VesselId(1));
    let pid = world.comms.submit(Job::packet(sat_id, ksc_id, 1000));

    world.tick(2.0);

    let p = world.comms.job(pid).unwrap();
    assert_eq!(p.status(), JobStatus::Completed);
    if let Job::Packet { delivered_bytes, total_bytes, .. } = p {
        assert_eq!(*delivered_bytes, 1000);
        assert_eq!(*total_bytes, 1000);
    } else {
        panic!("not a Packet");
    }
    // Allocated rate drops to 0 once Completed.
    assert_relative_eq!(p.allocated_rate_bps(), 0.0);
}

#[test]
fn world_tick_with_no_comms_endpoints_still_advances_vessels() {
    // No KSC, no antennas — comms is a no-op but Vessel::tick should
    // still run via the World::tick driver.
    use nova_sim::components::{Battery, Component as Comp};

    let mut v = Vessel::new(
        VesselId(1),
        "Sat",
        ids::KERBIN,
        OrbitalElements::circular(700_000.0 + 600_000.0),
    );
    v.add_part(1, "pod", 100.0, vec![Comp::Battery(Battery::new(100.0))]);
    let mut world = World::builder().bodies(kerbol_bodies()).vessel(v).build();
    world.vessels[0].initialize_solver(0.0);

    world.tick(10.0);

    assert_relative_eq!(world.vessel(VesselId(1)).systems().clock.ut(), 10.0, max_relative = 1e-9);
}
