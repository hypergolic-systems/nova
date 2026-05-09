//! Volumetric tank — multi-resource container with shared volume and
//! shared flow cap. Mirrors `mod/Nova.Core/Components/Propulsion/TankVolume.cs`.
//!
//! One TankVolume may hold multiple resources in a fixed mix
//! (e.g. kerolox = 60% LOx + 40% RP-1 by volume), with the part-level
//! pipe capacity (`max_rate`, L/s shared in/out) proportioned across
//! the constituent buffers by capacity fraction:
//!
//!   buffer.max_rate_in = buffer.max_rate_out
//!     = max_rate × (capacity / volume)

use crate::resource::Resource;
use crate::systems::{BufferId, NodeId, VesselSystems};

#[derive(Debug, Clone)]
pub struct TankSpec {
    pub resource: Resource,
    pub capacity: f64,
    pub initial_contents: f64,
}

#[derive(Debug, Clone)]
pub struct TankVolume {
    /// Total volume in litres — sets the proportioning denominator
    /// for `max_rate` distribution across tanks.
    pub volume: f64,
    /// Shared in/out cap, L/s.
    pub max_rate: f64,
    pub tanks: Vec<TankSpec>,
    /// Populated at `on_build_systems`; one buffer id per tank in
    /// the same order as `tanks`.
    pub(crate) buffer_ids: Vec<BufferId>,
}

impl TankVolume {
    pub fn new(volume: f64, max_rate: f64) -> Self {
        TankVolume {
            volume,
            max_rate,
            tanks: Vec::new(),
            buffer_ids: Vec::new(),
        }
    }

    /// Add a tank that starts full.
    pub fn add_tank(mut self, resource: Resource, capacity: f64) -> Self {
        self.tanks.push(TankSpec {
            resource,
            capacity,
            initial_contents: capacity,
        });
        self
    }

    /// Add a tank with explicit initial contents (≤ capacity).
    pub fn add_tank_with_contents(
        mut self,
        resource: Resource,
        capacity: f64,
        initial_contents: f64,
    ) -> Self {
        self.tanks.push(TankSpec {
            resource,
            capacity,
            initial_contents,
        });
        self
    }

    pub fn buffer_ids(&self) -> &[BufferId] { &self.buffer_ids }

    pub(crate) fn on_build_systems(&mut self, sys: &mut VesselSystems, node: NodeId) {
        self.buffer_ids.clear();
        for spec in &self.tanks {
            let bid = sys.staging.add_buffer(node, spec.resource, spec.capacity);
            let rate = if self.volume > 0.0 {
                self.max_rate * (spec.capacity / self.volume)
            } else {
                0.0
            };
            let buf = sys.staging.buffer_mut(bid);
            buf.flow_limits(rate, rate);
            buf.set_contents(spec.initial_contents);
            self.buffer_ids.push(bid);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::sim_clock::SimClock;
    use approx::assert_relative_eq;

    #[test]
    fn build_systems_registers_buffer_per_tank_with_initial_contents() {
        let mut sys = VesselSystems::new(SimClock::new(0.0));
        let node = sys.staging.add_node(0.0);

        let mut tv = TankVolume::new(100.0, 10.0)
            .add_tank(Resource::Rp1, 40.0)
            .add_tank(Resource::LiquidOxygen, 60.0);
        tv.on_build_systems(&mut sys, node);

        assert_eq!(tv.buffer_ids.len(), 2);
        let b_rp1 = tv.buffer_ids[0];
        let b_lox = tv.buffer_ids[1];

        assert_eq!(sys.staging.buffer(b_rp1).resource, Resource::Rp1);
        assert_relative_eq!(sys.staging.buffer(b_rp1).contents(), 40.0);
        assert_relative_eq!(sys.staging.buffer(b_lox).contents(), 60.0);
        assert_eq!(sys.staging.node(node).buffers.len(), 2);
    }

    #[test]
    fn max_rate_splits_proportional_to_tank_capacity() {
        let mut sys = VesselSystems::new(SimClock::new(0.0));
        let node = sys.staging.add_node(0.0);
        let mut tv = TankVolume::new(100.0, 10.0)
            .add_tank(Resource::Rp1, 40.0)
            .add_tank(Resource::LiquidOxygen, 60.0);
        tv.on_build_systems(&mut sys, node);
        // 10 × 40/100 = 4; 10 × 60/100 = 6.
        assert_relative_eq!(sys.staging.buffer(tv.buffer_ids[0]).max_rate_out, 4.0);
        assert_relative_eq!(sys.staging.buffer(tv.buffer_ids[1]).max_rate_out, 6.0);
        assert_relative_eq!(sys.staging.buffer(tv.buffer_ids[0]).max_rate_in, 4.0);
        assert_relative_eq!(sys.staging.buffer(tv.buffer_ids[1]).max_rate_in, 6.0);
    }

    #[test]
    fn explicit_initial_contents_honored() {
        let mut sys = VesselSystems::new(SimClock::new(0.0));
        let node = sys.staging.add_node(0.0);
        let mut tv = TankVolume::new(100.0, 10.0)
            .add_tank_with_contents(Resource::Hydrazine, 100.0, 25.0);
        tv.on_build_systems(&mut sys, node);
        assert_relative_eq!(sys.staging.buffer(tv.buffer_ids[0]).contents(), 25.0);
    }

    #[test]
    fn zero_volume_yields_zero_flow_caps() {
        let mut sys = VesselSystems::new(SimClock::new(0.0));
        let node = sys.staging.add_node(0.0);
        let mut tv = TankVolume::new(0.0, 10.0)
            .add_tank(Resource::Hydrazine, 100.0);
        tv.on_build_systems(&mut sys, node);
        assert_relative_eq!(sys.staging.buffer(tv.buffer_ids[0]).max_rate_out, 0.0);
    }
}
