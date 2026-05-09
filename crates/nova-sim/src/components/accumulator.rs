//! Off-LP storage cell with lerp-based state and an owned refill
//! device. Mirrors `mod/Nova.Core/Components/Accumulator.cs`.
//!
//! Used as a sub-component of FuelCell (LH₂ + LOx mix manifold,
//! Staging-refilled) and ReactionWheel (energy reserve,
//! Process-refilled). The Accumulator owns:
//!   - lerp state (`Contents(t) = clamp(baseline + Rate × (t - baseline_ut), 0, capacity)`)
//!   - a single unified refill `DeviceHandle` (Staging or Process,
//!     auto-routed by `VesselSystems::add_device` from input domain)
//!   - hysteresis on/off control of the refill device
//!   - `valid_until` forecasting of the next hysteresis flip
//!
//! Owners stop carrying boilerplate around device construction,
//! hysteresis flips, or forecast math.

use crate::resource::Resource;
use crate::sim_clock::SimClock;
use crate::systems::{DeviceHandle, NodeId, Priority, VesselSystems};

/// Default thresholds match `Accumulator.cs:62-63`. Refill flips ON
/// when fill fraction ≤ 10%, OFF when ≥ 100%.
pub const DEFAULT_REFILL_ON_FRACTION: f64 = 0.10;
pub const DEFAULT_REFILL_OFF_FRACTION: f64 = 1.00;

#[derive(Clone, Debug)]
pub struct Accumulator {
    pub capacity: f64,

    /// Baseline state. Direct field access for owners (snapshot/restore
    /// code paths). Most callers should go through the property surface
    /// (`contents()`, `set_contents`, `set_tap_rate`).
    pub baseline_contents: f64,
    pub baseline_ut: f64,

    /// Shared clock — installed by `configure()`. Tests can leave this
    /// `None`; `contents_at` then collapses to a static-value lookup at
    /// `baseline_ut`.
    clock: Option<SimClock>,

    // ── Refill side ──────────────────────────────────────────────
    /// Set at `configure()` time. None → no refill device wired.
    refill_device: Option<DeviceHandle>,
    /// Capacity-units per second when refill activity = 1.
    pub refill_rate: f64,
    /// Last-solve achieved fraction (0..1). Updated in `on_post_solve`.
    pub refill_activity: f64,

    // ── Hysteresis ───────────────────────────────────────────────
    pub refill_on_fraction: f64,
    pub refill_off_fraction: f64,
    pub refill_active: bool,

    /// Absolute UT of the next forecasted hysteresis flip. `+∞` when
    /// no flip is reachable from the current state.
    valid_until: f64,

    // ── Drain side ───────────────────────────────────────────────
    /// Continuous drain rate (capacity-units/sec). Use `set_tap_rate`
    /// to update — it rebaselines the lerp.
    tap_rate: f64,

    // ── Lerp state ───────────────────────────────────────────────
    /// Net signed rate (+ = filling). Updated on refill_activity or
    /// tap_rate change.
    rate: f64,
}

impl Accumulator {
    /// Construct an Accumulator with the given capacity, full at
    /// baseline UT 0. Caller can set `baseline_contents`, hysteresis
    /// thresholds, and `refill_active` directly before calling
    /// `configure()`.
    pub fn new(capacity: f64) -> Self {
        Accumulator {
            capacity,
            baseline_contents: capacity,
            baseline_ut: 0.0,
            clock: None,
            refill_device: None,
            refill_rate: 0.0,
            refill_activity: 0.0,
            refill_on_fraction: DEFAULT_REFILL_ON_FRACTION,
            refill_off_fraction: DEFAULT_REFILL_OFF_FRACTION,
            refill_active: false,
            valid_until: f64::INFINITY,
            tap_rate: 0.0,
            rate: 0.0,
        }
    }

    pub fn with_contents(mut self, contents: f64) -> Self {
        self.baseline_contents = contents;
        self
    }

