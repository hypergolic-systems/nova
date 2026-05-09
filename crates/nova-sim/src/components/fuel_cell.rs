//! PEM-style fuel cell, buffer-pattern. Mirrors
//! `mod/Nova.Core/Components/Electrical/FuelCell.cs`.
//!
//! Component-internal manifold is an `Accumulator` storing total
//! "mix" volume (LH₂ + LOx combined, in litres). Per-resource
//! production rates (`lh2_rate`, `lox_rate` at full activity) imply a
//! fixed volumetric mix ratio, so one number tracks both reactants
//! in lockstep. See `docs/lp_hygiene.md` for why the µL/s reactant
//! rates stay off the LP.
//!
//! The Accumulator owns the refill side (Staging consumer with two
//! coupled inputs at the configured proportions); FuelCell owns the
//! production side (Process producer of EC). Production hysteresis
//! flips on vessel-wide battery state of charge — 20% on / 80% off.

use crate::components::accumulator::Accumulator;
use crate::resource::Resource;
use crate::systems::{DeviceHandle, NodeId, Priority, VesselSystems};

/// Production hysteresis bands (vessel-wide battery SoC).
const SOC_ON_THRESHOLD: f64 = 0.20;
const SOC_OFF_THRESHOLD: f64 = 0.80;

#[derive(Debug, Clone)]
pub struct FuelCell {
    /// L/s of LH₂ at full production.
    pub lh2_rate: f64,
    /// L/s of LOx at full production.
    pub lox_rate: f64,
    /// W (EC/s) at full production.
    pub ec_output: f64,
    /// mix-L/s drawn from main tanks when refill is active.
    pub refill_rate: f64,

    /// Persisted production-hysteresis flag.
    pub is_active: bool,
    /// Mirror of `manifold.refill_active` — kept on `FuelCell` so old
    /// snapshot/restore paths can round-trip the persisted handle.
    pub refill_active: bool,

    pub manifold: Accumulator,

    /// Seconds until the next predicted production state flip given
    /// current rates. `+∞` when no transition is reachable.
    pub valid_until_seconds: f64,

    /// Set at `on_build_systems` time — handle to the Process-side EC
    /// producer. `None` until the component is wired.
    production: Option<DeviceHandle>,

    /// Set at `on_build_systems` — node the production device is
    /// attached to (Process treats it as harmless metadata).
    node_id: Option<NodeId>,

    /// Last-solve achieved fraction of the production device. Cached
    /// during `on_post_solve` so `current_output()` can read it
    /// without re-querying VesselSystems.
    production_activity: f64,

    /// Absolute UT of next state-change event for this component.
    /// `+∞` when none. Bubbles up via `Component::valid_until`.
    valid_until: f64,
}

impl FuelCell {
    /// Construct a fuel cell. `manifold_capacity` sets the
    /// Accumulator's capacity (mix-L); the manifold starts full.
    pub fn new(
        lh2_rate: f64,
        lox_rate: f64,
        ec_output: f64,
        refill_rate: f64,
        manifold_capacity: f64,
    ) -> Self {
        FuelCell {
            lh2_rate,
            lox_rate,
            ec_output,
            refill_rate,
            is_active: false,
            refill_active: false,
            manifold: Accumulator::new(manifold_capacity),
            valid_until_seconds: f64::INFINITY,
            production: None,
            node_id: None,
            production_activity: 0.0,
            valid_until: f64::INFINITY,
        }
    }

    pub fn with_active(mut self, active: bool) -> Self {
        self.is_active = active;
        self
    }

    pub fn with_refill_active(mut self, active: bool) -> Self {
        self.refill_active = active;
        self
    }

    pub fn with_manifold_contents(mut self, contents: f64) -> Self {
        self.manifold.baseline_contents = contents;
        self
    }

    /// Volumetric proportion of LH₂ in the mix. Derived from per-
    /// resource production rates — the manifold drains in lockstep
    /// with production (since production is the only consumer), so
    /// its mix composition matches the production stoichiometry.
    pub fn lh2_frac(&self) -> f64 {
        self.lh2_rate / (self.lh2_rate + self.lox_rate)
    }

    pub fn lox_frac(&self) -> f64 {
        self.lox_rate / (self.lh2_rate + self.lox_rate)
    }

    /// Combined volumetric production drain at full activity (mix-L/s).
    pub fn production_drain_rate(&self) -> f64 {
        self.lh2_rate + self.lox_rate
    }

    /// Live W actually delivered — the production device's last
    /// achieved activity × rated `ec_output`.
    pub fn current_output(&self) -> f64 {
        self.production_activity * self.ec_output
    }

