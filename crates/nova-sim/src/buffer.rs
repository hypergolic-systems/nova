//! Resource buffer with lerp-based state. Mirrors
//! `mod/Nova.Core/Resources/Buffer.cs`.
//!
//! Instead of integrating Contents per physics tick (rate × dt every
//! frame), Buffer stores `(baseline_contents, baseline_ut, rate)` and
//! computes Contents lazily when read:
//!
//!   contents(t) = clamp(baseline_contents + rate × (t - baseline_ut), 0, capacity)
//!
//! `t` is read from the shared SimClock the owning system installed.
//! Callers don't pass time explicitly — `b.contents()` always returns
//! the right value. Simulation cost scales with the number of
//! rate-change events, not with the number of physics ticks.
//!
//! Maintenance contract:
//! - Solver-driven owners (StagingFlowSystem / ProcessFlowSystem)
//!   can call `refresh(ut)` before changing rate to capture the
//!   lerped value at "now". `set_rate` does this automatically too;
//!   explicit `refresh` is for batch readability.
//! - Direct mutation of contents (loading a tank, restoring a save)
//!   goes through `set_contents`, which rebaselines.
//! - Tests that pre-Clock construct a Buffer can leave the clock as
//!   `None`; the lerp falls back to the static baseline value.

use crate::resource::Resource;
use crate::sim_clock::SimClock;

#[derive(Clone, Debug)]
pub struct Buffer {
    pub resource: Resource,
    pub capacity: f64,
    pub max_rate_in: f64,
    pub max_rate_out: f64,

    /// Baseline state. Direct field access for snapshot/restore code
    /// paths; most callers should go through the methods below.
    pub baseline_contents: f64,
    pub baseline_ut: f64,

    /// Shared clock — installed by the owning system at construction.
    /// Tests can leave this `None`; lerp collapses to a static value
    /// at `baseline_ut`.
    pub(crate) clock: Option<SimClock>,

    rate: f64,
}

impl Buffer {
    /// Construct a new buffer. Solver-side construction populates the
    /// clock; tests that don't care about the lerp can pass `None`.
    pub fn new(resource: Resource, capacity: f64, clock: Option<SimClock>) -> Self {
        let baseline_ut = clock.as_ref().map(|c| c.ut()).unwrap_or(0.0);
        Buffer {
            resource,
            capacity,
            max_rate_in: 0.0,
            max_rate_out: 0.0,
            baseline_contents: capacity,
            baseline_ut,
            clock,
            rate: 0.0,
        }
    }

    fn now(&self) -> f64 {
        self.clock.as_ref().map(|c| c.ut()).unwrap_or(self.baseline_ut)
    }

    /// Current contents — lerped from baseline to the shared clock's
    /// UT and clamped to `[0, capacity]`.
    pub fn contents(&self) -> f64 {
        self.contents_at(self.now())
    }

    /// Contents at an arbitrary UT — useful for forecasting (e.g. the
    /// solver predicting empty/full times).
    pub fn contents_at(&self, ut: f64) -> f64 {
        let projected = self.baseline_contents + self.rate * (ut - self.baseline_ut);
        if projected < 0.0 {
            0.0
        } else if projected > self.capacity {
            self.capacity
        } else {
            projected
        }
    }

    pub fn rate(&self) -> f64 {
        self.rate
    }

    /// Set rate and rebaseline. The OLD rate was valid from
    /// `baseline_ut` up to "now"; capture the resulting contents as
    /// the new baseline so the new rate applies forward, not
    /// retroactively.
    pub fn set_rate(&mut self, rate: f64) {
        let t = self.now();
        self.baseline_contents = self.contents_at(t);
        self.baseline_ut = t;
        self.rate = rate;
    }

    /// Replace contents wholesale (load a tank, restore from save).
    /// Rebaselines at the current clock UT; rate is left unchanged.
    pub fn set_contents(&mut self, contents: f64) {
        self.baseline_contents = contents;
        self.baseline_ut = self.now();
    }

    /// Capture contents at `ut` as the new baseline. Rate unchanged.
    /// Solvers use this to batch baseline updates across many buffers
    /// before a rate-write storm.
    pub fn refresh(&mut self, ut: f64) {
        self.baseline_contents = self.contents_at(ut);
        self.baseline_ut = ut;
    }

