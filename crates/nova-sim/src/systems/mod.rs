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

use crate::resource::{Resource, ResourceDomain};
use crate::sim_clock::SimClock;

/// Unified handle to either a Topological-domain (staging) consumer
/// or a Uniform-domain (process) device. Picked at
/// `VesselSystems::add_device` time based on the inputs/outputs'
/// resource domain. Mirrors the C# `Device` wrapper class — one API
/// surface for components regardless of which solver owns the device.
#[derive(Copy, Clone, Debug, PartialEq, Eq, Hash)]
pub enum DeviceHandle {
    Staging(ConsumerId),
    Process(DeviceId),
}

impl DeviceHandle {
    pub fn domain(self) -> ResourceDomain {
        match self {
            DeviceHandle::Staging(_) => ResourceDomain::Topological,
            DeviceHandle::Process(_) => ResourceDomain::Uniform,
        }
    }
}

/// Per-vessel container of solvers + clock. Components hand into
/// either `staging` or `process` directly during `on_build_systems`,
/// based on the resource domain they operate on (Topological vs
/// Uniform). The solvers never share buffers — domains are disjoint
/// at the resource level.
///
/// `needs_solve` drives event-driven re-solve scheduling. Set true on
/// initial-build and on any state change that invalidates per-buffer
/// rates (forecasted event firing, external mutation of throttle /
/// demand / contents). Cleared after each `Vessel::do_solve_with`. The
/// tick driver skips redundant solves when this flag is false — the
/// whole point of the design is that rates stay valid over long time
/// horizons and only get recomputed on change.
#[derive(Clone, Debug)]
pub struct VesselSystems {
    pub clock: SimClock,
    pub staging: StagingFlowSystem,
    pub process: ProcessFlowSystem,
    needs_solve: bool,
    solve_count: u64,
}

impl VesselSystems {
    pub fn new(clock: SimClock) -> Self {
        VesselSystems {
            staging: StagingFlowSystem::new(clock.clone()),
            process: ProcessFlowSystem::new(clock.clone()),
            clock,
            // Initial-state vessels haven't been solved yet; the first
            // tick must run pre/solve/post once to populate rates.
            needs_solve: true,
            solve_count: 0,
        }
    }

    /// Mark the per-vessel solvers dirty. The next `Vessel::tick`
    /// iter will run pre/solve/post; subsequent iters skip solve
    /// until something else invalidates again. Callers must invoke
    /// this after any external mutation that changes rates —
    /// `engine.throttle = ...`, `consumer_mut(id).demand = ...`, etc.
    /// Forecasted events (buffer empty / fill, component
    /// `valid_until` flips) auto-invalidate at clock-cross time.
    pub fn invalidate(&mut self) {
        self.needs_solve = true;
    }

    pub fn needs_solve(&self) -> bool {
        self.needs_solve
    }

    /// Diagnostic counter — number of times the per-vessel pre/solve/
    /// post pipeline has run since `new()`. Reset on
    /// `Vessel::initialize_solver`. Tests use this to verify the
    /// "skip redundant solves" path actually skips.
    pub fn solve_count(&self) -> u64 {
        self.solve_count
    }

    /// Called by `Vessel::do_solve_with` after a successful solve.
    pub(crate) fn note_solved(&mut self) {
        self.needs_solve = false;
        self.solve_count += 1;
    }

    /// Register a unified device with declared inputs and outputs.
    /// Validates that all endpoints share a single resource domain
    /// (a Device's Activity is managed by exactly one solver) and
    /// dispatches to staging or process accordingly. Mirrors C#
    /// `VesselSystems.AddDevice` (see `mod/Nova.Core/Systems/VesselSystems.cs:80-121`).
    ///
    /// Topological devices may not declare outputs — only tanks store
    /// topological resources.
    ///
    /// `priority` only matters on the Process side; staging-bound
    /// devices ignore it.
    pub fn add_device(
        &mut self,
        node: NodeId,
        inputs: &[(Resource, f64)],
        outputs: &[(Resource, f64)],
        priority: Priority,
    ) -> DeviceHandle {
        if inputs.is_empty() && outputs.is_empty() {
            panic!("Device must declare at least one input or output");
        }

        let mut domain: Option<ResourceDomain> = None;
        for (r, _) in inputs.iter().chain(outputs.iter()) {
            domain = Some(merge_domain(domain, *r));
        }
        let domain = domain.unwrap();

        match domain {
            ResourceDomain::Topological => {
                if !outputs.is_empty() {
                    panic!(
                        "Topological devices cannot declare outputs — only \
                         tanks store topological resources"
                    );
                }
                let cid = self.staging.add_consumer(node, inputs.to_vec());
                DeviceHandle::Staging(cid)
            }
            ResourceDomain::Uniform => {
                let did = self.process.add_device(priority);
                let dev = self.process.device_mut(did);
                for &(r, rate) in inputs {
                    dev.add_input(r, rate);
                }
                for &(r, rate) in outputs {
                    dev.add_output(r, rate);
                }
                DeviceHandle::Process(did)
            }
        }
    }

    /// 0..1, what fraction of the declared full rate this device wants
    /// this tick. Set pre-solve.
    pub fn device_demand(&self, h: DeviceHandle) -> f64 {
        match h {
            DeviceHandle::Staging(cid) => self.staging.consumer(cid).demand,
            DeviceHandle::Process(did) => self.process.device(did).demand,
        }
    }

