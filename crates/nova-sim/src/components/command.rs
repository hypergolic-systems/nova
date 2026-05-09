//! Command — vessel control source. Mirrors
//! `mod/Nova.Core/Components/Control/Command.cs`.
//!
//! Command pods, probe cores, cockpits — anything that's a vessel
//! control source. Carries a continuous baseline EC draw (avionics,
//! flight computer, telemetry, life support overhead) modelled as a
//! single High-priority Process device with demand = 1. Real spacecraft
//! sit in the 1–200 W range depending on class.
//!
//! Phase-1 port: `idle_draw` only. The C# `TestLoadRate` /
//! `TestLoadActive` debug machinery is intentionally deferred — it
//! returns in a later PR alongside Engine throttle, which is the
//! milestone that exercises the FFI input direction.

use crate::resource::Resource;
use crate::systems::{DeviceHandle, NodeId, Priority, VesselSystems};

#[derive(Debug, Clone)]
pub struct Command {
    pub idle_draw: f64,
    pub(crate) idle_device: Option<DeviceHandle>,
}

impl Command {
    pub fn new(idle_draw: f64) -> Self {
        Command {
            idle_draw,
            idle_device: None,
        }
    }

    pub(crate) fn on_build_systems(&mut self, sys: &mut VesselSystems, node: NodeId) {
        if self.idle_draw > 0.0 {
            let dev = sys.add_device(
                node,
                &[(Resource::ElectricCharge, self.idle_draw)],
                &[],
                Priority::High,
            );
            sys.set_device_demand(dev, 1.0);
            self.idle_device = Some(dev);
        }
    }

    pub fn idle_activity(&self, sys: &VesselSystems) -> f64 {
        match self.idle_device {
            Some(h) => sys.device_activity(h),
            None => 0.0,
        }
    }
}
