//! Solar oracle for shadow forecasting and panel-array optimisation.
//!
//! Bundles two algorithms that both need `&Ephemeris` access — the
//! shadow forecast (cylinder occlusion test + bisection refine on the
//! orbit) and the optimal-collection-rate search (Fibonacci-sphere
//! sweep over panel directions). Mirrors
//! `mod/Nova.Core/Resources/{ShadowCalculator,SolarOptimizer}.cs`,
//! collapsed onto one type since both share the ephem borrow.
//!
//! Returned events use *relative* dt (seconds from the queried UT)
//! rather than absolute UT — the call site converts to absolute with
//! `ut + dt` when feeding `valid_until` to a Process device.

use crate::ephem::{BodyId, Ephemeris};
use crate::math::Vec3d;
use crate::orbit::OrbitalElements;

/// Vessel orbit descriptor — a thin pair of parent body + Keplerian
/// elements composed at the call site. Not stored on `Vessel`; the
/// existing `(parent: BodyId, orbit: OrbitalElements)` fields stay
/// separate, and `forecast` callers build this on the fly.
#[derive(Copy, Clone, Debug)]
pub struct Orbit {
    pub elements: OrbitalElements,
    pub parent: BodyId,
}

impl Orbit {
    pub fn new(elements: OrbitalElements, parent: BodyId) -> Self {
        Orbit { elements, parent }
    }
}

/// Result of `SolarForecaster::forecast`. The variant tag carries the
/// *current* sunlit state and `dt` is the seconds-from-now until the
/// next sun/shade transition. `+∞` when no transition is reachable
/// (root-orbit, sub-solar in a hyperbolic flyby, etc.).
#[derive(Copy, Clone, Debug, PartialEq)]
pub enum SolarEvent {
    /// Currently sunlit; `dt` until shadow entry.
    Sun(f64),
    /// Currently shadowed; `dt` until shadow exit.
    Shade(f64),
}

impl SolarEvent {
    pub fn is_sunlit(self) -> bool {
        matches!(self, SolarEvent::Sun(_))
    }

    pub fn dt(self) -> f64 {
        match self {
            SolarEvent::Sun(dt) | SolarEvent::Shade(dt) => dt,
        }
    }
}

/// One panel's geometry input to `optimal_rate`. The forecaster doesn't
/// borrow the component directly — callers extract these tuples to keep
/// the optimiser independent of `Component` shape.
#[derive(Copy, Clone, Debug)]
pub struct PanelGeometry {
    pub direction: Vec3d,
    pub charge_rate: f64,
    pub is_tracking: bool,
}

/// Borrowed oracle. Cheap to construct (one reference), so the world
/// rebuilds it per `World::tick` — no caching needed.
pub struct SolarForecaster<'a> {
    ephem: &'a Ephemeris,
}

// ── Shadow algorithm constants (mirror C# ShadowCalculator.cs) ──────
const SEARCH_STEPS: usize = 200;
const MAX_SEARCH_HORIZON: f64 = 86_400.0;
const HYPERBOLIC_THRESHOLD: f64 = 0.1;
const PERIOD_FRACTION: f64 = 1e-6;

// ── Optimiser constants (mirror C# SolarOptimizer.cs) ───────────────
const SAMPLE_COUNT: usize = 200;

impl<'a> SolarForecaster<'a> {
    pub fn new(ephem: &'a Ephemeris) -> Self {
        SolarForecaster { ephem }
    }

    /// Pure geometric cylinder shadow test. `vessel_pos` and `sun_pos`
    /// are in the same frame (typically the parent body's centre as
    /// origin). Returns true when the vessel is on the anti-sun side
    /// of the body AND inside the umbra cylinder of radius `body_radius`.
    pub fn is_in_shadow(vessel_pos: Vec3d, sun_pos: Vec3d, body_radius: f64) -> bool {
        let sun_dir = sun_pos.normalize();
        let dot = vessel_pos.dot(sun_dir);
        if dot >= 0.0 {
            return false;
        }
        let perp = vessel_pos - sun_dir * dot;
        perp.norm() < body_radius
    }