    pub(crate) fn on_build_systems(&mut self, sys: &mut VesselSystems, node: NodeId) {
        self.node_id = Some(node);

        // Manifold owns the refill side: a coupled-input Staging
        // consumer with LH₂ + LOx at the configured mix proportions.
        // Push persisted RefillActive into runtime state before
        // configuring so the initial demand is set correctly.
        self.manifold.refill_active = self.refill_active;
        let inputs = [
            (Resource::LiquidHydrogen, self.refill_rate * self.lh2_frac()),
            (Resource::LiquidOxygen, self.refill_rate * self.lox_frac()),
        ];
        self.manifold.configure(sys, node, &inputs);

        // Production: vessel-wide EC producer. Activity is gated each
        // tick by `on_pre_solve` (is_active + manifold-non-empty).
        let prod = sys.add_device(
            node,
            &[],
            &[(Resource::ElectricCharge, self.ec_output)],
            Priority::Low,
        );
        sys.set_device_demand(
            prod,
            if self.is_active && !self.manifold.is_empty() { 1.0 } else { 0.0 },
        );
        self.production = Some(prod);
    }

    pub(crate) fn on_pre_solve(&mut self, sys: &mut VesselSystems) {
        let production = match self.production {
            Some(p) => p,
            None => return,
        };

        // Aggregate vessel-wide EC SoC by walking process EC buffers.
        // In this Rust port, EC buffers are owned exclusively by
        // Battery components — same effective set as the C# walk
        // `Vessel.AllComponents().OfType<Battery>()`.
        let mut contents = 0.0;
        let mut capacity = 0.0;
        for &bid in sys.process.buffers_for(Resource::ElectricCharge) {
            let b = sys.process.buffer(bid);
            contents += b.contents();
            capacity += b.capacity;
        }
        let no_batteries = capacity < 1e-9;
        let soc = if no_batteries { 0.0 } else { contents / capacity };

        if no_batteries {
            // Without a SoC signal there's no reason to throttle.
            self.is_active = true;
        } else if self.is_active && soc > SOC_OFF_THRESHOLD {
            self.is_active = false;
        } else if !self.is_active && soc < SOC_ON_THRESHOLD {
            self.is_active = true;
        }

        let demand = if self.is_active && !self.manifold.is_empty() {
            1.0
        } else {
            0.0
        };
        sys.set_device_demand(production, demand);

        self.manifold.on_pre_solve(sys);
        // Hysteresis flip happened inside `manifold.on_pre_solve` —
        // sync the persisted handle so external reads see runtime.
        self.refill_active = self.manifold.refill_active;
    }

    pub(crate) fn on_post_solve(&mut self, sys: &mut VesselSystems) {
        let production = match self.production {
            Some(p) => p,
            None => return,
        };

        // Drain side → manifold tap_rate. Manifold rebases its lerp
        // and refreshes its own valid_until inside `on_post_solve`.
        let activity = sys.device_activity(production);
        self.production_activity = activity;
        self.manifold.set_tap_rate(activity * self.production_drain_rate());
        self.manifold.on_post_solve(sys);

        // Aggregate batteries again for forecast.
        let mut contents = 0.0;
        let mut capacity = 0.0;
        let mut battery_rate = 0.0;
        for &bid in sys.process.buffers_for(Resource::ElectricCharge) {
            let b = sys.process.buffer(bid);
            contents += b.contents();
            capacity += b.capacity;
            battery_rate += b.rate(); // signed: + charging, − draining
        }
        let no_batteries = capacity < 1e-9;

        // SoC threshold flip.
        let mut dt_soc_flip = f64::INFINITY;
        if !no_batteries && battery_rate.abs() > 1e-9 {
            if self.is_active && battery_rate > 0.0 {
                let remaining = SOC_OFF_THRESHOLD * capacity - contents;
                if remaining > 0.0 {
                    dt_soc_flip = remaining / battery_rate;
                }
            } else if !self.is_active && battery_rate < 0.0 {
                let remaining = contents - SOC_ON_THRESHOLD * capacity;
                if remaining > 0.0 {
                    dt_soc_flip = remaining / -battery_rate;
                }
            }
        }

        // Manifold-empty time. Refill is much faster than production
        // reactant draw (~200×) so this only matters when the main
        // tank is empty and refill is forced to 0.
        let manifold_rate = self.manifold.rate();
        let mut dt_mfd_empty = f64::INFINITY;
        if activity > 1e-9 && manifold_rate < -1e-12 && self.manifold.contents() > 0.0 {
            dt_mfd_empty = self.manifold.contents() / -manifold_rate;
        }

        let dt_prod_flip = dt_soc_flip.min(dt_mfd_empty);
        self.valid_until_seconds = dt_prod_flip;
        let now = sys.clock.ut();
        let production_valid_until = if dt_prod_flip.is_infinite() {
            f64::INFINITY
        } else {
            now + dt_prod_flip
        };
        // Mirror to the production device so `ProcessFlowSystem::max_tick_dt`
        // folds the forecast into its dt clamp. Staging-bound devices
        // would no-op here (matches C# `Device.ValidUntil`).
        sys.set_device_valid_until(production, production_valid_until);

        // Bubble up to `valid_until` — soonest of production-flip and
        // manifold-refill-flip (the latter owned by the Accumulator).
        let earliest = production_valid_until.min(self.manifold.valid_until());
        self.valid_until = earliest;
    }

    pub(crate) fn valid_until(&self) -> f64 {
        self.valid_until
    }
}
