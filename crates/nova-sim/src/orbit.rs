use crate::math::Vec3d;
use std::f64::consts::TAU;

/// Keplerian orbital elements relative to a parent body.
///
/// Angles in radians. Conventions match the standard textbook
/// formulation: reference plane is XY, ascending node measured from
/// +X, argument of periapsis measured from ascending node in the
/// orbital plane. The KSP runtime uses Y-up; the C#↔Rust FFI is
/// responsible for any frame transform at the boundary.
#[derive(Copy, Clone, Debug, PartialEq)]
pub struct OrbitalElements {
    /// Semi-major axis, m. Negative for hyperbolic orbits.
    pub semi_major_axis: f64,
    pub eccentricity: f64,
    /// Inclination, rad. 0 = orbit lies in the reference plane.
    pub inclination: f64,
    /// Longitude of the ascending node, rad.
    pub lan: f64,
    /// Argument of periapsis, rad.
    pub arg_periapsis: f64,
    /// Mean anomaly at `epoch`, rad.
    pub mean_anomaly_at_epoch: f64,
    /// Reference time for `mean_anomaly_at_epoch`, UT seconds.
    pub epoch: f64,
}

impl OrbitalElements {
    /// Circular, equatorial orbit at the given semi-major axis.
    pub fn circular(semi_major_axis: f64) -> Self {
        OrbitalElements {
            semi_major_axis,
            eccentricity: 0.0,
            inclination: 0.0,
            lan: 0.0,
            arg_periapsis: 0.0,
            mean_anomaly_at_epoch: 0.0,
            epoch: 0.0,
        }
    }

    /// Mean motion (rad/s) for the given gravitational parameter.
    pub fn mean_motion(&self, mu: f64) -> f64 {
        let a = self.semi_major_axis.abs();
        (mu / (a * a * a)).sqrt()
    }

    /// Orbital period (s). Only meaningful for elliptical orbits;
    /// returns +inf for parabolic/hyperbolic.
    pub fn period(&self, mu: f64) -> f64 {
        if self.eccentricity >= 1.0 || self.semi_major_axis <= 0.0 {
            f64::INFINITY
        } else {
            TAU / self.mean_motion(mu)
        }
    }

    /// Periapsis distance, m.
    pub fn periapsis(&self) -> f64 {
        self.semi_major_axis * (1.0 - self.eccentricity)
    }

    /// Apoapsis distance, m. +inf for non-closed orbits.
    pub fn apoapsis(&self) -> f64 {
        if self.eccentricity >= 1.0 {
            f64::INFINITY
        } else {
            self.semi_major_axis * (1.0 + self.eccentricity)
        }
    }

    /// Position at universal time `ut`, in the parent body's
    /// inertial frame (parent at origin).
    pub fn position_at(&self, mu: f64, ut: f64) -> Vec3d {
        let m = self.mean_anomaly_at_epoch + self.mean_motion(mu) * (ut - self.epoch);
        let e = solve_kepler(m, self.eccentricity);
        let (cos_e, sin_e) = (e.cos(), e.sin());

        // Position in the perifocal frame (x toward periapsis, y toward
        // semi-latus rectum direction at +90° true anomaly).
        let a = self.semi_major_axis;
        let ecc = self.eccentricity;
        let b = a * (1.0 - ecc * ecc).sqrt();
        let perifocal = Vec3d::new(a * (cos_e - ecc), b * sin_e, 0.0);

        rotate_perifocal_to_inertial(perifocal, self.lan, self.inclination, self.arg_periapsis)
    }
}

/// Solve Kepler's equation `M = E - e * sin(E)` for E (eccentric
/// anomaly), given mean anomaly `m` (rad) and eccentricity `e`.
/// Newton iteration; converges to ~1e-12 in <10 iterations for
/// e < 1. Caller is expected to keep e < 1 (elliptical only) — we
/// don't ship hyperbolic propagation yet.
fn solve_kepler(m: f64, e: f64) -> f64 {
    // Wrap M into [-π, π] for fastest Newton convergence.
    let m = ((m + std::f64::consts::PI).rem_euclid(TAU)) - std::f64::consts::PI;
    let mut ea = if e < 0.8 { m } else { std::f64::consts::PI };
    for _ in 0..32 {
        let f = ea - e * ea.sin() - m;
        let fp = 1.0 - e * ea.cos();
        let delta = f / fp;
        ea -= delta;
        if delta.abs() < 1e-13 { break; }
    }
    ea
}