    pub fn refill_device(&self) -> Option<DeviceHandle> {
        self.refill_device
    }

    /// Net signed rate (+ filling, − draining). Updated whenever
    /// `refill_activity` or `tap_rate` changes.
    pub fn rate(&self) -> f64 {
        self.rate
    }

    /// Continuous drain rate (capacity-units/sec). Use `set_tap_rate`
    /// to update.
    pub fn tap_rate(&self) -> f64 {
        self.tap_rate
    }

    /// Setter rebases the lerp at "now" and recomputes net `rate`.
    /// Discrete-tap semantics are recoverable: piecewise-constant
    /// updates over a known dt are equivalent to `tap(rate × dt)`.
    pub fn set_tap_rate(&mut self, value: f64) {
        self.rebaseline_now();
        self.tap_rate = value;
        self.recompute_rate();
        self.refresh_valid_until();
    }

    /// Absolute UT of the next forecasted hysteresis flip. `+∞` if
    /// no flip is reachable.
    pub fn valid_until(&self) -> f64 {
        self.valid_until
    }

    /// Current contents, lerped to the shared clock's UT and clamped
    /// to `[0, capacity]`.
    pub fn contents(&self) -> f64 {
        self.contents_at(self.now())
    }

    /// Set contents at "now", rebaselining the lerp.
    pub fn set_contents(&mut self, contents: f64) {
        self.baseline_contents = contents;
        self.baseline_ut = self.now();
    }

    pub fn contents_at(&self, ut: f64) -> f64 {
        let projected = self.baseline_contents + self.rate * (ut - self.baseline_ut);
        if projected < 0.0 {
            return 0.0;
        }
        if projected > self.capacity {
            return self.capacity;
        }
        projected
    }

    /// Snap the lerp baseline to `ut`. Useful for fixtures that need
    /// to reset the integration anchor without mutating clock state.
    pub fn refresh(&mut self, ut: f64) {
        self.baseline_contents = self.contents_at(ut);
        self.baseline_ut = ut;
    }

    pub fn fill_fraction(&self) -> f64 {
        if self.capacity > 1e-9 {
            self.contents() / self.capacity
        } else {
            1.0
        }
    }

    pub fn is_empty(&self) -> bool {
        self.contents() <= 1e-9
    }

    pub fn is_full(&self) -> bool {
        self.contents() >= self.capacity - 1e-9
    }

    /// Time for contents to reach `target_frac × capacity` given the
    /// current signed rate. `+∞` when the rate isn't moving toward the
    /// target. When contents are AT the target and rate keeps pushing
    /// past, returns 0 — the clamp absorbs the over-fill silently, so
    /// the 0 forecast forces an immediate re-solve so `on_pre_solve`
    /// can flip the hysteresis flag.
    pub fn time_to_fraction(&self, target_frac: f64, net_rate: f64) -> f64 {
        if self.capacity <= 0.0 {
            return f64::INFINITY;
        }
        let target = target_frac * self.capacity;
        let slack = target - self.contents();
        if slack > 0.0 && net_rate > 1e-12 {
            return slack / net_rate;
        }
        if slack < 0.0 && net_rate < -1e-12 {
            return slack / net_rate;
        }
        if slack.abs() < 1e-12 && net_rate.abs() > 1e-12 {
            return 0.0;
        }
        f64::INFINITY
    }

    // ── Configure ────────────────────────────────────────────────
    /// Wire the refill side. The inputs' resource domain picks
    /// Staging (Topological — coupled multi-input water-fill) or
    /// Process (Uniform — LP) automatically via
    /// `VesselSystems::add_device`. `refill_rate` = sum of per-input
    /// rates. Installs the clock and rebaselines `baseline_ut` to
    /// "now" so the lerp anchors against the live clock from here on.
    pub fn configure(
        &mut self,
        sys: &mut VesselSystems,
        node: NodeId,
        inputs: &[(Resource, f64)],
    ) {
        let handle = sys.add_device(node, inputs, &[], Priority::Low);
        let total: f64 = inputs.iter().map(|(_, r)| *r).sum();
        self.refill_device = Some(handle);
        self.refill_rate = total;
        sys.set_device_demand(handle, if self.refill_active { 1.0 } else { 0.0 });
        self.install_clock(sys.clock.clone());
    }

