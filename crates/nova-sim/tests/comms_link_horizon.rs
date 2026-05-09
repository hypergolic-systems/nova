//! Mirror of `mod/Nova.Tests/Communications/LinkHorizonTests.cs`.

use nova_sim::comms::link_horizon::next_discrete_change;
use nova_sim::comms::MAX_HORIZON_SECONDS;

#[test]
fn stationary_horizon_is_horizon_cap() {
    let result = next_discrete_change(0.0, |_| 5);
    approx::assert_relative_eq!(result, MAX_HORIZON_SECONDS, epsilon = 1e-9);
}

#[test]
fn step_function_finds_crossing_near_step_ut() {
    // Bucket flips from 5 → 4 at UT = 1234.5. Bisection should land
    // within `horizon · 1e-6 ≈ 0.086` s.
    let crossing_ut = 1234.5;
    let result = next_discrete_change(0.0, |ut| if ut < crossing_ut { 5 } else { 4 });
    let threshold = MAX_HORIZON_SECONDS * 1e-6;
    assert!(
        (result - crossing_ut).abs() < threshold,
        "expected {}, got {} (threshold {})",
        crossing_ut,
        result,
        threshold
    );
}

#[test]
fn two_crossings_returns_first_only() {
    // Bucket: 5 (until 1000) → 4 (until 5000) → 3.
    let result = next_discrete_change(0.0, |ut| {
        if ut < 1000.0 {
            5
        } else if ut < 5000.0 {
            4
        } else {
            3
        }
    });
    assert!(result < 2000.0, "expected first crossing near 1000, got {}", result);
    assert!(result > 999.0, "expected first crossing near 1000, got {}", result);
}

#[test]
fn crossing_past_horizon_returns_horizon_cap() {
    // Crossing is at 100k seconds, past the 86400 default horizon.
    let result = next_discrete_change(0.0, |ut| if ut < 100_000.0 { 5 } else { 4 });
    approx::assert_relative_eq!(result, MAX_HORIZON_SECONDS, epsilon = 1e-9);
}

#[test]
fn nonzero_current_ut_horizon_relative_to_it() {
    // Crossing at absolute UT = 5000; currentUT = 1000. Should still
    // return absolute 5000, not 4000 or 6000.
    let result = next_discrete_change(1000.0, |ut| if ut < 5000.0 { 7 } else { 6 });
    let threshold = MAX_HORIZON_SECONDS * 1e-6;
    assert!(
        (result - 5000.0).abs() < threshold,
        "expected ≈5000, got {}",
        result
    );
}

#[test]
fn rising_bucket_also_triggers_crossing() {
    // Approaching geometry: bucket 4 → 5. Crossing detected; direction
    // doesn't matter.
    let result = next_discrete_change(0.0, |ut| if ut < 2000.0 { 4 } else { 5 });
    let threshold = MAX_HORIZON_SECONDS * 1e-6;
    assert!(
        (result - 2000.0).abs() < threshold,
        "expected ≈2000, got {}",
        result
    );
}