    pub fn set_device_demand(&mut self, h: DeviceHandle, demand: f64) {
        match h {
            DeviceHandle::Staging(cid) => self.staging.consumer_mut(cid).demand = demand,
            DeviceHandle::Process(did) => self.process.device_mut(did).demand = demand,
        }
    }

    /// 0..1, achieved fraction post-solve.
    pub fn device_activity(&self, h: DeviceHandle) -> f64 {
        match h {
            DeviceHandle::Staging(cid) => self.staging.consumer(cid).activity,
            DeviceHandle::Process(did) => self.process.device(did).activity,
        }
    }

    /// Set the next forecasted state-change UT on a Process device.
    /// Staging-bound devices don't carry per-device forecasts so the
    /// setter is a no-op there (matches the C# `Device.ValidUntil`
    /// semantics).
    pub fn set_device_valid_until(&mut self, h: DeviceHandle, valid_until: f64) {
        if let DeviceHandle::Process(did) = h {
            self.process.device_mut(did).valid_until = valid_until;
        }
    }
}

fn merge_domain(acc: Option<ResourceDomain>, r: Resource) -> ResourceDomain {
    let d = r.domain();
    match acc {
        None => d,
        Some(prev) if prev == d => d,
        Some(prev) => panic!(
            "Device cannot mix resource domains: {:?} is {:?}, prior endpoints were {:?}",
            r, d, prev
        ),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn fresh() -> VesselSystems {
        VesselSystems::new(SimClock::new(0.0))
    }

    #[test]
    fn add_device_topological_routes_to_staging() {
        let mut sys = fresh();
        let n = sys.staging.add_node(0.0);
        let h = sys.add_device(
            n,
            &[(Resource::Hydrazine, 1.0)],
            &[],
            Priority::Low,
        );
        assert!(matches!(h, DeviceHandle::Staging(_)));
        assert_eq!(h.domain(), ResourceDomain::Topological);
    }

    #[test]
    fn add_device_uniform_routes_to_process() {
        let mut sys = fresh();
        let n = sys.staging.add_node(0.0);
        let h = sys.add_device(
            n,
            &[(Resource::ElectricCharge, 100.0)],
            &[],
            Priority::Low,
        );
        assert!(matches!(h, DeviceHandle::Process(_)));
        assert_eq!(h.domain(), ResourceDomain::Uniform);
    }

    #[test]
    fn add_device_uniform_with_outputs_routes_to_process() {
        let mut sys = fresh();
        let n = sys.staging.add_node(0.0);
        let h = sys.add_device(
            n,
            &[],
            &[(Resource::ElectricCharge, 1000.0)],
            Priority::Low,
        );
        assert!(matches!(h, DeviceHandle::Process(_)));
    }

    #[test]
    fn set_and_read_device_demand_dispatches_on_handle() {
        let mut sys = fresh();
        let n = sys.staging.add_node(0.0);
        let staging_h = sys.add_device(
            n,
            &[(Resource::Hydrazine, 1.0)],
            &[],
            Priority::Low,
        );
        let process_h = sys.add_device(
            n,
            &[(Resource::ElectricCharge, 50.0)],
            &[],
            Priority::Low,
        );

        sys.set_device_demand(staging_h, 0.7);
        sys.set_device_demand(process_h, 0.3);
        assert_eq!(sys.device_demand(staging_h), 0.7);
        assert_eq!(sys.device_demand(process_h), 0.3);
    }

    #[test]
    #[should_panic(expected = "mix resource domains")]
    fn add_device_rejects_mixed_domains() {
        let mut sys = fresh();
        let n = sys.staging.add_node(0.0);
        sys.add_device(
            n,
            &[(Resource::Hydrazine, 1.0), (Resource::ElectricCharge, 1.0)],
            &[],
            Priority::Low,
        );
    }

    #[test]
    #[should_panic(expected = "Topological devices cannot declare outputs")]
    fn add_device_rejects_topological_outputs() {
        let mut sys = fresh();
        let n = sys.staging.add_node(0.0);
        sys.add_device(
            n,
            &[(Resource::Hydrazine, 1.0)],
            &[(Resource::Hydrazine, 1.0)],
            Priority::Low,
        );
    }

    #[test]
    #[should_panic(expected = "at least one input or output")]
    fn add_device_rejects_empty_endpoints() {
        let mut sys = fresh();
        let n = sys.staging.add_node(0.0);
        sys.add_device(n, &[], &[], Priority::Low);
    }

    #[test]
    fn set_device_valid_until_only_affects_process() {
        let mut sys = fresh();
        let n = sys.staging.add_node(0.0);
        let staging_h = sys.add_device(
            n,
            &[(Resource::Hydrazine, 1.0)],
            &[],
            Priority::Low,
        );
        let process_h = sys.add_device(
            n,
            &[(Resource::ElectricCharge, 50.0)],
            &[],
            Priority::Low,
        );

        // No-op on staging; visible on process.
        sys.set_device_valid_until(staging_h, 42.0);
        sys.set_device_valid_until(process_h, 99.0);
        if let DeviceHandle::Process(did) = process_h {
            assert_eq!(sys.process.device(did).valid_until, 99.0);
        } else {
            unreachable!();
        }
    }
}
