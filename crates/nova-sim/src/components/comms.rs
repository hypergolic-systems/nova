//! Comms — a single antenna mounted on a part. Mirrors the
//! data-only role of `mod/Nova.Core/Components/Communications/Antenna.cs`.
//!
//! No per-vessel solver registration: `CommsSystem` is world-level and
//! synthesises vessel endpoints by walking each vessel's parts at
//! solve time. The component holds the antenna spec and nothing else.

use crate::comms::Antenna;
use crate::systems::{NodeId, VesselSystems};

#[derive(Clone, Debug)]
pub struct Comms {
    pub antenna: Antenna,
}

impl Comms {
    pub fn new(antenna: Antenna) -> Self {
        Comms { antenna }
    }

    pub(crate) fn on_build_systems(&mut self, _sys: &mut VesselSystems, _node: NodeId) {
        // No solver registration in v1 — antennas don't consume EC.
    }
}
