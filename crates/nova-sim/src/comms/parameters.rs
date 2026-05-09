//! System-wide tunables for the comms subsystem. Mirrors
//! `mod/Nova.Core/Communications/CommunicationsParameters.cs`.

/// SNR denominator. The absolute value is gameplay-irrelevant — only
/// the ratio between SNR(A→B) and SNR_ref(A) drives Shannon scaling;
/// antenna specs in cfg are calibrated against `NoiseFloor = 1`.
pub const NOISE_FLOOR: f64 = 1.0;

/// Equal-width bucket count partitioning the Shannon factor in [0, 1].
/// A distinguished above-knee bucket (index `BUCKET_COUNT`) holds
/// rates clamped to the hardware ceiling.
pub const BUCKET_COUNT: i32 = 10;

/// Cap on the per-link bucket-crossing horizon search, in seconds.
/// If the coarse sweep finds no crossing within this window,
/// `next_event_ut` returns `now + MAX_HORIZON_SECONDS` and the search
/// is repeated one horizon out.
pub const MAX_HORIZON_SECONDS: f64 = 86_400.0;

/// Coarse-sweep step count across `MAX_HORIZON_SECONDS`. Bisection
/// refines from there.
pub const HORIZON_SEARCH_STEPS: i32 = 200;

/// Distance-sample count in the cheap pre-screen that skips full
/// bucket-bisection for pairs always out of range.
pub const PRESCREEN_SAMPLES: i32 = 20;
