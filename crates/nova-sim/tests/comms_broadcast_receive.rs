//! Mirror of `mod/Nova.Tests/Communications/BroadcastReceiveTests.cs`.

use approx::assert_relative_eq;
use nova_sim::comms::{Antenna, CommsSystem, EndpointId, GroundStationSpec, Job, JobStatus};
use nova_sim::ephem::{Body, BodyId, BodyRotation};
use nova_sim::world::World;

const TESTBODY: BodyId = BodyId(0);

fn flat(max_rate: f64) -> Antenna {
    Antenna { tx_power: 1.0, gain: 1.0, max_rate, ref_distance: 1.0e6 }
}

fn stationary_world() -> World {
    let bodies = vec![Body {
        id: TESTBODY,
        name: "Stub".into(),
        parent: None,
        mu: 1.0,
        radius: 100.0,
        soi_radius: f64::INFINITY,
        atmosphere: None,
        rotation: BodyRotation { rotates: true, period_seconds: 1.0e12, ..Default::default() },
        orbit: None,
    }];
    World::builder().bodies(bodies).build()
}

fn ground(name: &str, lon_deg: f64, antennas: Vec<Antenna>) -> GroundStationSpec {
    GroundStationSpec {
        name: name.into(),
        primary: TESTBODY,
        latitude_deg: 0.0,
        longitude_deg: lon_deg,
        altitude_m: 1000.0,
        antennas,
    }
}

fn world_pair() -> (World, CommsSystem, EndpointId, EndpointId) {
    let world = stationary_world();
    let mut comms = CommsSystem::new();
    let src = comms.add_ground_station(ground("src", -5.0, vec![flat(1000.0)]));
    let rx = comms.add_ground_station(ground("rx", 5.0, vec![flat(1000.0)]));
    (world, comms, src, rx)
}

#[test]
fn broadcast_no_receivers_no_transmission() {
    let world = stationary_world();
    let mut comms = CommsSystem::new();
    let src = comms.add_ground_station(ground("src", -5.0, vec![flat(1000.0)]));
    let bid = comms.submit(Job::broadcast(src, &"telemetry", 500.0));

    comms.solve(&world, 0.0);
    comms.solve(&world, 10.0);

    let b = comms.job(bid).unwrap();
    assert_relative_eq!(b.allocated_rate_bps(), 0.0);
    if let Job::Broadcast { bytes_sent, .. } = b {
        assert_eq!(*bytes_sent, 0);
    }
}

#[test]
fn broadcast_single_receiver_willingness_below_target_pushes_willingness() {
    let (world, mut comms, src, rx) = world_pair();
    let bid = comms.submit(Job::broadcast(src, &"telemetry", 500.0));
    let rid = comms.submit(Job::receive(rx, &"telemetry", 100.0));

    comms.solve(&world, 0.0);

    assert_relative_eq!(comms.job(bid).unwrap().allocated_rate_bps(), 100.0, max_relative = 1e-6);
    assert_relative_eq!(comms.job(rid).unwrap().allocated_rate_bps(), 100.0, max_relative = 1e-6);
}

#[test]
fn broadcast_single_receiver_willingness_above_target_pushes_target() {
    let (world, mut comms, src, rx) = world_pair();
    let bid = comms.submit(Job::broadcast(src, &"telemetry", 200.0));
    let rid = comms.submit(Job::receive(rx, &"telemetry", 1000.0));

    comms.solve(&world, 0.0);

    assert_relative_eq!(comms.job(bid).unwrap().allocated_rate_bps(), 200.0, max_relative = 1e-6);
    assert_relative_eq!(comms.job(rid).unwrap().allocated_rate_bps(), 200.0, max_relative = 1e-6);
}

#[test]
fn broadcast_two_receivers_fair_split_of_target() {
    let world = stationary_world();
    let mut comms = CommsSystem::new();
    let src = comms.add_ground_station(ground("src", 0.0, vec![flat(1000.0)]));
    let r1 = comms.add_ground_station(ground("r1", -5.0, vec![flat(1000.0)]));
    let r2 = comms.add_ground_station(ground("r2", 5.0, vec![flat(1000.0)]));
    let bid = comms.submit(Job::broadcast(src, &"telemetry", 300.0));
    let rid1 = comms.submit(Job::receive(r1, &"telemetry", 1000.0));
    let rid2 = comms.submit(Job::receive(r2, &"telemetry", 1000.0));

    comms.solve(&world, 0.0);

    assert_relative_eq!(comms.job(rid1).unwrap().allocated_rate_bps(), 150.0, max_relative = 1e-6);
    assert_relative_eq!(comms.job(rid2).unwrap().allocated_rate_bps(), 150.0, max_relative = 1e-6);
    assert_relative_eq!(comms.job(bid).unwrap().allocated_rate_bps(), 300.0, max_relative = 1e-6);
}

