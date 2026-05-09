//! Per-vessel solver container. Wraps both resource-flow systems
//! (Topological → `StagingFlowSystem` water-fill, Uniform →
//! `ProcessFlowSystem` LP) plus the shared simulation clock. Both
//! systems see the same clock, so per-tick rate distribution and
//! lerp-based buffer state stay coherent.

pub mod process;
pub mod staging;

pub use process::{Device, DeviceId, Priority, ProcessFlowSystem};
pub use staging::{
    BufferId, Consumer, ConsumerId, ConsumerInput, Node, NodeId, StagingFlowSystem,
};

use crate::resource::Resource;
use crate::sim_clock::SimClock;

/// Per-vessel container of solvers + clock. Components hand into
/// either `staging` or `process` directly during `on_build_systems`,
/// based on the resource domain they operate on (Topological vs
/// Uniform). The solvers never share buffers — domains are disjoint
/// at the resource level.
#[derive(Clone, Debug)]
pub struct VesselSystems {
    pub clock: SimClock,
    pub staging: StagingFlowSystem,
    pub process: ProcessFlowSystem,
}

impl VesselSystems {
    pub fn new(clock: SimClock) -> Self {
        VesselSystems {
            staging: StagingFlowSystem::new(clock.clone()),
            process: ProcessFlowSystem::new(clock.clone()),
            clock,
        }
    }

    /// Register a coupled-input device on the staging system. Kept
    /// for the M3 RcsTests-equivalent fixtures; new code should
    /// prefer calling `staging.add_consumer` directly.
    pub fn add_device(
        &mut self,
        node: NodeId,
        inputs: Vec<(Resource, f64)>,
    ) -> ConsumerId {
        self.staging.add_consumer(node, inputs)
    }
}
