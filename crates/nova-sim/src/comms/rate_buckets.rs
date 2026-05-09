//! Quantises continuous link rates onto a fixed bucket grid so
//! allocator and routing decisions stay stable across small
//! geometry changes. Mirrors `RateBuckets.cs`:
//!
//!   Bucket k ∈ [0, N-1]:  shannon ∈ [k/N, (k+1)/N),  floor = ceiling · k/N
//!   Bucket N:             rate ≥ ceiling,            floor = ceiling
//!
//! Reported rate is the bucket floor — conservative within each
//! sub-knee bucket so the allocator never over-grants what geometry
//! won't sustain until the next solve.

use super::parameters::BUCKET_COUNT;

/// Bucket index for a continuous rate against the link ceiling.
///
/// - `0`              if `ceiling <= 0` (degenerate link)
/// - `BUCKET_COUNT`   if `rate >= ceiling` (above-knee)
/// - `floor(N · r/c)` otherwise, clamped to `[0, N-1]`
pub fn bucket_index(rate: f64, ceiling: f64) -> i32 {
    if ceiling <= 0.0 {
        return 0;
    }
    let n = BUCKET_COUNT;
    if rate >= ceiling {
        return n;
    }
    if rate <= 0.0 {
        return 0;
    }
    let idx = (rate / ceiling * f64::from(n)).floor() as i32;
    if idx < 0 {
        return 0;
    }
    if idx >= n {
        return n - 1;
    }
    idx
}

/// Bucket-floor rate. Above-knee returns the full ceiling (rate is
/// hardware-clamped); sub-knee returns `ceiling · k/N`.
pub fn quantize(rate: f64, ceiling: f64) -> f64 {
    if ceiling <= 0.0 {
        return 0.0;
    }
    let n = BUCKET_COUNT;
    let idx = bucket_index(rate, ceiling);
    if idx >= n {
        return ceiling;
    }
    ceiling * f64::from(idx) / f64::from(n)
}
