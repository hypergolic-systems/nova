//! State-change bisection for per-link horizon scheduling. Mirrors
//! `mod/Nova.Core/Communications/LinkHorizon.cs`.
//!
//! The caller supplies a `state_at(ut) -> i32` function — bucket index
//! for rate quantisation, 0/1 for occlusion blocked/clear, or any
//! other integer-valued state. This module owns only the search:
//! coarse uniform sweep over `MAX_HORIZON_SECONDS`, bisection to
//! refine the crossing, conservative horizon-cap fallback if no
//! crossing is seen.
//!
//! The harness only finds the FIRST state change in the window —
//! callers tracking multiple parallel state functions (e.g. bucket
//! transition + occlusion transition) should run the search per state
//! and take the min, not OR-combine into one `state_at` (which
//! collapses adjacent transitions of distinct states into a single
//! event).

use super::parameters::{HORIZON_SEARCH_STEPS, MAX_HORIZON_SECONDS};

/// Returns the UT at which `state_at` first reports a different value
/// than `state_at(current_ut)`, found by uniform sweep + bisection in
/// `(current_ut, current_ut + MAX_HORIZON_SECONDS]`. If no crossing is
/// seen, returns `current_ut + MAX_HORIZON_SECONDS`.
///
/// The returned UT is the past-crossing side of the bisected window:
/// `state_at(result)` is guaranteed to differ from `state_at(current_ut)`.
/// The driver calling `solve(next_event_ut)` lands past the crossing,
/// not straddling it.
pub fn next_discrete_change<F>(current_ut: f64, state_at: F) -> f64
where
    F: Fn(f64) -> i32,
{
    let current_state = state_at(current_ut);

    let horizon = MAX_HORIZON_SECONDS;
    let steps = HORIZON_SEARCH_STEPS;
    let step = horizon / f64::from(steps);
    let threshold = horizon * 1e-6;

    for i in 1..=steps {
        let t = current_ut + f64::from(i) * step;
        if state_at(t) != current_state {
            let mut lo = t - step;
            let mut hi = t;
            while hi - lo > threshold {
                let mid = (lo + hi) / 2.0;
                if state_at(mid) == current_state {
                    lo = mid;
                } else {
                    hi = mid;
                }
            }
            return hi;
        }
    }

    current_ut + horizon
}
