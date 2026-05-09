//! World-level communications: directed graph of antenna-bearing
//! endpoints, max-rate routing, water-fill bandwidth allocation,
//! event-driven solve cadence. Mirrors the C# `Nova.Core.Communications`
//! namespace.

pub mod allocator;
pub mod antenna;
pub mod endpoint;
pub mod ground_station;
pub mod job;
pub mod link;
pub mod link_horizon;
pub mod max_rate_path;
pub mod motion;
pub mod occluder_set;
pub mod occlusion;
pub mod parameters;
pub mod rate_buckets;
pub mod system;

pub use antenna::Antenna;
pub use endpoint::{Endpoint, EndpointId, EndpointKind, PathSummary};
pub use ground_station::{ksc, GroundStationSpec};
pub use job::{Job, JobId, JobStatus, TopicKey};
pub use link::{GraphSnapshot, Link};
pub use motion::{position_at, surface_offset, MotionModel};
pub use occluder_set::occluder_set;
pub use occlusion::{is_any_blocked, is_blocked};
pub use system::CommsSystem;
pub use parameters::{
    BUCKET_COUNT, HORIZON_SEARCH_STEPS, MAX_HORIZON_SECONDS, NOISE_FLOOR, PRESCREEN_SAMPLES,
};
pub use rate_buckets::{bucket_index, quantize};
