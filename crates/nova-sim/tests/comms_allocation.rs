//! Mirror of `mod/Nova.Tests/Communications/AllocationTests.cs` —
//! the Packet-only subset. Broadcast/Receive cases land alongside
//! step 17 in `comms_broadcast_receive.rs`.

use approx::assert_relative_eq;
use nova_sim::comms::{
    Antenna, CommsSystem, EndpointId, GraphSnapshot, GroundStationSpec, Job, Link,
};
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

fn link<'a>(g: &'a GraphSnapshot, from: EndpointId, to: EndpointId) -> &'a Link {
    g.links
        .iter()
        .find(|l| l.from == from && l.to == to)
        .unwrap_or_else(|| panic!("missing link {:?} → {:?}", from, to))
}

#[test]
fn two_packets_sharing_edge_fair_split() {
    let world = stationary_world();
    let mut comms = CommsSystem::new();
    let a = comms.add_ground_station(ground("A", -5.0, vec![flat(100.0)]));
    let b = comms.add_ground_station(ground("B", 5.0, vec![flat(100.0)]));
    let p1 = comms.submit(Job::packet(a, b, 100_000));
    let p2 = comms.submit(Job::packet(a, b, 100_000));

    comms.solve(&world, 0.0);

    assert_relative_eq!(comms.job(p1).unwrap().allocated_rate_bps(), 50.0, max_relative = 1e-6);
    assert_relative_eq!(comms.job(p2).unwrap().allocated_rate_bps(), 50.0, max_relative = 1e-6);
}

#[test]
fn three_packets_sharing_edge_equal_split() {
    let world = stationary_world();
    let mut comms = CommsSystem::new();
    let a = comms.add_ground_station(ground("A", -5.0, vec![flat(99.0)]));
    let b = comms.add_ground_station(ground("B", 5.0, vec![flat(99.0)]));
    let p1 = comms.submit(Job::packet(a, b, 100_000));
    let p2 = comms.submit(Job::packet(a, b, 100_000));
    let p3 = comms.submit(Job::packet(a, b, 100_000));

    comms.solve(&world, 0.0);

    assert_relative_eq!(comms.job(p1).unwrap().allocated_rate_bps(), 33.0, max_relative = 1e-6);
    assert_relative_eq!(comms.job(p2).unwrap().allocated_rate_bps(), 33.0, max_relative = 1e-6);
    assert_relative_eq!(comms.job(p3).unwrap().allocated_rate_bps(), 33.0, max_relative = 1e-6);
}

#[test]
fn edge_usage_filled_after_allocation() {
    let world = stationary_world();
    let mut comms = CommsSystem::new();
    let a = comms.add_ground_station(ground("A", -5.0, vec![flat(100.0)]));
    let b = comms.add_ground_station(ground("B", 5.0, vec![flat(100.0)]));
    comms.submit(Job::packet(a, b, 100_000));
    comms.submit(Job::packet(a, b, 100_000));

    comms.solve(&world, 0.0);
    let g = comms.graph();
    assert_relative_eq!(link(g, a, b).used_bps, 100.0, max_relative = 1e-6);
    // No reverse-direction flow was submitted.
    assert_relative_eq!(link(g, b, a).used_bps, 0.0);
}

#[test]
fn no_flows_active_graph_links_have_zero_usage() {
    let world = stationary_world();
    let mut comms = CommsSystem::new();
    let _a = comms.add_ground_station(ground("A", -5.0, vec![flat(1000.0)]));
    let _b = comms.add_ground_station(ground("B", 5.0, vec![flat(1000.0)]));
    comms.solve(&world, 0.0);
    assert_eq!(comms.graph().links.len(), 2);
    for l in &comms.graph().links {
        assert_relative_eq!(l.used_bps, 0.0);
    }
}

#[test]
fn graph_recomputed_used_bps_resets_between_solves() {
    let world = stationary_world();
    let mut comms = CommsSystem::new();
    let a = comms.add_ground_station(ground("A", -5.0, vec![flat(100.0)]));
    let b = comms.add_ground_station(ground("B", 5.0, vec![flat(100.0)]));

    let pid = comms.submit(Job::packet(a, b, 100_000));
    comms.solve(&world, 0.0);
    assert_relative_eq!(link(comms.graph(), a, b).used_bps, 100.0, max_relative = 1e-6);

    assert!(comms.cancel(pid));
    comms.solve(&world, 1.0);
    assert_relative_eq!(link(comms.graph(), a, b).used_bps, 0.0);
}
