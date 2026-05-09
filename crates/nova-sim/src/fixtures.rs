//! Stock-Kerbol-shaped scenario fixtures for tests.
//!
//! Numbers come from KSP 1.12.5 stock — gravitational parameters and
//! orbits match the live game; atmosphere curves are simplified
//! linear approximations (full multi-key curves are KSP-specific and
//! belong on the C# side at FFI time, not in test scaffolding).

use crate::atmosphere::{Atmosphere, FloatCurve, FloatCurveKey};
use crate::ephem::{Body, BodyId, BodyRotation, Ephemeris};
use crate::orbit::OrbitalElements;
use crate::world_context::WorldContext;

use std::sync::OnceLock;

pub mod ids {
    use super::BodyId;
    pub const SUN: BodyId    = BodyId(0);
    pub const KERBIN: BodyId = BodyId(1);
    pub const MUN: BodyId    = BodyId(2);
    pub const MINMUS: BodyId = BodyId(3);
    pub const DUNA: BodyId   = BodyId(4);
}

/// Approximate stock-Kerbin atmosphere: linear from 1 atm at sea
/// level to vacuum at 70 km.
pub fn kerbin_atmosphere() -> Atmosphere {
    let pressure = FloatCurve::new(vec![
        FloatCurveKey { time: 0.0,     value: 1.0, in_tangent: -1.0 / 70_000.0, out_tangent: -1.0 / 70_000.0 },
        FloatCurveKey { time: 70_000.0, value: 0.0, in_tangent: -1.0 / 70_000.0, out_tangent:  0.0 },
    ]);
    // Crude ISA-ish: 288 K at sea level, 200 K at 70 km.
    let temperature = FloatCurve::new(vec![
        FloatCurveKey { time: 0.0,     value: 288.0, in_tangent: -88.0 / 70_000.0, out_tangent: -88.0 / 70_000.0 },
        FloatCurveKey { time: 70_000.0, value: 200.0, in_tangent: -88.0 / 70_000.0, out_tangent:  0.0 },
    ]);
    Atmosphere {
        depth_m: 70_000.0,
        pressure_curve_atm: pressure,
        temperature_curve_k: temperature,
    }
}

/// Stock-Kerbol body database wrapped in an `Ephemeris`. Convenience
/// helper for unit tests that build a `Vessel` directly (without going
/// through `World::builder`) and need a `WorldContext` to call
/// `Vessel::initialize_solver` / `Vessel::tick` / `Vessel::solve`.
pub fn kerbol_ephemeris() -> Ephemeris {
    Ephemeris::new(kerbol_bodies())
}

/// Shared `'static` Kerbol ephemeris — initialised on first call,
/// reused across tests. Pair with `kerbol_ctx()` to get a one-liner
/// `&WorldContext` in tests that build a `Vessel` directly.
pub fn shared_kerbol_ephemeris() -> &'static Ephemeris {
    static E: OnceLock<Ephemeris> = OnceLock::new();
    E.get_or_init(kerbol_ephemeris)
}

/// One-liner test helper: a fresh `WorldContext` wrapping the shared
/// stock-Kerbol ephemeris. Cheap (one borrow), so tests can call it
/// at every `Vessel::tick`/`solve`/`initialize_solver` site.
pub fn kerbol_ctx() -> WorldContext<'static> {
    WorldContext::new(shared_kerbol_ephemeris())
}

/// The Kerbol system: Sun, Kerbin (with atmosphere + Mun + Minmus),
/// Duna. Use `World::builder().bodies(kerbol_bodies()).build()`.
pub fn kerbol_bodies() -> Vec<Body> {
    vec![
        Body {
            id: ids::SUN,
            name: "Kerbol".into(),
            parent: None,
            mu: 1.1723328e18,
            radius: 261_600_000.0,
            soi_radius: f64::INFINITY,
            atmosphere: None,
            rotation: BodyRotation { rotates: true, period_seconds: 432_000.0, ..Default::default() },
            orbit: None,
        },
        Body {
            id: ids::KERBIN,
            name: "Kerbin".into(),
            parent: Some(ids::SUN),
            mu: 3.5316e12,
            radius: 600_000.0,
            soi_radius: 84_159_286.0,
            atmosphere: Some(kerbin_atmosphere()),
            rotation: BodyRotation { rotates: true, period_seconds: 21_549.425, ..Default::default() },
            orbit: Some(OrbitalElements::circular(13_599_840_256.0)),
        },
        Body {
            id: ids::MUN,
            name: "Mun".into(),
            parent: Some(ids::KERBIN),
            mu: 6.5138398e10,
            radius: 200_000.0,
            soi_radius: 2_429_559.1,
            atmosphere: None,
            rotation: BodyRotation {
                rotates: true,
                period_seconds: 138_984.38,
                tidally_locked: true,
                ..Default::default()
            },
            orbit: Some(OrbitalElements::circular(12_000_000.0)),
        },
        Body {
            id: ids::MINMUS,
            name: "Minmus".into(),
            parent: Some(ids::KERBIN),
            mu: 1.7658e9,
            radius: 60_000.0,
            soi_radius: 2_247_428.4,
            atmosphere: None,
            rotation: BodyRotation { rotates: true, period_seconds: 40_400.0, ..Default::default() },
            orbit: Some(OrbitalElements {
                semi_major_axis: 47_000_000.0,
                eccentricity: 0.0,
                inclination: 6.0_f64.to_radians(),
                lan: 78.0_f64.to_radians(),
                arg_periapsis: 38.0_f64.to_radians(),
                mean_anomaly_at_epoch: 0.9,
                epoch: 0.0,
            }),
        },
        Body {
            id: ids::DUNA,
            name: "Duna".into(),
            parent: Some(ids::SUN),
            mu: 3.0136321e11,
            radius: 320_000.0,
            soi_radius: 47_921_949.0,
            atmosphere: None, // simplified — real Duna has thin atmo
            rotation: BodyRotation { rotates: true, period_seconds: 65_517.859, ..Default::default() },
            orbit: Some(OrbitalElements {
                semi_major_axis: 20_726_155_264.0,
                eccentricity: 0.051,
                inclination: 0.06_f64.to_radians(),
                lan: 135.5_f64.to_radians(),
                arg_periapsis: 0.0,
                mean_anomaly_at_epoch: 3.14,
                epoch: 0.0,
            }),
        },
    ]
}
