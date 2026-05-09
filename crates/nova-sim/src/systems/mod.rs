//! Per-vessel solver container. M3 only ships the topological side
//! (StagingFlowSystem); the LP-driven ProcessFlowSystem and the
//! `AddDevice` routing that picks between them lands in M4+.

pub mod staging;

pub use staging::{
    BufferId, Consumer, ConsumerId, ConsumerInput, Node, NodeId, StagingFlowSystem,
};

use crate::resource::Resource;
use crate::sim_clock::SimClock;

/// Per-vessel container of solvers + clock. M3 wraps just the staging
/// system; later milestones add ProcessFlowSystem + routing.
#[derive(Clone, Debug)]
pub struct VesselSystems {
    pub clock: SimClock,
    pub staging: StagingFlowSystem,
}

impl VesselSystems {
    pub fn new(clock: SimClock) -> Self {
        VesselSystems {
            staging: StagingFlowSystem::new(clock.clone()),
            clock,
        }
    }

    /// Register a coupled-input device on the staging system. M3 has
    /// no Process side, so all devices route to staging; this method
    /// is shaped to accommodate the eventual domain split.
    pub fn add_device(
        &mut self,
        node: NodeId,
        inputs: Vec<(Resource, f64)>,
    ) -> ConsumerId {
        self.staging.add_consumer(node, inputs)
    }
}
