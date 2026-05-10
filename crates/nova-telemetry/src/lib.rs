//! `nova-telemetry` — topic registry, snapshot buffers, and per-topic
//! JSON serializers for Nova.
//!
//! Pure Rust: borrows `&nova_sim::World` to produce wire bytes; never
//! exposes `extern "C"`. Two transports consume the bytes:
//!
//! - In production, `nova-ksp` wraps the registry behind extern "C"
//!   and a generic C# proxy splices the bytes into Dragonglass's
//!   WebSocket broadcaster.
//! - In a future `nova-stand` harness, a Rust WS server emits the
//!   same bytes directly to a browser.
//!
//! Topics are addressed by their wire-level name (e.g.
//! `nova/part/12345`, `nova/vessel-structure/abc-...`). Each
//! subscription is refcounted; the registry walks every active
//! subscription on `refresh` (called once per `nova_world_tick`)
//! and re-serializes into a per-name `Vec<u8>` snapshot.
//! Subscribers read `(ptr, len, gen)`; gen increments per refresh
//! so consumers can skip unchanged frames.

pub mod frame;
pub mod registry;
pub mod topics;

pub use registry::TopicRegistry;
pub use topics::serialize as serialize_topic;