    /// Forecast the next sun/shade transition for a vessel at `ut`.
    ///
    /// Algorithm (mirrors `ShadowCalculator.Compute`):
    /// 1. Sample state at `ut`. Done if the vessel orbits the root star
    ///    directly (no occluder ⇒ always sunlit, no transition).
    /// 2. Walk forward in 200 even steps over `period` (or 86400s for
    ///    hyperbolic orbits). First step that flips the shadow state
    ///    triggers a bisection refine on `[lo, hi]` to a tolerance of
    ///    `period × 1e-6` (or 0.1s hyperbolic).
    /// 3. No flip found ⇒ return current state with `dt = horizon`.
    pub fn forecast(&self, orbit: &Orbit, ut: f64) -> SolarEvent {
        // Vessels in direct orbit of the root star have no occluder —
        // always sunlit, no scheduled transition.
        if self.ephem.body(orbit.parent).parent.is_none() {
            return SolarEvent::Sun(f64::INFINITY);
        }

        let parent_radius = self.ephem.body(orbit.parent).radius;
        let parent_mu = self.ephem.body(orbit.parent).mu;
        let period = orbit.elements.period(parent_mu);

        let pos_at = |t: f64| self.vessel_pos_relative_to_parent(orbit, t);
        let sun_at = |t: f64| self.sun_pos_relative_to_parent(orbit, t);

        let in_shadow_now = Self::is_in_shadow(pos_at(ut), sun_at(ut), parent_radius);

        let hyperbolic = period.is_infinite();
        let horizon = if hyperbolic { MAX_SEARCH_HORIZON } else { period };
        let step = horizon / SEARCH_STEPS as f64;
        let threshold = if hyperbolic {
            HYPERBOLIC_THRESHOLD
        } else {
            horizon * PERIOD_FRACTION
        };

        for i in 1..=SEARCH_STEPS {
            let t = ut + i as f64 * step;
            let in_shadow = Self::is_in_shadow(pos_at(t), sun_at(t), parent_radius);
            if in_shadow != in_shadow_now {
                let mut lo = t - step;
                let mut hi = t;
                while hi - lo > threshold {
                    let mid = 0.5 * (lo + hi);
                    if Self::is_in_shadow(pos_at(mid), sun_at(mid), parent_radius)
                        == in_shadow_now
                    {
                        lo = mid;
                    } else {
                        hi = mid;
                    }
                }
                let transition = 0.5 * (lo + hi);
                let dt = (transition - ut).max(0.0);
                return Self::event(in_shadow_now, dt);
            }
        }

        // No flip within the horizon — return current state. dt is the
        // horizon length; in elliptical cases the next solve a full
        // period later is fine, in hyperbolic cases we get a cheap
        // periodic re-check.
        Self::event(in_shadow_now, horizon)
    }

    /// Maximum total power achievable across the panel layout, found
    /// by Fibonacci-sphere sampling of sun directions. Returns 0 when
    /// the panel list is empty. Pure function of geometry — no ephem
    /// access needed; lives on the forecaster only because it's the
    /// natural co-home of the C# `SolarOptimizer` namespace.
    pub fn optimal_rate(panels: &[PanelGeometry]) -> f64 {
        if panels.is_empty() {
            return 0.0;
        }

        let mut best = 0.0;
        for i in 0..SAMPLE_COUNT {
            let d = fibonacci_sphere_direction(i, SAMPLE_COUNT);
            let mut total = 0.0;
            for p in panels {
                let dot = d.dot(p.direction);
                if p.is_tracking {
                    let perp_sq = 1.0 - dot * dot;
                    if perp_sq > 0.0 {
                        total += p.charge_rate * perp_sq.sqrt();
                    }
                } else if dot > 0.0 {
                    total += p.charge_rate * dot;
                }
            }
            if total > best {
                best = total;
            }
        }
        best
    }

    // ── helpers ─────────────────────────────────────────────────────

    /// Vessel position relative to its parent body's centre at `ut`.
    /// Same frame the C# code uses for the cylinder test.
    fn vessel_pos_relative_to_parent(&self, orbit: &Orbit, ut: f64) -> Vec3d {
        let mu = self.ephem.body(orbit.parent).mu;
        orbit.elements.position_at(mu, ut)
    }

    /// Position of the root star, expressed relative to the orbit's
    /// parent body. Equals `−parent_absolute_position` since the root
    /// is at the origin of the absolute frame.
    fn sun_pos_relative_to_parent(&self, orbit: &Orbit, ut: f64) -> Vec3d {
        Vec3d::ZERO - self.ephem.body_position_absolute(orbit.parent, ut)
    }

    fn event(in_shadow_now: bool, dt: f64) -> SolarEvent {
        if in_shadow_now {
            SolarEvent::Shade(dt)
        } else {
            SolarEvent::Sun(dt)
        }
    }
}

/// Fibonacci-spiral point on the unit sphere. Public so tests can verify
/// uniform-coverage / unit-vector properties directly.
pub fn fibonacci_sphere_direction(index: usize, count: usize) -> Vec3d {
    let golden_angle = std::f64::consts::PI * (3.0 - 5.0_f64.sqrt());
    let y = 1.0 - (2.0 * index as f64 + 1.0) / count as f64;
    let radius = (1.0 - y * y).max(0.0).sqrt();
    let theta = golden_angle * index as f64;
    Vec3d::new(theta.cos() * radius, y, theta.sin() * radius)
}
