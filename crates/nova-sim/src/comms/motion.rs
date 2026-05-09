//! Solver hint metadata + closed-form position evaluator. Mirrors
//! `mod/Nova.Core/Communications/MotionModel.cs` and
//! `AnalyticalPosition.cs`.
//!
//! Endpoints expose `position_at(world, ut)` for any caller that needs
//! a numeric position; `MotionModel` is what the analytical horizon
//! solver inspects to decide whether it can compute relative motion in
//! closed form. A `None` motion forces the numerical-bisection path.

use std::f64::consts::TAU;

use crate::ephem::{BodyId, Ephemeris};
use crate::math::Vec3d;
use crate::orbit::OrbitalElements;

/// Solver hint metadata attached to an `Endpoint`. The discriminator
/// drives the analytical-vs-numerical horizon path. A null motion
/// forces the numerical fallback — always safe.
#[derive(Clone, Debug, PartialEq)]
pub enum MotionModel {
    /// Kepler orbit around `parent`. `elements` holds the standard six
    /// orbital elements + epoch; angles in radians.
    Kepler {
        parent: BodyId,
        elements: OrbitalElements,
    },
    /// Fixed surface point on `parent`, rotating with the body.
    /// Captured day-one for the future analytical surface↔Kepler
    /// solver; v1 dispatcher routes Surface links through numerical
    /// because no Surface↔Kepler analytical solver exists yet.
    Surface {
        parent: BodyId,
        latitude_deg: f64,
        longitude_deg: f64,
        altitude_m: f64,
    },
}

/// Closed-form position at UT for the given motion model, in the root
/// inertial frame. Composes recursively through the parent chain via
/// `Ephemeris::body_position_absolute`.
pub fn position_at(model: &MotionModel, ephem: &Ephemeris, ut: f64) -> Vec3d {
    match model {
        MotionModel::Kepler { parent, elements } => {
            let parent_body = ephem.body(*parent);
            let local = elements.position_at(parent_body.mu, ut);
            ephem.body_position_absolute(*parent, ut) + local
        }
        MotionModel::Surface { parent, latitude_deg, longitude_deg, altitude_m } => {
            let parent_body = ephem.body(*parent);
            let local = surface_offset(
                parent_body.radius,
                parent_body.rotation.period_seconds,
                parent_body.rotation.initial_rotation_rad,
                *latitude_deg,
                *longitude_deg,
                *altitude_m,
                ut,
            );
            ephem.body_position_absolute(*parent, ut) + local
        }
    }
}

/// Body-frame offset of a surface point at `ut`. Spin axis is +Y (KSP
/// convention); rotation is eastward (positive `ut` advances
/// +longitude). `body_radius_m + alt_m` sets the radial position.
pub fn surface_offset(
    body_radius_m: f64,
    rotation_period_s: f64,
    initial_rotation_rad: f64,
    latitude_deg: f64,
    longitude_deg: f64,
    altitude_m: f64,
    ut: f64,
) -> Vec3d {
    let omega = TAU / rotation_period_s;
    let lat_rad = latitude_deg.to_radians();
    let lon_rad = longitude_deg.to_radians();
    let radius = body_radius_m + altitude_m;
    let phase = lon_rad + initial_rotation_rad + omega * ut;
    let cos_lat = lat_rad.cos();
    Vec3d::new(
        radius * cos_lat * phase.cos(),
        radius * lat_rad.sin(),
        radius * cos_lat * phase.sin(),
    )
}
