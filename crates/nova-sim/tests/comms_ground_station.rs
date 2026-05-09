//! Mirrors `mod/Nova.Tests/Communications/GroundStationTests.cs`.
//!
//! The C# tests fed a `bodyPositionAt: Func<UT, Vec3d>` closure
//! directly. The Rust port is data-driven — surface endpoints look up
//! body centres through `Ephemeris::body_position_absolute`. So these
//! tests build a minimal `World` with the homeworld at the origin
//! (root body, no orbit), and compose with a real Kerbin-around-Sun
//! orbit for the "tracks homeworld" check.

use approx::assert_relative_eq;
use nova_sim::comms::{ksc, Antenna, Endpoint, GroundStationSpec};
use nova_sim::ephem::{Body, BodyId, BodyRotation};
use nova_sim::fixtures::{ids, kerbol_bodies};
use nova_sim::orbit::OrbitalElements;
use nova_sim::world::{Vessel, VesselId, World};

const TESTBODY: BodyId = BodyId(0);

fn one_antenna() -> Vec<Antenna> {
    vec![Antenna { tx_power: 1.0, gain: 1.0, max_rate: 1.0, ref_distance: 1.0 }]
}

/// Single root body at origin. `radius`/`sidereal` are configurable;
/// initial rotation is 0.
fn world_with_root_body(radius: f64, sidereal_s: f64) -> World {
    let bodies = vec![Body {
        id: TESTBODY,
        name: "TestBody".into(),
        parent: None,
        mu: 1.0,
        radius,
        soi_radius: f64::INFINITY,
        atmosphere: None,
        rotation: BodyRotation {
            rotates: true,
            period_seconds: sidereal_s,
            initial_rotation_rad: 0.0,
            tidally_locked: false,
        },
        orbit: None,
    }];
    World::builder().bodies(bodies).build()
}

fn ground(spec: GroundStationSpec) -> Endpoint {
    spec.into_endpoint(0)
}

#[test]
fn surface_endpoint_equator_lon_zero_at_ut_zero_lies_on_plus_x() {
    let world = world_with_root_body(100.0, 10.0);
    let ep = ground(GroundStationSpec {
        name: "test".into(),
        primary: TESTBODY,
        latitude_deg: 0.0,
        longitude_deg: 0.0,
        altitude_m: 0.0,
        antennas: one_antenna(),
    });
    let p = ep.position_at(&world, 0.0);
    assert_relative_eq!(p.x, 100.0, epsilon = 1e-9);
    assert_relative_eq!(p.y, 0.0, epsilon = 1e-9);
    assert_relative_eq!(p.z, 0.0, epsilon = 1e-9);
}

#[test]
fn surface_endpoint_rotates_eastward_half_period_flips_sign() {
    let world = world_with_root_body(100.0, 10.0);
    let ep = ground(GroundStationSpec {
        name: "test".into(),
        primary: TESTBODY,
        latitude_deg: 0.0,
        longitude_deg: 0.0,
        altitude_m: 0.0,
        antennas: one_antenna(),
    });
    let p = ep.position_at(&world, 5.0); // half a sidereal day
    assert_relative_eq!(p.x, -100.0, epsilon = 1e-9);
    assert_relative_eq!(p.y, 0.0, epsilon = 1e-9);
    assert_relative_eq!(p.z, 0.0, epsilon = 1e-6);
}

#[test]
fn surface_endpoint_north_pole_position_invariant_under_rotation() {
    let world = world_with_root_body(100.0, 10.0);
    let ep = ground(GroundStationSpec {
        name: "pole".into(),
        primary: TESTBODY,
        latitude_deg: 90.0,
        longitude_deg: 0.0,
        altitude_m: 0.0,
        antennas: one_antenna(),
    });
    let p0 = ep.position_at(&world, 0.0);
    let p1 = ep.position_at(&world, 2.5);
    let p2 = ep.position_at(&world, 7.7);
    assert_relative_eq!(p0.x, 0.0, epsilon = 1e-9);
    assert_relative_eq!(p0.y, 100.0, epsilon = 1e-9);
    assert_relative_eq!(p0.z, 0.0, epsilon = 1e-9);
    assert_relative_eq!(p0.x, p1.x, epsilon = 1e-9);
    assert_relative_eq!(p0.y, p1.y, epsilon = 1e-9);
    assert_relative_eq!(p0.z, p1.z, epsilon = 1e-9);
    assert_relative_eq!(p0.x, p2.x, epsilon = 1e-9);
    assert_relative_eq!(p0.y, p2.y, epsilon = 1e-9);
    assert_relative_eq!(p0.z, p2.z, epsilon = 1e-9);
}

#[test]
fn surface_endpoint_distance_from_body_centre_equals_radius_plus_altitude() {
    let world = world_with_root_body(600_000.0, 21_549.425);
    let ep = ground(GroundStationSpec {
        name: "wherever".into(),
        primary: TESTBODY,
        latitude_deg: 37.0,
        longitude_deg: -122.0,
        altitude_m: 250.0,
        antennas: one_antenna(),
    });
    for i in 0..5 {
        let ut = (i as f64) * 1234.0;
        let p = ep.position_at(&world, ut);
        assert_relative_eq!(p.norm(), 600_250.0, max_relative = 1e-9);
    }
}

#[test]
fn surface_endpoint_tracks_orbiting_body() {
    // Replacement for the C#-side moving-body test. Build a real
    // Kerbol → Kerbin world and verify that at any UT the ground
    // endpoint sits at distance (Kerbin.radius + alt) from Kerbin's
    // centre — i.e. the surface point is glued to the homeworld and
    // the homeworld is moving.
    let world = World::builder()
        .bodies(kerbol_bodies())
        // World needs at least one vessel to satisfy builder; add a
        // stationary Kerbin orbiter that we don't read.
        .vessel(Vessel::in_orbit(
            VesselId(99),
            "Filler",
            ids::KERBIN,
            OrbitalElements::circular(1_000_000.0),
        ))
        .build();
    let ep = ground(ksc(ids::KERBIN));
    for i in 0..5 {
        let ut = (i as f64) * 1234.0;
        let p = ep.position_at(&world, ut);
        let kerbin_centre = world.ephemeris.body_position_absolute(ids::KERBIN, ut);
        assert_relative_eq!((p - kerbin_centre).norm(), 600_075.0, max_relative = 1e-9);
    }
}

#[test]
fn ksc_has_one_antenna_and_id_matches() {
    let spec = ksc(ids::KERBIN);
    assert_eq!(spec.name, "KSC");
    assert_eq!(spec.antennas.len(), 1);
    assert!(spec.antennas[0].tx_power > 0.0);
    assert!(spec.antennas[0].max_rate > 0.0);
}

#[test]
fn ksc_position_magnitude_equals_kerbin_radius_plus_altitude() {
    // Build a stub world where Kerbin sits at origin (no orbit) so
    // KSC's magnitude isolates radius + altitude.
    let kerbin_only = vec![Body {
        id: ids::KERBIN,
        name: "Kerbin".into(),
        parent: None,
        mu: 1.0,
        radius: 600_000.0,
        soi_radius: f64::INFINITY,
        atmosphere: None,
        rotation: BodyRotation {
            rotates: true,
            period_seconds: 21_549.425,
            ..Default::default()
        },
        orbit: None,
    }];
    let world = World::builder().bodies(kerbin_only).build();
    let ep = ground(ksc(ids::KERBIN));
    let p = ep.position_at(&world, 123.0);
    assert_relative_eq!(p.norm(), 600_075.0, max_relative = 1e-6);
}
