//! End-to-end scenario tests — author a scenario in pure Rust, then
//! exercise the simulator against it. These are the integration
//! tests that validate "independent Rust simulation testing"
//! (Milestone 2): no KSP, no FFI, no proto.

use approx::{assert_relative_eq, relative_eq};
use nova_sim::fixtures::{ids, kerbin_atmosphere, kerbol_bodies};
use nova_sim::orbit::OrbitalElements;
use nova_sim::{Vessel, VesselId, World};

fn stock_world() -> World {
    World::builder().bodies(kerbol_bodies()).build()
}

#[test]
fn kerbol_system_is_well_formed() {
    let w = stock_world();
    assert_eq!(w.ephemeris.bodies().len(), 5);

    // Hierarchy: Mun & Minmus orbit Kerbin; Kerbin & Duna orbit Sun.
    assert_eq!(w.ephemeris.body(ids::MUN).parent, Some(ids::KERBIN));
    assert_eq!(w.ephemeris.body(ids::MINMUS).parent, Some(ids::KERBIN));
    assert_eq!(w.ephemeris.body(ids::KERBIN).parent, Some(ids::SUN));
    assert_eq!(w.ephemeris.body(ids::DUNA).parent, Some(ids::SUN));
    assert_eq!(w.ephemeris.body(ids::SUN).parent, None);

    // Roots resolve correctly through the chain.
    assert_eq!(w.ephemeris.root(ids::MUN), ids::SUN);
    assert_eq!(w.ephemeris.root(ids::DUNA), ids::SUN);
    assert_eq!(w.ephemeris.root(ids::SUN), ids::SUN);
}

#[test]
fn sun_sits_at_absolute_origin() {
    let w = stock_world();
    let p = w.ephemeris.body_position_absolute(ids::SUN, 1234.5);
    assert_relative_eq!(p.norm(), 0.0);
}

#[test]
fn mun_absolute_position_is_kerbin_plus_relative() {
    let w = stock_world();
    let ut = 5000.0;
    let kerbin_abs = w.ephemeris.body_position_absolute(ids::KERBIN, ut);
    let mun_abs    = w.ephemeris.body_position_absolute(ids::MUN, ut);
    let mun_rel    = w.ephemeris.body_position_relative(ids::MUN, ut);
    let composed   = kerbin_abs + mun_rel;
    assert_relative_eq!(mun_abs.x, composed.x, max_relative = 1e-12);
    assert_relative_eq!(mun_abs.y, composed.y, max_relative = 1e-12);
    assert_relative_eq!(mun_abs.z, composed.z, max_relative = 1e-12);
}

#[test]
fn kerbin_sma_matches_orbital_period() {
    // Kerbin's stock orbital period is approximately 1 Kerbin year =
    // 9_203_545 s. Derive it from sma + Sun's mu.
    let w = stock_world();
    let period = w.ephemeris.orbital_period(ids::KERBIN);
    assert_relative_eq!(period, 9_203_544.6, max_relative = 1e-4);
}

#[test]
fn kerbin_atmosphere_sea_level_is_one_atm() {
    let atm = kerbin_atmosphere();
    assert_relative_eq!(atm.pressure_atm(0.0), 1.0);
    assert_relative_eq!(atm.pressure_atm(70_000.0), 0.0);
    assert!(atm.pressure_atm(35_000.0) > 0.4);
    assert!(atm.pressure_atm(35_000.0) < 0.6);
}

#[test]
fn vacuum_above_atmosphere_top() {
    let w = stock_world();
    assert_relative_eq!(w.ephemeris.pressure_atm(ids::KERBIN, 100_000.0), 0.0);
    // Mun has no atmosphere — every altitude is 0.
    assert_relative_eq!(w.ephemeris.pressure_atm(ids::MUN, 0.0), 0.0);
    assert_relative_eq!(w.ephemeris.pressure_atm(ids::MUN, 10_000.0), 0.0);
}

#[test]
fn vessel_in_low_kerbin_orbit_position_at_epoch() {
    let world = World::builder()
        .bodies(kerbol_bodies())
        .vessel(Vessel::in_orbit(
            VesselId(1),
            "TestSat",
            ids::KERBIN,
            OrbitalElements::circular(700_000.0 + 600_000.0),
        ))
        .build();

    let pos = world.vessel_position_relative(VesselId(1), 0.0).unwrap();
    assert_relative_eq!(pos.norm(), 1_300_000.0, max_relative = 1e-9);

    // Kerbin's absolute position + vessel's relative position == vessel absolute.
    let kerbin_abs = world.ephemeris.body_position_absolute(ids::KERBIN, 0.0);
    let vessel_abs = world.vessel_position_absolute(VesselId(1), 0.0).unwrap();
    assert_relative_eq!((vessel_abs - kerbin_abs).norm(), 1_300_000.0, max_relative = 1e-9);
}

#[test]
fn vessel_returns_to_start_after_one_period() {
    let r = 1_400_000.0; // 800 km altitude
    let world = World::builder()
        .bodies(kerbol_bodies())
        .vessel(Vessel::in_orbit(
            VesselId(7),
            "LKO",
            ids::KERBIN,
            OrbitalElements::circular(r),
        ))
        .build();

    let kerbin_mu = world.ephemeris.body(ids::KERBIN).mu;
    let period = OrbitalElements::circular(r).period(kerbin_mu);

    let p0 = world.vessel_position_relative(VesselId(7), 0.0).unwrap();
    let p1 = world.vessel_position_relative(VesselId(7), period).unwrap();
    // Body's position drifts during a full vessel-period (Kerbin moves
    // around the Sun), so we compare *relative* positions only.
    assert_relative_eq!(p0.x, p1.x, max_relative = 1e-9);
    assert_relative_eq!(p0.y, p1.y, epsilon = 1e-3);
    assert_relative_eq!(p0.z, p1.z, epsilon = 1e-3);
}

#[test]
fn sun_direction_from_kerbin_orbit_points_toward_origin() {
    let world = stock_world();
    // Kerbin at UT=0 sits on +X (circular orbit, default elements).
    let kerbin_abs = world.ephemeris.body_position_absolute(ids::KERBIN, 0.0);
    assert!(kerbin_abs.x > 0.0 && relative_eq!(kerbin_abs.y, 0.0, epsilon = 1e-6));

    let sun_dir = world.sun_direction(kerbin_abs, 0.0);
    // From +X toward origin → roughly -X.
    assert_relative_eq!(sun_dir.x, -1.0, max_relative = 1e-12);
    assert_relative_eq!(sun_dir.y, 0.0, epsilon = 1e-12);
}

#[test]
fn minmus_inclined_orbit_leaves_xy_plane() {
    let w = stock_world();
    // Sample at a few UTs; |z| should rise above zero (orbit
    // inclined 6°).
    let mut max_z = 0.0_f64;
    for ut_step in 0..10 {
        let ut = (ut_step as f64) * 4_000.0;
        let p = w.ephemeris.body_position_relative(ids::MINMUS, ut);
        max_z = max_z.max(p.z.abs());
    }
    assert!(max_z > 1_000_000.0, "expected non-trivial Z motion, got {max_z}");
}
