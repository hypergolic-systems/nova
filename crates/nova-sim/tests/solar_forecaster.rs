//! Mirror of `mod/Nova.Tests/Resources/SolarOptimizerTests.cs` and
//! `mod/Nova.Tests/Resources/ShadowCalculatorTests.cs`.
//!
//! The C# tests inject motion via `Func<double, Vec3d>` closures; the
//! Rust port consults `Ephemeris` directly, so the geometric tests
//! here build a synthetic two-body system (sun at root + Kerbin-sized
//! "planet" at +X) that gives full control over the shadow geometry.

use approx::{assert_relative_eq, assert_relative_ne};
use nova_sim::ephem::{Body, BodyId, BodyRotation, Ephemeris};
use nova_sim::math::Vec3d;
use nova_sim::orbit::OrbitalElements;
use nova_sim::resources::solar_forecaster::fibonacci_sphere_direction;
use nova_sim::{Orbit, PanelGeometry, SolarEvent, SolarForecaster};

// ── Optimizer (pure function) ───────────────────────────────────────

#[test]
fn optimizer_no_panels_returns_zero() {
    assert_eq!(SolarForecaster::optimal_rate(&[]), 0.0);
}

#[test]
fn optimizer_single_fixed_panel_returns_charge_rate() {
    let panels = [PanelGeometry {
        direction: Vec3d::new(0.0, 1.0, 0.0),
        charge_rate: 10.0,
        is_tracking: false,
    }];
    assert_relative_eq!(SolarForecaster::optimal_rate(&panels), 10.0, epsilon = 0.2);
}

#[test]
fn optimizer_two_opposed_fixed_panels_returns_one_charge_rate() {
    let panels = [
        PanelGeometry { direction: Vec3d::new(0.0, 1.0, 0.0), charge_rate: 10.0, is_tracking: false },
        PanelGeometry { direction: Vec3d::new(0.0, -1.0, 0.0), charge_rate: 10.0, is_tracking: false },
    ];
    assert_relative_eq!(SolarForecaster::optimal_rate(&panels), 10.0, epsilon = 0.2);
}

#[test]
fn optimizer_two_perpendicular_fixed_panels_optimal_at_45_degrees() {
    // Two panels at 90° apart. Optimal sun direction at 45° between
    // them. Each contributes cos(45°) ≈ 0.707 of charge_rate.
    let panels = [
        PanelGeometry { direction: Vec3d::new(0.0, 1.0, 0.0), charge_rate: 10.0, is_tracking: false },
        PanelGeometry { direction: Vec3d::new(1.0, 0.0, 0.0), charge_rate: 10.0, is_tracking: false },
    ];
    assert_relative_eq!(SolarForecaster::optimal_rate(&panels), 14.14, epsilon = 0.5);
}

#[test]
fn optimizer_single_tracking_panel_returns_charge_rate() {
    // Tracking with vertical axis — full output when sun perpendicular
    // to the axis. sqrt(1 − 0²) = 1 → full charge_rate.
    let panels = [PanelGeometry {
        direction: Vec3d::new(0.0, 1.0, 0.0),
        charge_rate: 20.0,
        is_tracking: true,
    }];
    assert_relative_eq!(SolarForecaster::optimal_rate(&panels), 20.0, epsilon = 0.5);
}

#[test]
fn optimizer_tracking_and_fixed_panel_combined() {
    // Tracking with Y axis + fixed with X normal. Best sun direction
    // is +X: tracking gets 20 (perpendicular to Y), fixed gets 10
    // (cosine 1). Total 30.
    let panels = [
        PanelGeometry { direction: Vec3d::new(0.0, 1.0, 0.0), charge_rate: 20.0, is_tracking: true },
        PanelGeometry { direction: Vec3d::new(1.0, 0.0, 0.0), charge_rate: 10.0, is_tracking: false },
    ];
    assert_relative_eq!(SolarForecaster::optimal_rate(&panels), 30.0, epsilon = 0.5);
}