    // ── Lifecycle ────────────────────────────────────────────────
    /// Pre-solve: hysteresis flip on `fill_fraction`, push the
    /// resulting active state into the refill device's demand.
    pub fn on_pre_solve(&mut self, sys: &mut VesselSystems) {
        let frac = self.fill_fraction();
        if self.refill_active && frac >= self.refill_off_fraction {
            self.refill_active = false;
        } else if !self.refill_active && frac <= self.refill_on_fraction {
            self.refill_active = true;
        }
        if let Some(h) = self.refill_device {
            sys.set_device_demand(h, if self.refill_active { 1.0 } else { 0.0 });
        }
    }

    /// Post-solve: capture the refill device's achieved activity,
    /// recompute net rate (rebaselining at "now"), and forecast the
    /// next hysteresis flip.
    pub fn on_post_solve(&mut self, sys: &VesselSystems) {
        self.refill_activity = self
            .refill_device
            .map(|h| sys.device_activity(h))
            .unwrap_or(0.0);
        self.rebaseline_now();
        self.recompute_rate();
        self.refresh_valid_until();
    }

    // ── Internals ────────────────────────────────────────────────
    fn install_clock(&mut self, clock: SimClock) {
        self.baseline_ut = clock.ut();
        self.clock = Some(clock);
    }

    fn now(&self) -> f64 {
        self.clock.as_ref().map(|c| c.ut()).unwrap_or(self.baseline_ut)
    }

    fn rebaseline_now(&mut self) {
        let t = self.now();
        self.baseline_contents = self.contents_at(t);
        self.baseline_ut = t;
    }

    fn recompute_rate(&mut self) {
        self.rate = self.refill_activity * self.refill_rate - self.tap_rate;
    }

