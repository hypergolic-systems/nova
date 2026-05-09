//! Virtual components — the per-part simulation logic. Closed enum
//! (vs C#'s `VirtualComponent` abstract class) since Rust has no
//! Unity/KSP `Component` namespace conflict to dodge.

pub mod battery;
pub mod engine;
pub mod tank;

pub use battery::Battery;
pub use engine::{Engine, Propellant};
pub use tank::{TankSpec, TankVolume};

use crate::systems::{NodeId, VesselSystems};

/// A simulated component on a part. Each variant owns its own
/// runtime state and lifecycle. Add a new component port = one new
/// variant + a match arm in each dispatch method below.
#[derive(Debug, Clone)]
pub enum Component {
    Engine(Engine),
    TankVolume(TankVolume),
    Battery(Battery),
}

impl Component {
    /// Called once when the vessel's solver is initialised. Each
    /// component registers buffers, consumers, and any other
    /// solver-side state it needs.
    pub fn on_build_systems(&mut self, sys: &mut VesselSystems, node: NodeId) {
        match self {
            Component::Engine(e) => e.on_build_systems(sys, node),
            Component::TankVolume(t) => t.on_build_systems(sys, node),
            Component::Battery(b) => b.on_build_systems(sys, node),
        }
    }

    /// Called once at the start of each `Vessel::tick(...)` call —
    /// before any solve. Components reset per-tick state here.
    /// (M4-M5 components don't have any.)
    pub fn on_tick_begin(&mut self) {
        match self {
            Component::Engine(_) | Component::TankVolume(_) | Component::Battery(_) => {}
        }
    }

    /// Called before each solve to push the current tick's inputs
    /// into the solver (engine throttle → consumer demand, etc).
    pub fn on_pre_solve(&mut self, sys: &mut VesselSystems) {
        match self {
            Component::Engine(e) => e.on_pre_solve(sys),
            Component::TankVolume(_) | Component::Battery(_) => {}
        }
    }

    /// Called after each solve. Components recompute internal
    /// post-solve state (e.g. Accumulator hysteresis flips). M4-M5
    /// components are pure read-out and don't need this hook yet.
    pub fn on_post_solve(&mut self, _sys: &VesselSystems) {
        match self {
            Component::Engine(_) | Component::TankVolume(_) | Component::Battery(_) => {}
        }
    }

    /// Component-side forecast: UT at which this component expects
    /// to need a re-solve from its own state (e.g. Accumulator
    /// hysteresis flip). +∞ when no scheduled event. The tick driver
    /// merges this with system-side `max_tick_dt` to bound how far
    /// time can advance before the next solve.
    pub fn valid_until(&self) -> f64 {
        match self {
            Component::Engine(_) | Component::TankVolume(_) | Component::Battery(_) => {
                f64::INFINITY
            }
        }
    }
}

/// One part on a vessel. Owns its dry mass and its components; the
/// parent link participates in the staging-system topology graph.
#[derive(Debug, Clone)]
pub struct Part {
    pub id: u32,
    pub name: String,
    pub dry_mass_kg: f64,
    pub parent: Option<u32>,
    pub components: Vec<Component>,
    /// Set at `Vessel::initialize_solver` time — the staging-system
    /// node that backs this part.
    pub(crate) node_id: Option<NodeId>,
}

impl Part {
    pub fn new(id: u32, name: impl Into<String>, dry_mass_kg: f64) -> Self {
        Part {
            id,
            name: name.into(),
            dry_mass_kg,
            parent: None,
            components: Vec::new(),
            node_id: None,
        }
    }

    pub fn with_components(mut self, components: Vec<Component>) -> Self {
        self.components = components;
        self
    }
}