#[test]
fn optimizer_proportional_distribution() {
    // 3 panels facing +Y, 20+20+50 = 90 W total. Sun aligned with +Y
    // → each at full charge_rate, total = 90.
    let panels = [
        PanelGeometry { direction: Vec3d::new(0.0, 1.0, 0.0), charge_rate: 20.0, is_tracking: false },
        PanelGeometry { direction: Vec3d::new(0.0, 1.0, 0.0), charge_rate: 20.0, is_tracking: false },
        PanelGeometry { direction: Vec3d::new(0.0, 1.0, 0.0), charge_rate: 50.0, is_tracking: false },
    ];
    assert_relative_eq!(SolarForecaster::optimal_rate(&panels), 90.0, epsilon = 1.0);
}

#[test]
fn fibonacci_sphere_directions_are_unit_vectors() {
    for i in 0..200 {
        let d = fibonacci_sphere_direction(i, 200);
        assert_relative_eq!(d.norm(), 1.0, epsilon = 1e-10);
    }
}

// ── Pure cylinder shadow test ───────────────────────────────────────

#[test]
fn is_in_shadow_sun_side_not_in_shadow() {
    let vessel_pos = Vec3d::new(1000.0, 0.0, 0.0);
    let sun_pos = Vec3d::new(1.0e10, 0.0, 0.0);
    assert!(!SolarForecaster::is_in_shadow(vessel_pos, sun_pos, 600.0));
}

#[test]
fn is_in_shadow_dark_side_in_cylinder_in_shadow() {
    let vessel_pos = Vec3d::new(-1000.0, 0.0, 100.0);
    let sun_pos = Vec3d::new(1.0e10, 0.0, 0.0);
    assert!(SolarForecaster::is_in_shadow(vessel_pos, sun_pos, 600.0));
}

#[test]
fn is_in_shadow_dark_side_outside_cylinder_not_in_shadow() {
    let vessel_pos = Vec3d::new(-1000.0, 0.0, 700.0);
    let sun_pos = Vec3d::new(1.0e10, 0.0, 0.0);
    assert!(!SolarForecaster::is_in_shadow(vessel_pos, sun_pos, 600.0));
}

// ── Forecast (Ephemeris-driven) ─────────────────────────────────────

/// Two-body fixture: sun at root, "Planet" with circular orbit such
/// that at UT=0 it sits at (+R, 0, 0) absolute. Lets tests place a
/// vessel at known relative positions and predict shadow geometry by
/// hand. Same numbers the C# `ShadowCalculatorTests` uses.
mod fx {
    use super::*;

    pub const SUN: BodyId = BodyId(0);
    pub const PLANET: BodyId = BodyId(1);

    pub const BODY_RADIUS: f64 = 600_000.0;
    pub const ORBIT_RADIUS: f64 = 700_000.0;
    pub const ORBITAL_PERIOD: f64 = 2400.0;
    pub const PLANET_DISTANCE: f64 = 1.0e10;
    /// Inverse-period for the vessel's circular orbit, used to derive
    /// `mu` from the desired period and orbit radius.
    pub fn vessel_mu() -> f64 {
        let n = std::f64::consts::TAU / ORBITAL_PERIOD;
        let a = ORBIT_RADIUS;
        n * n * a * a * a
    }

    pub fn ephemeris() -> Ephemeris {
        let vessel_mu = vessel_mu();
        // Pick sun mu so the planet's circular orbit has a long
        // period — keeps relative-sun motion small over one vessel
        // period (matches a real Kerbin-around-the-sun setup).
        let planet_period = 1.0e8; // ~3 years
        let n_planet = std::f64::consts::TAU / planet_period;
        let sun_mu = n_planet * n_planet * PLANET_DISTANCE * PLANET_DISTANCE * PLANET_DISTANCE;
        Ephemeris::new(vec![
            Body {
                id: SUN,
                name: "Sun".into(),
                parent: None,
                mu: sun_mu,
                radius: 1.0,
                soi_radius: f64::INFINITY,
                atmosphere: None,
                rotation: BodyRotation::default(),
                orbit: None,
            },
            Body {
                id: PLANET,
                name: "Planet".into(),
                parent: Some(SUN),
                mu: vessel_mu,
                radius: BODY_RADIUS,
                soi_radius: PLANET_DISTANCE,
                atmosphere: None,
                rotation: BodyRotation::default(),
                orbit: Some(OrbitalElements::circular(PLANET_DISTANCE)),
            },
        ])
    }