    fn refresh_valid_until(&mut self) {
        let dt = if self.refill_active {
            self.time_to_fraction(self.refill_off_fraction, self.rate)
        } else {
            self.time_to_fraction(self.refill_on_fraction, self.rate)
        };
        self.valid_until = if dt.is_infinite() {
            f64::INFINITY
        } else {
            self.now() + dt
        };
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use approx::assert_relative_eq;

    fn fresh_sys() -> VesselSystems {
        VesselSystems::new(SimClock::new(0.0))
    }

    #[test]
    fn contents_lerps_with_rate_over_clock_advance() {
        let clock = SimClock::new(0.0);
        let mut acc = Accumulator::new(100.0).with_contents(50.0);
        acc.install_clock(clock.clone());
        // Manually set a draining rate and baseline.
        acc.tap_rate = 1.0;
        acc.recompute_rate();
        // Rate = -1.0 (draining). After 10 s, contents = 40.
        clock.advance(10.0);
        assert_relative_eq!(acc.contents(), 40.0);
    }

    #[test]
    fn contents_clamps_to_zero_on_over_drain() {
        let clock = SimClock::new(0.0);
        let mut acc = Accumulator::new(100.0).with_contents(5.0);
        acc.install_clock(clock.clone());
        acc.tap_rate = 1.0;
        acc.recompute_rate();
        clock.advance(100.0); // would project to -95
        assert_relative_eq!(acc.contents(), 0.0);
    }

    #[test]
    fn contents_clamps_to_capacity_on_over_fill() {
        let clock = SimClock::new(0.0);
        let mut acc = Accumulator::new(100.0).with_contents(95.0);
        acc.install_clock(clock.clone());
        acc.refill_rate = 1.0;
        acc.refill_activity = 1.0;
        acc.recompute_rate();
        clock.advance(100.0); // would project to 195
        assert_relative_eq!(acc.contents(), 100.0);
    }

    #[test]
    fn set_tap_rate_rebases_lerp_at_now() {
        let clock = SimClock::new(0.0);
        let mut acc = Accumulator::new(100.0).with_contents(50.0);
        acc.install_clock(clock.clone());
        acc.tap_rate = 1.0;
        acc.recompute_rate();
        clock.advance(10.0);
        // contents = 40 at t=10. Now bump tap_rate to 4 (drains 4× faster).
        acc.set_tap_rate(4.0);
        // Baseline reset to (40, 10).
        assert_relative_eq!(acc.baseline_contents, 40.0);
        assert_relative_eq!(acc.baseline_ut, 10.0);
        clock.advance(5.0); // 5 more seconds at -4/s → 20
        assert_relative_eq!(acc.contents(), 20.0);
    }

    #[test]
    fn fill_fraction_handles_zero_capacity() {
        let mut acc = Accumulator::new(0.0);
        acc.baseline_contents = 0.0;
        // Degenerate: zero capacity → fraction reads "full" so
        // hysteresis treats it as "stop refilling".
        assert_relative_eq!(acc.fill_fraction(), 1.0);
    }

    #[test]
    fn time_to_fraction_filling_returns_dt_to_threshold() {
        let acc = Accumulator::new(100.0).with_contents(20.0);
        // rate = +1/s; to reach 80% (= 80 contents) needs 60 s.
        assert_relative_eq!(acc.time_to_fraction(0.8, 1.0), 60.0);
    }

    #[test]
    fn time_to_fraction_draining_returns_dt_to_threshold() {
        let acc = Accumulator::new(100.0).with_contents(80.0);
        // rate = -2/s; to reach 20% (= 20) needs (20-80)/(-2) = 30 s.
        assert_relative_eq!(acc.time_to_fraction(0.2, -2.0), 30.0);
    }

    #[test]
    fn time_to_fraction_wrong_direction_is_infinity() {
        let acc = Accumulator::new(100.0).with_contents(50.0);
        // Filling but threshold is below current → never reachable.
        assert!(acc.time_to_fraction(0.2, 1.0).is_infinite());
        // Draining but threshold is above current → never reachable.
        assert!(acc.time_to_fraction(0.8, -1.0).is_infinite());
    }

    #[test]
    fn time_to_fraction_at_target_with_overshoot_returns_zero() {
        let acc = Accumulator::new(100.0).with_contents(80.0);
        // Already at target, rate keeps pushing past → 0 (force re-solve).
        assert_relative_eq!(acc.time_to_fraction(0.8, 1.0), 0.0);
    }

    #[test]
    fn configure_routes_topological_inputs_to_staging() {
        let mut sys = fresh_sys();
        let n = sys.staging.add_node(0.0);
        let mut acc = Accumulator::new(10.0);
        acc.configure(
            &mut sys,
            n,
            &[(Resource::LiquidHydrogen, 0.5), (Resource::LiquidOxygen, 0.3)],
        );
        let h = acc.refill_device().unwrap();
        assert_eq!(h.domain(), crate::resource::ResourceDomain::Topological);
        assert_relative_eq!(acc.refill_rate, 0.8);
    }

    #[test]
    fn configure_routes_uniform_inputs_to_process() {
        let mut sys = fresh_sys();
        let n = sys.staging.add_node(0.0);
        let mut acc = Accumulator::new(100.0);
        acc.configure(&mut sys, n, &[(Resource::ElectricCharge, 25.0)]);
        let h = acc.refill_device().unwrap();
        assert_eq!(h.domain(), crate::resource::ResourceDomain::Uniform);
    }

    #[test]
    fn configure_pushes_initial_demand_per_refill_active() {
        let mut sys = fresh_sys();
        let n = sys.staging.add_node(0.0);
        let mut acc_off = Accumulator::new(10.0);
        acc_off.refill_active = false;
        acc_off.configure(&mut sys, n, &[(Resource::LiquidHydrogen, 1.0)]);
        assert_relative_eq!(sys.device_demand(acc_off.refill_device().unwrap()), 0.0);

        let mut acc_on = Accumulator::new(10.0);
        acc_on.refill_active = true;
        acc_on.configure(&mut sys, n, &[(Resource::LiquidOxygen, 1.0)]);
        assert_relative_eq!(sys.device_demand(acc_on.refill_device().unwrap()), 1.0);
    }

    #[test]
    fn hysteresis_flips_on_when_below_on_fraction() {
        let mut sys = fresh_sys();
        let n = sys.staging.add_node(0.0);
        let _ = sys.staging.add_buffer(n, Resource::LiquidHydrogen, 100.0);

        let mut acc = Accumulator::new(100.0).with_contents(5.0); // 5%
        acc.refill_active = false;
        acc.configure(&mut sys, n, &[(Resource::LiquidHydrogen, 1.0)]);

        acc.on_pre_solve(&mut sys);
        assert!(acc.refill_active);
    }

    #[test]
    fn hysteresis_flips_off_when_above_off_fraction() {
        let mut sys = fresh_sys();
        let n = sys.staging.add_node(0.0);

        let mut acc = Accumulator::new(100.0).with_contents(100.0); // full
        acc.refill_active = true;
        acc.configure(&mut sys, n, &[(Resource::LiquidHydrogen, 1.0)]);

        acc.on_pre_solve(&mut sys);
        assert!(!acc.refill_active);
    }

    #[test]
    fn hysteresis_holds_in_band() {
        let mut sys = fresh_sys();
        let n = sys.staging.add_node(0.0);

        let mut acc = Accumulator::new(100.0).with_contents(50.0); // 50%
        acc.refill_active = false;
        acc.configure(&mut sys, n, &[(Resource::LiquidHydrogen, 1.0)]);
        acc.on_pre_solve(&mut sys);
        assert!(!acc.refill_active);

        let mut acc_on = Accumulator::new(100.0).with_contents(50.0);
        acc_on.refill_active = true;
        acc_on.configure(&mut sys, n, &[(Resource::LiquidHydrogen, 1.0)]);
        acc_on.on_pre_solve(&mut sys);
        assert!(acc_on.refill_active);
    }

    #[test]
    fn valid_until_filling_lands_on_off_fraction() {
        let clock = SimClock::new(0.0);
        let mut sys = VesselSystems::new(clock.clone());
        let n = sys.staging.add_node(0.0);
        let _ = sys.staging.add_buffer(n, Resource::LiquidHydrogen, 100.0);

        let mut acc = Accumulator::new(100.0).with_contents(20.0);
        acc.refill_active = true;
        acc.refill_off_fraction = 1.0;
        acc.configure(&mut sys, n, &[(Resource::LiquidHydrogen, 1.0)]);
        // Skip solve: fake activity = 1 directly.
        acc.refill_activity = 1.0;
        acc.recompute_rate();
        acc.refresh_valid_until();
        // rate +1/s, slack = 80 - 20 = 80. Expected valid_until = 80 s.
        assert_relative_eq!(acc.valid_until(), 80.0);
    }

    #[test]
    fn valid_until_no_motion_is_infinity() {
        let mut sys = fresh_sys();
        let n = sys.staging.add_node(0.0);
        let mut acc = Accumulator::new(100.0).with_contents(50.0);
        acc.configure(&mut sys, n, &[(Resource::LiquidHydrogen, 1.0)]);
        // rate = 0 → no flip reachable.
        acc.refresh_valid_until();
        assert!(acc.valid_until().is_infinite());
    }
}
