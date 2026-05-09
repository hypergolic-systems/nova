//! Battery — single-cell EC storage. Mirrors
//! `mod/Nova.Core/Components/Electrical/Battery.cs`.
//!
//! Trivial wrapper: at `on_build_systems` it constructs a Buffer for
//! ElectricCharge and hands it to `ProcessFlowSystem`. Reads of
//! current contents go through the system: `sys.process.buffer(bid)`.
//!
//! The battery itself doesn't drive demand or hooks — the Process LP
//! takes care of distributing supply / fill across all batteries on
//! the vessel proportionally to contents / remaining capacity.

use crate::buffer::Buffer;
use crate::resource::Resource;
use crate::systems::{BufferId, NodeId, VesselSystems};

#[derive(Debug, Clone)]
pub struct Battery {
    pub capacity: f64,
    pub initial_contents: f64,
    pub max_rate_in: f64,
    pub max_rate_out: f64,
    /// Set at `on_build_systems`; the Process system buffer id.
    pub(crate) buffer_id: Option<BufferId>,
}

impl Battery {
    /// Create a battery starting at full capacity. Default flow caps
    /// of 10 EC/s in/out match the C# `Battery.cs:13-14` defaults; use
    /// `with_flow_limits` to set realistic C-rate caps.
    pub fn new(capacity: f64) -> Self {
        Battery {
            capacity,
            initial_contents: capacity,
            max_rate_in: 10.0,
            max_rate_out: 10.0,
            buffer_id: None,
        }
    }

    pub fn with_contents(mut self, contents: f64) -> Self {
        self.initial_contents = contents;
        self
    }

    pub fn with_flow_limits(mut self, max_rate_in: f64, max_rate_out: f64) -> Self {
        self.max_rate_in = max_rate_in;
        self.max_rate_out = max_rate_out;
        self
    }

    pub fn buffer_id(&self) -> Option<BufferId> {
        self.buffer_id
    }

    pub(crate) fn on_build_systems(&mut self, sys: &mut VesselSystems, _node: NodeId) {
        let mut buffer = Buffer::new(Resource::ElectricCharge, self.capacity, None);
        buffer.set_contents(self.initial_contents);
        buffer.flow_limits(self.max_rate_in, self.max_rate_out);
        let bid = sys.process.add_buffer(buffer);
        self.buffer_id = Some(bid);
    }
}
