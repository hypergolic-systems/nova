//! Mirror of `mod/Nova.Tests/Communications/OcclusionTests.cs`.
//!
//! Pure geometry tests for `is_blocked`; small Ephemeris fixtures
//! for `is_any_blocked` (the C# tests use root-only Body objects so
//! their centres sit at the origin — the Rust port mirrors that with
//! root bodies in a tiny Ephemeris).

use nova_sim::comms::{is_any_blocked, is_blocked};
use nova_sim::ephem::{Body, BodyId, BodyRotation, Ephemeris};
use nova_sim::math::Vec3d;

fn root_body(id: u32, name: &str, radius: f64) -> Body {
    Body {
        id: BodyId(id),
        name: name.into(),
        parent: None,
        mu: 1.0,
        radius,
        soi_radius: f64::INFINITY,
        atmosphere: None,
        rotation: BodyRotation::default(),
        orbit: None,
    }
}

#[test]
fn foot_on_segment_distance_less_than_radius_blocks() {
    // Chord A=(-10,0,0) to B=(10,0,0); body at origin, radius 2. Foot
    // is the origin, distance 0 < 2 → blocked.
    let a = Vec3d::new(-10.0, 0.0, 0.0);
    let b = Vec3d::new(10.0, 0.0, 0.0);
    let c = Vec3d::new(0.0, 0.0, 0.0);
    assert!(is_blocked(a, b, c, 2.0));
}

#[test]
fn foot_on_segment_distance_greater_than_radius_clear() {
    let a = Vec3d::new(-10.0, 0.0, 0.0);
    let b = Vec3d::new(10.0, 0.0, 0.0);
    let c = Vec3d::new(0.0, 3.0, 0.0);
    assert!(!is_blocked(a, b, c, 2.0));
}

#[test]
fn foot_outside_segment_past_endpoint_b_clear() {
    // Foot lies at t=1.5, beyond B. Even with the body close to the
    // line and small radius, the segment-distance check rejects it.
    let a = Vec3d::new(-10.0, 0.0, 0.0);
    let b = Vec3d::new(10.0, 0.0, 0.0);
    let c = Vec3d::new(15.0, 0.1, 0.0);
    assert!(!is_blocked(a, b, c, 2.0));
}

#[test]
fn foot_outside_segment_before_endpoint_a_clear() {
    let a = Vec3d::new(-10.0, 0.0, 0.0);
    let b = Vec3d::new(10.0, 0.0, 0.0);
    let c = Vec3d::new(-15.0, 0.1, 0.0);
    assert!(!is_blocked(a, b, c, 2.0));
}

#[test]
fn grazing_distance_equals_radius_clear() {
    // Convention: distance >= radius → clear. Pin it.
    let a = Vec3d::new(-10.0, 0.0, 0.0);
    let b = Vec3d::new(10.0, 0.0, 0.0);
    let c = Vec3d::new(0.0, 2.0, 0.0);
    assert!(!is_blocked(a, b, c, 2.0));
}

#[test]
fn coincident_endpoints_returns_clear() {
    let a = Vec3d::new(0.0, 0.0, 0.0);
    let b = Vec3d::new(0.0, 0.0, 0.0);
    let c = Vec3d::new(0.0, 0.0, 0.0);
    assert!(!is_blocked(a, b, c, 5.0));
}

#[test]
fn is_any_blocked_empty_occluders_returns_false() {
    let ephem = Ephemeris::new(vec![root_body(0, "Solo", 1.0)]);
    let a = Vec3d::new(-10.0, 0.0, 0.0);
    let b = Vec3d::new(10.0, 0.0, 0.0);
    assert!(!is_any_blocked(&[], &ephem, a, b, 0.0));
}

#[test]
fn is_any_blocked_only_one_occluder_blocks_returns_true() {
    let ephem = Ephemeris::new(vec![
        root_body(0, "Far", 1.0),
        root_body(1, "Block", 5.0),
    ]);
    let far = BodyId(0);
    let block = BodyId(1);
    let a = Vec3d::new(-10.0, 0.0, 0.0);
    let b = Vec3d::new(10.0, 0.0, 0.0);
    assert!(is_any_blocked(&[far, block], &ephem, a, b, 0.0));
    assert!(is_any_blocked(&[block, far], &ephem, a, b, 0.0));
}

#[test]
fn is_any_blocked_no_occluder_blocks_returns_false() {
    // Both bodies at origin but radii too small to reach the chord
    // (chord runs through y=5 plane; bodies at origin, radius 0.5).
    let ephem = Ephemeris::new(vec![
        root_body(0, "A", 0.5),
        root_body(1, "B", 0.5),
    ]);
    let a = Vec3d::new(-10.0, 5.0, 0.0);
    let b = Vec3d::new(10.0, 5.0, 0.0);
    assert!(!is_any_blocked(&[BodyId(0), BodyId(1)], &ephem, a, b, 0.0));
}
