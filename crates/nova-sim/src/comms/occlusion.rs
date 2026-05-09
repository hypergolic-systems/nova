//! Line-of-sight blocking geometry. Mirrors
//! `mod/Nova.Core/Communications/Occlusion.cs`.
//!
//! A link from A to B is blocked by body C at time `t` when:
//!   - the foot of the perpendicular from C's centre to the line through
//!     A and B falls within the segment (parametric `t ∈ [0, 1]`), AND
//!   - the perpendicular distance is strictly less than `C.radius`.
//!
//! Grazing convention: distance ≥ radius → clear.

use crate::ephem::{BodyId, Ephemeris};
use crate::math::Vec3d;

/// True iff the chord A↔B passes through the sphere of radius `radius`
/// centred at `occluder_centre`. Pure geometry — no body chain or
/// motion.
pub fn is_blocked(
    pos_a: Vec3d,
    pos_b: Vec3d,
    occluder_centre: Vec3d,
    radius: f64,
) -> bool {
    if radius <= 0.0 {
        return false;
    }

    let ab = pos_b - pos_a;
    let len_sq = ab.norm_squared();
    if len_sq < 1e-18 {
        return false;
    }

    let ap = occluder_centre - pos_a;
    let t = ap.dot(ab) / len_sq;
    if !(0.0..=1.0).contains(&t) {
        return false;
    }

    let foot = pos_a + ab * t;
    let dist_sq = (occluder_centre - foot).norm_squared();
    dist_sq < radius * radius
}

/// True iff *any* body in `occluders` blocks the chord at UT `ut`.
/// Each body's centre is resolved via
/// `Ephemeris::body_position_absolute`.
pub fn is_any_blocked(
    occluders: &[BodyId],
    ephem: &Ephemeris,
    pos_a: Vec3d,
    pos_b: Vec3d,
    ut: f64,
) -> bool {
    for &bid in occluders {
        let centre = ephem.body_position_absolute(bid, ut);
        let radius = ephem.body(bid).radius;
        if is_blocked(pos_a, pos_b, centre, radius) {
            return true;
        }
    }
    false
}
