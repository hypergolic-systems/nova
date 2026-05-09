//! Closed-form position evaluator tests. The bulk of MotionTests.cs is
//! end-to-end network behavior (bucket horizons, allocator stability,
//! multi-hop reroute) — those land in `comms_network.rs` once the
//! solver is in place. Here we only check `MotionModel` →
//! `Vec3d` evaluation, both Kepler and Surface.

use std::f64::consts::{PI, TAU};

use approx::assert_relative_eq;
use nova_sim::comms::{position_at, surface_offset, MotionModel};
use nova_sim::ephem::{Body, BodyId, BodyRotation, Ephemeris};
use nova_sim::orbit::OrbitalElements;

const KERBIN_MU: f64 = 3.5316e12;
const KERBIN_RADIUS: f64 = 600_000.0;
const KERBIN_ROT_S: f64 = 21_549.425;

const SUN: BodyId = BodyId(0);
const KERBIN: BodyId = BodyId(1);

fn solo_kerbin_ephem() -> Ephemeris {
    Ephemeris::new(vec![Body {
        id: KERBIN,
        name: "Kerbin".into(),
        parent: None,
        mu: KERBIN_MU,
        radius: KERBIN_RADIUS,
        soi_radius: f64::INFINITY,
        atmosphere: None,
        rotation: BodyRotation {
            rotates: true,
            period_seconds: KERBIN_ROT_S,
            initial_rotation_rad: 0.0,
            tidally_locked: false,
        },
        orbit: None,
    }])
}

fn sun_kerbin_ephem() -> Ephemeris {
    Ephemeris::new(vec![
        Body {
            id: SUN,
            name: "Sun".into(),
            parent: None,
            mu: 1.0e20,
            radius: 0.0,
            soi_radius: f64::INFINITY,
            atmosphere: None,
            rotation: BodyRotation::default(),
            orbit: None,
        },
        Body {
            id: KERBIN,
            name: "Kerbin".into(),
            parent: Some(SUN),
            mu: KERBIN_MU,
            radius: KERBIN_RADIUS,
            soi_radius: f64::INFINITY,
            atmosphere: None,
            rotation: BodyRotation {
                rotates: true,
                period_seconds: KERBIN_ROT_S,
                ..Default::default()
            },
            orbit: Some(OrbitalElements::circular(13_599_840_256.0)),
        },
    ])
}

#[test]
fn kepler_motion_yields_orbit_position_at_epoch() {
    // Kepler around Kerbin (no parent above): 700 km circular orbit.
    // At ut=0, M=0, E=0 → x=a, y=0, z=0.
    let ephem = solo_kerbin_ephem();
    let model = MotionModel::Kepler {
        parent: KERBIN,
        elements: OrbitalElements::circular(KERBIN_RADIUS + 700_000.0),
    };
    let p = position_at(&model, &ephem, 0.0);
    assert_relative_eq!(p.x, KERBIN_RADIUS + 700_000.0, max_relative = 1e-12);
    assert_relative_eq!(p.y, 0.0, epsilon = 1e-6);
    assert_relative_eq!(p.z, 0.0, epsilon = 1e-6);
}

#[test]
fn kepler_motion_composes_through_parent_chain() {
    // Kerbin orbits Sun at a=13.6 Gm. A vessel in 700 km Kerbin orbit
    // should sit at distance |Kerbin-pos| + a_orbit from origin.
    let ephem = sun_kerbin_ephem();
    let model = MotionModel::Kepler {
        parent: KERBIN,
        elements: OrbitalElements::circular(KERBIN_RADIUS + 700_000.0),
    };
    let p = position_at(&model, &ephem, 0.0);
    let kerbin_centre = ephem.body_position_absolute(KERBIN, 0.0);
    let from_centre = p - kerbin_centre;
    assert_relative_eq!(
        from_centre.norm(),
        KERBIN_RADIUS + 700_000.0,
        max_relative = 1e-9
    );
}

#[test]
fn surface_offset_at_ut_zero_lat_lon_zero_aligns_x_axis() {
    // lat=0, lon=0, alt=0 at ut=0, init_rot=0 → on +X axis at body
    // radius.
    let r = surface_offset(KERBIN_RADIUS, KERBIN_ROT_S, 0.0, 0.0, 0.0, 0.0, 0.0);
    assert_relative_eq!(r.x, KERBIN_RADIUS, max_relative = 1e-12);
    assert_relative_eq!(r.y, 0.0, epsilon = 1e-9);
    assert_relative_eq!(r.z, 0.0, epsilon = 1e-9);
}

#[test]
fn surface_offset_one_full_period_returns_to_start() {
    let p0 = surface_offset(KERBIN_RADIUS, KERBIN_ROT_S, 0.0, 30.0, 70.0, 0.0, 0.0);
    let p1 = surface_offset(KERBIN_RADIUS, KERBIN_ROT_S, 0.0, 30.0, 70.0, 0.0, KERBIN_ROT_S);
    assert_relative_eq!(p0.x, p1.x, max_relative = 1e-9);
    assert_relative_eq!(p0.y, p1.y, max_relative = 1e-9);
    assert_relative_eq!(p0.z, p1.z, max_relative = 1e-9);
}

#[test]
fn surface_offset_quarter_period_rotates_phase_by_90deg() {
    // lon=0 at ut=0 sits on +X. After T/4, the body has rotated 90°,
    // so the same lat/lon body-fixed point is on +Z (sin(π/2)=1) when
    // initial_rotation=0.
    let p0 = surface_offset(KERBIN_RADIUS, KERBIN_ROT_S, 0.0, 0.0, 0.0, 0.0, 0.0);
    let pq = surface_offset(KERBIN_RADIUS, KERBIN_ROT_S, 0.0, 0.0, 0.0, 0.0, KERBIN_ROT_S / 4.0);
    assert_relative_eq!(p0.x, KERBIN_RADIUS, max_relative = 1e-12);
    assert_relative_eq!(pq.x, 0.0, epsilon = 1e-6);
    assert_relative_eq!(pq.z, KERBIN_RADIUS, max_relative = 1e-9);
}

#[test]
fn surface_motion_initial_rotation_offsets_phase() {
    // Two bodies identical except initial_rotation: π/2 advance in
    // initial rotation should match a forward-time evaluation of T/4
    // on the zero-init body.
    let baseline = surface_offset(KERBIN_RADIUS, KERBIN_ROT_S, 0.0, 0.0, 0.0, 0.0, KERBIN_ROT_S / 4.0);
    let init_rotated = surface_offset(KERBIN_RADIUS, KERBIN_ROT_S, PI / 2.0, 0.0, 0.0, 0.0, 0.0);
    assert_relative_eq!(baseline.x, init_rotated.x, epsilon = 1e-6);
    assert_relative_eq!(baseline.z, init_rotated.z, max_relative = 1e-9);
}

#[test]
fn surface_motion_through_position_at_composes_with_parent() {
    // Surface point on Kerbin (which orbits the Sun) — total absolute
    // position should be Kerbin-centre + body-frame offset.
    let ephem = sun_kerbin_ephem();
    let model = MotionModel::Surface {
        parent: KERBIN,
        latitude_deg: 0.0,
        longitude_deg: 0.0,
        altitude_m: 75.0,
    };
    let ut = 0.0;
    let p = position_at(&model, &ephem, ut);
    let kerbin_centre = ephem.body_position_absolute(KERBIN, ut);
    let local = p - kerbin_centre;
    assert_relative_eq!(local.norm(), KERBIN_RADIUS + 75.0, max_relative = 1e-9);
    let _ = TAU; // silence unused import on stable
}
