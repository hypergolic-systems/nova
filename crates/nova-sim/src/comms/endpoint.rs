//! Endpoint — a node in the comms graph. Aggregates one or more
//! antennas and resolves its world-frame position at any UT through
//! `World`. Mirrors `mod/Nova.Core/Communications/Endpoint.cs`, but
//! data-driven (no closure stored; `position_at` dispatches on
//! `EndpointKind`) so endpoints stay `Clone + Debug` across solver
//! mutations.

use crate::ephem::BodyId;
use crate::math::Vec3d;
use crate::world::{VesselId, World};

use super::antenna::Antenna;
use super::motion::{surface_offset, MotionModel};

/// Stable identity used as cache key in `(from, to)` pairs. Vessels
/// reuse their `VesselId`; ground stations get a monotonic id assigned
/// by `CommsSystem::add_ground_station`. `Ord` is derived (declaration
/// order: `Vessel < Ground`); used only to form a deterministic
/// unordered-pair key for caches.
#[derive(Copy, Clone, Debug, PartialEq, Eq, Hash, PartialOrd, Ord)]
pub enum EndpointId {
    Vessel(VesselId),
    Ground(u32),
}

/// Where this endpoint's position comes from. `Vessel` resolves
/// through `World::vessel_position_absolute`; `Ground` is a fixed
/// surface point on a body, rotating with the body's spin.
#[derive(Clone, Debug)]
pub enum EndpointKind {
    Vessel(VesselId),
    Ground {
        primary: BodyId,
        latitude_deg: f64,
        longitude_deg: f64,
        altitude_m: f64,
    },
}

/// Cached connectivity summary from this endpoint to a designated
/// home. Refreshed once per Solve by
/// `CommsSystem::refresh_home_path_summaries`. Defaults to
/// no-path/zero — `default()` is the "before any solve" state and the
/// "this is the home endpoint" state.
#[derive(Copy, Clone, Debug, Default, PartialEq)]
pub struct PathSummary {
    pub has_path: bool,
    pub bottleneck_bps: f64,
    pub direct_snr: f64,
    pub direct_rate_bps: f64,
    pub direct_max_rate_bps: f64,
}

#[derive(Clone, Debug)]
pub struct Endpoint {
    pub id: EndpointId,
    pub name: String,
    pub kind: EndpointKind,
    /// Optional analytical motion hint. When both endpoints of a
    /// directed link expose a compatible model, the horizon solver
    /// runs in closed form. `None` falls back to numerical bisection
    /// on the `position_at` evaluator.
    pub motion: Option<MotionModel>,
    /// Body whose SOI this endpoint sits in. Drives the link's
    /// occluder set with the other endpoint's `primary_body`. `None`
    /// → empty occluder set (always-unblocked default for fixtures).
    pub primary_body: Option<BodyId>,
    pub antennas: Vec<Antenna>,
    /// True iff the position evaluator is reliable for forecasting.
    /// Off-rails KSP vessels under thrust set this false; the horizon
    /// solver pins their links at the horizon cap and the driver
    /// handles bucket transitions reactively. M6 has no off-rails
    /// surface — always `true`.
    pub is_predictable: bool,
    pub path_to_home: PathSummary,
}

impl Endpoint {
    /// World-frame position at `ut`. Vessels look up their orbital
    /// state via `World`; ground stations rotate with their parent
    /// body's spin.
    pub fn position_at(&self, world: &World, ut: f64) -> Vec3d {
        match &self.kind {
            // `synthesise_vessel_endpoints` skips abstract vessels, so
            // every Vessel endpoint has a positionable counterpart.
            EndpointKind::Vessel(vid) => world
                .vessel_position_absolute(*vid, ut)
                .expect("vessel endpoint synthesised for abstract vessel"),
            EndpointKind::Ground { primary, latitude_deg, longitude_deg, altitude_m } => {
                let body = world.ephemeris.body(*primary);
                let local = surface_offset(
                    body.radius,
                    body.rotation.period_seconds,
                    body.rotation.initial_rotation_rad,
                    *latitude_deg,
                    *longitude_deg,
                    *altitude_m,
                    ut,
                );
                world.ephemeris.body_position_absolute(*primary, ut) + local
            }
        }
    }
}