/// Rotate a perifocal-frame vector into the parent inertial frame.
/// `R = Rz(lan) · Rx(inc) · Rz(arg_pe)`.
fn rotate_perifocal_to_inertial(p: Vec3d, lan: f64, inc: f64, argp: f64) -> Vec3d {
    let (cl, sl) = (lan.cos(), lan.sin());
    let (ci, si) = (inc.cos(), inc.sin());
    let (cw, sw) = (argp.cos(), argp.sin());

    // Combined rotation Rz(lan) · Rx(inc) · Rz(argp), expanded.
    let r11 = cl * cw - sl * sw * ci;
    let r12 = -cl * sw - sl * cw * ci;
    let r13 = sl * si;
    let r21 = sl * cw + cl * sw * ci;
    let r22 = -sl * sw + cl * cw * ci;
    let r23 = -cl * si;
    let r31 = sw * si;
    let r32 = cw * si;
    let r33 = ci;

    Vec3d::new(
        r11 * p.x + r12 * p.y + r13 * p.z,
        r21 * p.x + r22 * p.y + r23 * p.z,
        r31 * p.x + r32 * p.y + r33 * p.z,
    )
}

#[cfg(test)]
mod tests {
    use super::*;
    use approx::assert_relative_eq;

    const KERBIN_MU: f64 = 3.5316e12;

    #[test]
    fn circular_orbit_at_epoch_is_on_periapsis() {
        let o = OrbitalElements::circular(700_000.0 + 600_000.0);
        let p = o.position_at(KERBIN_MU, 0.0);
        // mean_anomaly_at_epoch = 0 → eccentric anomaly = 0 → periapsis,
        // which for argp=0, lan=0 sits on +X.
        assert_relative_eq!(p.x, 1_300_000.0, max_relative = 1e-12);
        assert_relative_eq!(p.y, 0.0, epsilon = 1e-6);
        assert_relative_eq!(p.z, 0.0, epsilon = 1e-6);
    }

    #[test]
    fn circular_orbit_returns_to_start_after_one_period() {
        let o = OrbitalElements::circular(800_000.0 + 600_000.0);
        let p0 = o.position_at(KERBIN_MU, 0.0);
        let p1 = o.position_at(KERBIN_MU, o.period(KERBIN_MU));
        assert_relative_eq!(p0.x, p1.x, max_relative = 1e-9);
        assert_relative_eq!(p0.y, p1.y, epsilon = 1e-3);
        assert_relative_eq!(p0.z, p1.z, epsilon = 1e-3);
    }

    #[test]
    fn circular_orbit_at_quarter_period_is_90deg_around() {
        let r = 1_400_000.0;
        let o = OrbitalElements::circular(r);
        let p = o.position_at(KERBIN_MU, o.period(KERBIN_MU) / 4.0);
        // Quarter period → +Y direction (counterclockwise when viewed
        // from +Z, since orbit rotates with positive mean motion).
        assert_relative_eq!(p.x, 0.0, epsilon = 1.0);
        assert_relative_eq!(p.y, r, max_relative = 1e-9);
        assert_relative_eq!(p.z, 0.0, epsilon = 1e-6);
    }

    #[test]
    fn elliptical_orbit_periapsis_apoapsis_distances_match_elements() {
        let o = OrbitalElements {
            semi_major_axis: 1_000_000.0,
            eccentricity: 0.3,
            inclination: 0.0,
            lan: 0.0,
            arg_periapsis: 0.0,
            mean_anomaly_at_epoch: 0.0,
            epoch: 0.0,
        };
        // At epoch (M=0 → E=0), object is at periapsis.
        let pe = o.position_at(KERBIN_MU, 0.0);
        assert_relative_eq!(pe.norm(), o.periapsis(), max_relative = 1e-12);

        // Half period later → apoapsis.
        let half = o.period(KERBIN_MU) / 2.0;
        let ap = o.position_at(KERBIN_MU, half);
        assert_relative_eq!(ap.norm(), o.apoapsis(), max_relative = 1e-9);
    }

    #[test]
    fn keplers_third_law_holds() {
        // T² ∝ a³ — for two orbits at radii r and 2r, periods differ
        // by a factor of 2^(3/2).
        let r = 1_000_000.0;
        let o1 = OrbitalElements::circular(r);
        let o2 = OrbitalElements::circular(2.0 * r);
        let ratio = o2.period(KERBIN_MU) / o1.period(KERBIN_MU);
        assert_relative_eq!(ratio, 2.0_f64.powf(1.5), max_relative = 1e-12);
    }
}
