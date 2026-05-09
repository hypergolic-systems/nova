//! Mirror of `mod/Nova.Tests/Communications/RateBucketsTests.cs`.

use approx::assert_relative_eq;
use nova_sim::comms::{bucket_index, quantize, BUCKET_COUNT};

// All tests assume BUCKET_COUNT == 10 (matches C# default). If that
// ever changes, the assertion arithmetic here will need to track it.

#[test]
fn quantize_below_knee_rounds_down_to_bucket_floor() {
    // shannon = 0.55, ceiling = 1000 → bucket index 5, floor = 500.
    assert_relative_eq!(quantize(550.0, 1000.0), 500.0, epsilon = 1e-9);
}

#[test]
fn quantize_above_knee_returns_full_ceiling() {
    // rate ≥ ceiling lands in the above-knee bucket (index N), which
    // reports the full hardware ceiling — not (N-1)/N · ceiling.
    assert_relative_eq!(quantize(1000.0, 1000.0), 1000.0, epsilon = 1e-9);
    assert_relative_eq!(quantize(1500.0, 1000.0), 1000.0, epsilon = 1e-9);
    assert_relative_eq!(quantize(1.0e9, 1000.0), 1000.0, epsilon = 1e-9);
}

#[test]
fn quantize_at_boundary_falls_into_upper_bucket() {
    // shannon exactly 0.7 → floor(7.0) = 7 → bucket 7, floor 700.
    assert_relative_eq!(quantize(700.0, 1000.0), 700.0, epsilon = 1e-9);
}

#[test]
fn quantize_zero_returns_zero() {
    assert_relative_eq!(quantize(0.0, 1000.0), 0.0);
}

#[test]
fn quantize_negative_rate_returns_zero() {
    assert_relative_eq!(quantize(-50.0, 1000.0), 0.0);
}

#[test]
fn quantize_degenerate_ceiling_returns_zero() {
    assert_relative_eq!(quantize(100.0, 0.0), 0.0);
    assert_relative_eq!(quantize(100.0, -1.0), 0.0);
}

#[test]
fn bucket_index_above_knee_is_bucket_count() {
    assert_eq!(bucket_index(1000.0, 1000.0), BUCKET_COUNT);
    assert_eq!(bucket_index(2.0e9, 1000.0), BUCKET_COUNT);
}

#[test]
fn bucket_index_sub_knee_monotone() {
    // Index is monotone non-decreasing in rate within sub-knee.
    let mut prev: i32 = -1;
    for i in 0..=100 {
        let r = (i as f64) * 9.99; // up to 999, all sub-knee
        let idx = bucket_index(r, 1000.0);
        assert!(idx >= prev, "bucket regressed at rate {}: {} < {}", r, idx, prev);
        prev = idx;
    }
}