    /// Vessel orbit phased so periapsis sits at the given direction
    /// relative to the planet (radians from +X in the XY plane).
    /// At UT=0 with mean_anomaly=0, vessel is at periapsis.
    pub fn vessel_orbit_at_phase(phase_rad: f64) -> Orbit {
        Orbit::new(
            OrbitalElements {
                semi_major_axis: ORBIT_RADIUS,
                eccentricity: 0.0,
                inclination: 0.0,
                lan: 0.0,
                arg_periapsis: phase_rad,
                mean_anomaly_at_epoch: 0.0,
                epoch: 0.0,
            },
            PLANET,
        )
    }
}

#[test]
fn root_orbiting_always_sunlit() {
    // Vessel directly orbiting the root star — no occluder, no
    // transition. Mirrors C# `OrbitingSun_AlwaysSunlit` (with the
    // closure-based "orbiting_sun" flag replaced by checking
    // `parent.parent.is_none()`).
    let ephem = fx::ephemeris();
    let f = SolarForecaster::new(&ephem);
    let orbit = Orbit::new(OrbitalElements::circular(fx::PLANET_DISTANCE), fx::SUN);
    let event = f.forecast(&orbit, 0.0);
    assert!(matches!(event, SolarEvent::Sun(dt) if dt.is_infinite()));
}

#[test]
fn sub_solar_point_in_sunlight() {
    // Phase π → vessel at (-r, 0, 0) relative to planet at (+R, 0, 0).
    // Vessel is between the sun (at origin) and the planet centre →
    // sunlit.
    let ephem = fx::ephemeris();
    let f = SolarForecaster::new(&ephem);
    let orbit = fx::vessel_orbit_at_phase(std::f64::consts::PI);
    let event = f.forecast(&orbit, 0.0);
    assert!(event.is_sunlit(), "got {:?}", event);
}

#[test]
fn anti_solar_point_in_shadow() {
    // Phase 0 → vessel at (+r, 0, 0) relative to planet at (+R, 0, 0).
    // Vessel is on the far side of the planet from the sun → in
    // shadow.
    let ephem = fx::ephemeris();
    let f = SolarForecaster::new(&ephem);
    let orbit = fx::vessel_orbit_at_phase(0.0);
    let event = f.forecast(&orbit, 0.0);
    assert!(!event.is_sunlit(), "got {:?}", event);
}

#[test]
fn shadow_entry_transition_time() {
    // Vessel starts at sub-solar (sunlit), should enter shadow
    // when phase reaches π − arcsin(R_body / R_orbit).
    let ephem = fx::ephemeris();
    let f = SolarForecaster::new(&ephem);
    let orbit = fx::vessel_orbit_at_phase(std::f64::consts::PI);
    let event = f.forecast(&orbit, 0.0);

    let half_angle = (fx::BODY_RADIUS / fx::ORBIT_RADIUS).asin();
    let entry_phase = std::f64::consts::PI - half_angle;
    let expected_dt = entry_phase / std::f64::consts::TAU * fx::ORBITAL_PERIOD;

    match event {
        SolarEvent::Sun(dt) => {
            assert_relative_eq!(dt, expected_dt, max_relative = 1e-3);
        }
        _ => panic!("expected Sun, got {:?}", event),
    }
}

#[test]
fn shadow_exit_transition_time() {
    // Vessel starts at anti-solar (in shadow). Exits shadow at phase
    // π + arcsin(R_body / R_orbit) — i.e. arcsin(R/r) past start.
    let ephem = fx::ephemeris();
    let f = SolarForecaster::new(&ephem);
    let orbit = fx::vessel_orbit_at_phase(0.0);
    let event = f.forecast(&orbit, 0.0);

    let half_angle = (fx::BODY_RADIUS / fx::ORBIT_RADIUS).asin();
    let expected_dt = half_angle / std::f64::consts::TAU * fx::ORBITAL_PERIOD;

    match event {
        SolarEvent::Shade(dt) => {
            assert_relative_eq!(dt, expected_dt, max_relative = 1e-3);
        }
        _ => panic!("expected Shade, got {:?}", event),
    }
}

