//! Mirror of `mod/Nova.Tests/Communications/PacketTests.cs`. The C#
//! tests place endpoints at arbitrary positions via closures; the Rust
//! port uses a stub stationary body with two ground stations at
//! controlled lat/lon, which gives us comparable distance control
//! without the closure machinery.

use approx::assert_relative_eq;
use nova_sim::comms::{Antenna, CommsSystem, EndpointId, GroundStationSpec, Job, JobStatus};
use nova_sim::ephem::{Body, BodyId, BodyRotation};
use nova_sim::world::World;

const TESTBODY: BodyId = BodyId(0);

/// Wide-aperture antenna; any reasonable distance keeps the link
/// above the Shannon knee. Mirrors the C# `Flat` fixture.
fn flat(max_rate: f64) -> Antenna {
    Antenna { tx_power: 1.0, gain: 1.0, max_rate, ref_distance: 1.0e6 }
}

/// Stationary single-body world with no rotation (period_seconds set
/// large enough that omega·t is negligible across test windows).
fn stationary_world() -> World {
    let bodies = vec![Body {
        id: TESTBODY,
        name: "Stub".into(),
        parent: None,
        mu: 1.0,
        radius: 100.0,
        soi_radius: f64::INFINITY,
        atmosphere: None,
        rotation: BodyRotation {
            rotates: true,
            period_seconds: 1.0e12,
            initial_rotation_rad: 0.0,
            tidally_locked: false,
        },
        orbit: None,
    }];
    World::builder().bodies(bodies).build()
}

/// Stations live at altitude 1000 m (10× the stub body's radius) so
/// chords between same-body stations safely clear the body's interior
/// and the link's only occluder set is `{TESTBODY}` — which the chord
/// can avoid at sufficient altitude.
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

fn pair_world() -> (World, CommsSystem, EndpointId, EndpointId) {
    let world = stationary_world();
    let mut comms = CommsSystem::new();
    let a = comms.add_ground_station(ground("A", -5.0, vec![flat(100.0)]));
    let b = comms.add_ground_station(ground("B", 5.0, vec![flat(100.0)]));
    (world, comms, a, b)
}

#[test]
fn submit_then_solve_allocates_full_edge_bandwidth() {
    let world = stationary_world();
    let mut comms = CommsSystem::new();
    let a = comms.add_ground_station(ground("A", -5.0, vec![flat(500.0)]));
    let b = comms.add_ground_station(ground("B", 5.0, vec![flat(500.0)]));
    let pid = comms.submit(Job::packet(a, b, 10_000));

    comms.solve(&world, 0.0);

    let p = comms.job(pid).expect("packet");
    assert_eq!(p.status(), JobStatus::Active);
    assert_relative_eq!(p.allocated_rate_bps(), 500.0, max_relative = 1e-6);
}

#[test]
fn solve_across_dt_delivers_bytes() {
    let (world, mut comms, a, b) = pair_world();
    let pid = comms.submit(Job::packet(a, b, 10_000));

    comms.solve(&world, 0.0);
    comms.solve(&world, 5.0);

    let p = comms.job(pid).expect("packet");
    if let Job::Packet { delivered_bytes, .. } = p {
        assert_eq!(*delivered_bytes, 500); // 100 bps × 5 s
    } else {
        panic!("not a Packet");
    }
    assert_eq!(p.status(), JobStatus::Active);
}

#[test]
fn packet_completes_when_all_bytes_delivered() {
    let (world, mut comms, a, b) = pair_world();
    let pid = comms.submit(Job::packet(a, b, 1000));

    comms.solve(&world, 0.0);
    comms.solve(&world, 20.0); // 100 bps × 20 s = 2000, capped at 1000

    let p = comms.job(pid).expect("packet");
    assert_eq!(p.status(), JobStatus::Completed);
    if let Job::Packet { delivered_bytes, total_bytes, .. } = p {
        assert_eq!(*delivered_bytes, 1000);
        assert_eq!(*total_bytes, 1000);
    } else {
        panic!("not a Packet");
    }
    assert_relative_eq!(p.allocated_rate_bps(), 0.0);
}