    pub fn flow_limits(&mut self, in_: f64, out: f64) {
        self.max_rate_in = in_;
        self.max_rate_out = out;
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use approx::assert_relative_eq;

    fn fresh_buffer(clock: SimClock) -> Buffer {
        let mut b = Buffer::new(Resource::Hydrazine, 100.0, Some(clock));
        b.flow_limits(10.0, 10.0);
        b
    }

    #[test]
    fn contents_at_baseline_ut_is_baseline() {
        let clock = SimClock::new(50.0);
        let b = fresh_buffer(clock);
        assert_relative_eq!(b.contents(), 100.0);
    }

    #[test]
    fn contents_lerps_with_rate_over_time() {
        let clock = SimClock::new(0.0);
        let mut b = fresh_buffer(clock.clone());
        b.set_rate(-1.0);
        clock.advance(10.0);
        assert_relative_eq!(b.contents(), 90.0);
        clock.advance(40.0);
        assert_relative_eq!(b.contents(), 50.0);
    }

    #[test]
    fn contents_clamps_at_zero_when_drained_past_floor() {
        let clock = SimClock::new(0.0);
        let mut b = fresh_buffer(clock.clone());
        b.set_rate(-2.0);
        clock.advance(80.0);
        assert_relative_eq!(b.contents(), 0.0);
    }

    #[test]
    fn contents_clamps_at_capacity_when_filled_past_ceiling() {
        let clock = SimClock::new(0.0);
        let mut b = Buffer::new(Resource::Hydrazine, 100.0, Some(clock.clone()));
        b.set_contents(50.0);
        b.set_rate(2.0);
        clock.advance(40.0);
        assert_relative_eq!(b.contents(), 100.0);
    }

    #[test]
    fn set_rate_rebaselines_so_new_rate_applies_forward() {
        let clock = SimClock::new(0.0);
        let mut b = fresh_buffer(clock.clone());
        b.set_rate(-1.0);
        clock.advance(20.0);
        assert_relative_eq!(b.contents(), 80.0);
        // Switching to fill rate from the current value, not retroactively.
        b.set_rate(0.5);
        assert_relative_eq!(b.contents(), 80.0);
        clock.advance(20.0);
        assert_relative_eq!(b.contents(), 90.0);
    }

    #[test]
    fn refresh_rebaselines_without_changing_rate() {
        let clock = SimClock::new(0.0);
        let mut b = fresh_buffer(clock.clone());
        b.set_rate(-1.0);
        clock.advance(30.0);
        b.refresh(clock.ut());
        assert_relative_eq!(b.baseline_contents, 70.0);
        assert_relative_eq!(b.baseline_ut, 30.0);
        assert_relative_eq!(b.rate(), -1.0);
    }

    #[test]
    fn set_contents_replaces_value_and_anchors_at_clock() {
        let clock = SimClock::new(100.0);
        let mut b = fresh_buffer(clock.clone());
        b.set_rate(-1.0);
        clock.advance(10.0); // contents now ~90
        b.set_contents(42.0);
        assert_relative_eq!(b.contents(), 42.0);
        assert_relative_eq!(b.baseline_ut, 110.0);
        // Rate is preserved across set_contents.
        assert_relative_eq!(b.rate(), -1.0);
    }

    #[test]
    fn flow_limits_writes_both_caps() {
        let mut b = Buffer::new(Resource::Rp1, 1000.0, None);
        b.flow_limits(5.5, 12.5);
        assert_relative_eq!(b.max_rate_in, 5.5);
        assert_relative_eq!(b.max_rate_out, 12.5);
    }

    #[test]
    fn no_clock_means_static_value_at_baseline_ut() {
        let mut b = Buffer::new(Resource::Rp1, 100.0, None);
        assert_relative_eq!(b.contents(), 100.0);
        // Even after setting a non-zero rate, with no clock the lerp
        // sees no time passing.
        b.set_rate(-10.0);
        assert_relative_eq!(b.contents(), 100.0);
    }

    #[test]
    fn contents_at_ignores_clock_uses_passed_ut() {
        let clock = SimClock::new(0.0);
        let mut b = fresh_buffer(clock.clone());
        b.set_rate(-1.0);
        // contents_at projects from baseline regardless of the clock.
        assert_relative_eq!(b.contents_at(25.0), 75.0);
        assert_relative_eq!(b.contents_at(0.0), 100.0);
    }
}