#[test]
fn broadcast_two_receivers_one_capped_low_other_claims_rest() {
    let world = stationary_world();
    let mut comms = CommsSystem::new();
    let src = comms.add_ground_station(ground("src", 0.0, vec![flat(2000.0)]));
    let r1 = comms.add_ground_station(ground("r1", -5.0, vec![flat(2000.0)]));
    let r2 = comms.add_ground_station(ground("r2", 5.0, vec![flat(2000.0)]));
    let bid = comms.submit(Job::broadcast(src, &"telemetry", 1000.0));
    let rid1 = comms.submit(Job::receive(r1, &"telemetry", 100.0));
    let rid2 = comms.submit(Job::receive(r2, &"telemetry", 1000.0));

    comms.solve(&world, 0.0);

    assert_relative_eq!(comms.job(rid1).unwrap().allocated_rate_bps(), 100.0, max_relative = 1e-6);
    assert_relative_eq!(comms.job(rid2).unwrap().allocated_rate_bps(), 900.0, max_relative = 1e-6);
    assert_relative_eq!(comms.job(bid).unwrap().allocated_rate_bps(), 1000.0, max_relative = 1e-6);
}

#[test]
fn broadcast_different_keys_dont_match() {
    let (world, mut comms, src, rx) = world_pair();
    let bid = comms.submit(Job::broadcast(src, &"telemetry", 500.0));
    let rid = comms.submit(Job::receive(rx, &"science", 500.0));

    comms.solve(&world, 0.0);

    assert_relative_eq!(comms.job(bid).unwrap().allocated_rate_bps(), 0.0);
    assert_relative_eq!(comms.job(rid).unwrap().allocated_rate_bps(), 0.0);
}

#[test]
fn broadcast_different_key_types_dont_match() {
    let (world, mut comms, src, rx) = world_pair();
    let bid = comms.submit(Job::broadcast(src, &5_i32, 500.0));
    // String "5" hashes differently and has different TypeId.
    let rid = comms.submit(Job::receive(rx, &"5", 500.0));

    comms.solve(&world, 0.0);

    assert_relative_eq!(comms.job(bid).unwrap().allocated_rate_bps(), 0.0);
    assert_relative_eq!(comms.job(rid).unwrap().allocated_rate_bps(), 0.0);
}

#[test]
fn two_broadcasts_same_key_both_feed_receiver() {
    let world = stationary_world();
    let mut comms = CommsSystem::new();
    let s1 = comms.add_ground_station(ground("s1", -5.0, vec![flat(1000.0)]));
    let s2 = comms.add_ground_station(ground("s2", 5.0, vec![flat(1000.0)]));
    let rx = comms.add_ground_station(ground("rx", 0.0, vec![flat(1000.0)]));
    let bid1 = comms.submit(Job::broadcast(s1, &"weather", 100.0));
    let bid2 = comms.submit(Job::broadcast(s2, &"weather", 200.0));
    let rid = comms.submit(Job::receive(rx, &"weather", 1000.0));

    comms.solve(&world, 0.0);

    assert_relative_eq!(comms.job(bid1).unwrap().allocated_rate_bps(), 100.0, max_relative = 1e-6);
    assert_relative_eq!(comms.job(bid2).unwrap().allocated_rate_bps(), 200.0, max_relative = 1e-6);
    assert_relative_eq!(comms.job(rid).unwrap().allocated_rate_bps(), 300.0, max_relative = 1e-6);
}

#[test]
fn cancel_broadcast_stops_transmission() {
    let (world, mut comms, src, rx) = world_pair();
    let bid = comms.submit(Job::broadcast(src, &"k", 200.0));
    let rid = comms.submit(Job::receive(rx, &"k", 1000.0));

    comms.solve(&world, 0.0);
    comms.solve(&world, 5.0);
    let r_bytes_at_5 = match comms.job(rid).unwrap() {
        Job::Receive { bytes_received, .. } => *bytes_received,
        _ => unreachable!(),
    };
    assert_eq!(r_bytes_at_5, 1000); // 200 × 5

    assert!(comms.cancel(bid));
    comms.solve(&world, 10.0);

    let b = comms.job(bid).unwrap();
    let r = comms.job(rid).unwrap();
    assert_eq!(b.status(), JobStatus::Cancelled);
    assert_relative_eq!(r.allocated_rate_bps(), 0.0);
    if let Job::Receive { bytes_received, .. } = r {
        assert_eq!(*bytes_received, 1000); // unchanged
    }
}

#[test]
fn receive_zero_willingness_broadcast_idle() {
    let (world, mut comms, src, rx) = world_pair();
    let bid = comms.submit(Job::broadcast(src, &"k", 500.0));
    let rid = comms.submit(Job::receive(rx, &"k", 0.0));

    comms.solve(&world, 0.0);

    assert_relative_eq!(comms.job(bid).unwrap().allocated_rate_bps(), 0.0);
    assert_relative_eq!(comms.job(rid).unwrap().allocated_rate_bps(), 0.0);
}

#[test]
fn broadcast_integrates_bytes_sent() {
    let (world, mut comms, src, rx) = world_pair();
    let bid = comms.submit(Job::broadcast(src, &"k", 50.0));
    let rid = comms.submit(Job::receive(rx, &"k", 1000.0));

    comms.solve(&world, 0.0);
    comms.solve(&world, 7.0);

    if let Job::Broadcast { bytes_sent, .. } = comms.job(bid).unwrap() {
        assert_eq!(*bytes_sent, 350);
    } else {
        panic!("not a Broadcast");
    }
    if let Job::Receive { bytes_received, .. } = comms.job(rid).unwrap() {
        assert_eq!(*bytes_received, 350);
    } else {
        panic!("not a Receive");
    }
}