#[test]
fn cancel_packet_stops_accruing_bytes() {
    let (world, mut comms, a, b) = pair_world();
    let pid = comms.submit(Job::packet(a, b, 10_000));

    comms.solve(&world, 0.0);
    comms.solve(&world, 5.0);
    if let Job::Packet { delivered_bytes, .. } = comms.job(pid).unwrap() {
        assert_eq!(*delivered_bytes, 500);
    }

    assert!(comms.cancel(pid));
    comms.solve(&world, 10.0);

    let p = comms.job(pid).unwrap();
    assert_eq!(p.status(), JobStatus::Cancelled);
    if let Job::Packet { delivered_bytes, .. } = p {
        assert_eq!(*delivered_bytes, 500);
    }
    assert_relative_eq!(p.allocated_rate_bps(), 0.0);
}

#[test]
fn cancel_already_cancelled_returns_false() {
    let (_world, mut comms, a, b) = pair_world();
    let pid = comms.submit(Job::packet(a, b, 1000));
    assert!(comms.cancel(pid));
    assert!(!comms.cancel(pid));
}

#[test]
fn packet_to_endpoint_without_antennas_gets_zero_rate() {
    // Endpoint with no antennas means the destination doesn't even
    // appear in the graph (BuildGraph skips endpoints with no
    // antennas). MaxRatePath finds no path → flow allocated 0.
    let world = stationary_world();
    let mut comms = CommsSystem::new();
    let a = comms.add_ground_station(ground("A", -5.0, vec![flat(100.0)]));
    let silent = comms.add_ground_station(ground("Silent", 5.0, vec![]));
    let pid = comms.submit(Job::packet(a, silent, 1000));

    comms.solve(&world, 0.0);

    let p = comms.job(pid).unwrap();
    assert_relative_eq!(p.allocated_rate_bps(), 0.0);
    if let Job::Packet { delivered_bytes, .. } = p {
        assert_eq!(*delivered_bytes, 0);
    }
}

#[test]
fn max_tick_dt_forecasts_completion() {
    let world = stationary_world();
    let mut comms = CommsSystem::new();
    let a = comms.add_ground_station(ground("A", -5.0, vec![flat(200.0)]));
    let b = comms.add_ground_station(ground("B", 5.0, vec![flat(200.0)]));
    comms.submit(Job::packet(a, b, 1000));

    comms.solve(&world, 0.0);
    // 1000 bytes at 200 bps → 5 s.
    assert_relative_eq!(comms.max_tick_dt(), 5.0, max_relative = 1e-9);
}

#[test]
fn max_tick_dt_no_active_packets_is_infinity_or_horizon_cap() {
    // No packets, no orbital motion → either infinity (no events) or
    // horizon-cap (link's next_event_ut). Either is acceptable; we
    // just assert it's "very large" so the driver doesn't burn solves
    // on a frozen system.
    let world = stationary_world();
    let mut comms = CommsSystem::new();
    let _a = comms.add_ground_station(ground("A", -5.0, vec![flat(1000.0)]));
    let _b = comms.add_ground_station(ground("B", 5.0, vec![flat(1000.0)]));
    comms.solve(&world, 0.0);

    let dt = comms.max_tick_dt();
    assert!(dt >= nova_sim::comms::MAX_HORIZON_SECONDS - 1e-3);
}

#[test]
fn packets_at_different_rates_complete_independently() {
    // Three stations, each pair gets its own edge. Two packets on
    // independent edges; each owns its capacity.
    let world = stationary_world();
    let mut comms = CommsSystem::new();
    let a = comms.add_ground_station(ground("A", 0.0, vec![flat(1000.0)]));
    let b = comms.add_ground_station(ground("B", 5.0, vec![flat(1000.0)]));
    let c = comms.add_ground_station(ground("C", -5.0, vec![flat(1000.0)]));

    let p_slow = comms.submit(Job::packet(a, b, 5000)); // 5 s at 1000 bps
    let p_fast = comms.submit(Job::packet(a, c, 1000)); // 1 s

    comms.solve(&world, 0.0);

    let slow = comms.job(p_slow).unwrap();
    let fast = comms.job(p_fast).unwrap();
    assert_relative_eq!(slow.allocated_rate_bps(), 1000.0, max_relative = 1e-6);
    assert_relative_eq!(fast.allocated_rate_bps(), 1000.0, max_relative = 1e-6);
}
