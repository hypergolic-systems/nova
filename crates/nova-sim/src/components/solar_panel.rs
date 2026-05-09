//! SolarPanel — telemetry-only EC source. Mirrors
//! `mod/Nova.Core/Components/Electrical/SolarPanel.cs`.
//!
//! The LP entry is a single aggregate Process device on the Vessel
//! (created in `Vessel::initialize_solver`), summed across every panel.
//! This component contributes its `charge_rate` to that sum and carries
//! per-panel telemetry (deploy/sunlit/effective/current rates) — it
//! does not register its own LP device.
//!
//! TODO: deploy-state mutation API (`set_deployed`) needs to invalidate
//! the vessel's `cached_optimal_rate`. Deferred until any caller
//! actually toggles deploy at runtime; current tests construct in the
//! desired state at `initialize_solver` time.
//!
//! TODO: persistence (proto round-trip) — out of scope until the
//! persistence milestone.

use crate::math::Vec3d;
use crate::systems::{NodeId, VesselSystems};

#[derive(Debug, Clone)]
pub struct SolarPanel {
    /// Rated EC/s when the sun is normal-incident on a fixed panel, or
    /// perpendicular to the rotation axis on a tracking panel.
    pub charge_rate: f64,
    /// Surface normal (fixed panels) or rotation axis (tracking).
    pub panel_direction: Vec3d,
    pub is_tracking: bool,
    pub is_deployed: bool,
    /// True for panels that can be retracted after deployment. Fixed
    /// (non-deployable) panels and one-shot deployables both leave
    /// this false.
    pub is_retractable: bool,
    /// Pro-rata share of the vessel-aggregate optimal rate, in EC/s.
    /// Set by `Vessel::initialize_solver` (and on deploy-state change,
    /// once that API lands). This is the *max* rate the panel could
    /// deliver in its current orientation.
    pub effective_rate: f64,
    /// Pro-rata share of the LP-solved aggregate output, in EC/s. Set
    /// post-solve. Equals `effective_rate` when consumers want full
    /// output; drops to 0 when batteries are full and nothing's drawing.
    pub current_rate: f64,
    pub is_sunlit: bool,
    /// Absolute UT of the next forecasted sun/shade transition.
    /// `+∞` when the vessel orbits the root star (no occluder) or no
    /// transition is reachable in the search horizon.
    pub shadow_transition_ut: f64,
}

impl SolarPanel {
    pub fn new(charge_rate: f64, panel_direction: Vec3d) -> Self {
        SolarPanel {
            charge_rate,
            panel_direction,
            is_tracking: false,
            is_deployed: true,
            is_retractable: false,
            effective_rate: 0.0,
            current_rate: 0.0,
            is_sunlit: true,
            shadow_transition_ut: f64::INFINITY,
        }
    }

    pub fn tracking(mut self) -> Self {
        self.is_tracking = true;
        self
    }

    pub fn retractable(mut self) -> Self {
        self.is_retractable = true;
        self
    }

    pub fn with_deployed(mut self, deployed: bool) -> Self {
        self.is_deployed = deployed;
        self
    }

    /// No solver registration — the aggregate Device is owned by the
    /// vessel. Lifecycle hook is here to match the dispatch pattern.
    pub(crate) fn on_build_systems(&mut self, _sys: &mut VesselSystems, _node: NodeId) {}
}