#[test]
fn high_orbit_narrow_shadow_still_detected() {
    // Orbit at 10× body radius — narrow shadow arc (~5.7°). The
    // 200-step search must still land it.
    let ephem = {
        let big_orbit_radius = fx::BODY_RADIUS * 10.0;
        let big_period = 20_000.0;
        let n = std::f64::consts::TAU / big_period;
        let mu = n * n * big_orbit_radius * big_orbit_radius * big_orbit_radius;
        Ephemeris::new(vec![
            Body {
                id: fx::SUN,
                name: "Sun".into(),
                parent: None,
                mu: 1.0,
                radius: 1.0,
                soi_radius: f64::INFINITY,
                atmosphere: None,
                rotation: BodyRotation::default(),
                orbit: None,
            },
            Body {
                id: fx::PLANET,
                name: "Planet".into(),
                parent: Some(fx::SUN),
                mu,
                radius: fx::BODY_RADIUS,
                soi_radius: f64::INFINITY,
                atmosphere: None,
                rotation: BodyRotation::default(),
                orbit: Some(OrbitalElements::circular(fx::PLANET_DISTANCE)),
            },
        ])
    };
    let f = SolarForecaster::new(&ephem);
    let orbit = Orbit::new(
        OrbitalElements {
            semi_major_axis: fx::BODY_RADIUS * 10.0,
            eccentricity: 0.0,
            inclination: 0.0,
            lan: 0.0,
            arg_periapsis: std::f64::consts::PI, // start sunlit
            mean_anomaly_at_epoch: 0.0,
            epoch: 0.0,
        },
        fx::PLANET,
    );
    let event = f.forecast(&orbit, 0.0);
    assert!(event.is_sunlit());
    // Transition must fire within one orbital period.
    assert!(event.dt() < 20_000.0);
}

#[test]
fn moving_sun_affects_transition_time() {
    // The forecaster walks the ephemeris at every sample, so the
    // planet's own orbit around the sun shifts the relative sun
    // direction over the search window. Compare a Kerbin-sized planet
    // with two different orbital periods around the sun and assert the
    // vessel's shadow-entry time differs (small but measurable).
    fn ephem_with_planet_period(planet_period: f64) -> Ephemeris {
        let n = std::f64::consts::TAU / planet_period;
        let sun_mu = n * n * fx::PLANET_DISTANCE * fx::PLANET_DISTANCE * fx::PLANET_DISTANCE;
        Ephemeris::new(vec![
            Body {
                id: fx::SUN,
                name: "Sun".into(),
                parent: None,
                mu: sun_mu,
                radius: 1.0,
                soi_radius: f64::INFINITY,
                atmosphere: None,
                rotation: BodyRotation::default(),
                orbit: None,
            },
            Body {
                id: fx::PLANET,
                name: "Planet".into(),
                parent: Some(fx::SUN),
                mu: fx::vessel_mu(),
                radius: fx::BODY_RADIUS,
                soi_radius: f64::INFINITY,
                atmosphere: None,
                rotation: BodyRotation::default(),
                orbit: Some(OrbitalElements::circular(fx::PLANET_DISTANCE)),
            },
        ])
    }

    let orbit = fx::vessel_orbit_at_phase(std::f64::consts::PI);

    let ephem_slow = ephem_with_planet_period(fx::ORBITAL_PERIOD * 1000.0);
    let ephem_fast = ephem_with_planet_period(fx::ORBITAL_PERIOD * 5.0);
    let f_slow = SolarForecaster::new(&ephem_slow);
    let f_fast = SolarForecaster::new(&ephem_fast);

    let dt_slow = f_slow.forecast(&orbit, 0.0).dt();
    let dt_fast = f_fast.forecast(&orbit, 0.0).dt();

    assert_relative_ne!(dt_slow, dt_fast, epsilon = 1.0);
}
