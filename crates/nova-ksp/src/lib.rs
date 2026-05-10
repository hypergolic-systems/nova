//! `nova-ksp` — FFI bridge between KSP (C# Nova.dll) and `nova-sim`.
//!
//! Surface contract:
//!
//! - **Per-vessel mirror arena.** Each `nova_vessel_new` call
//!   allocates one `Box<[u8]>` sized to fit one state struct per
//!   (part, component-kind) pair. Pointers are stable for the
//!   vessel's lifetime.
//! - **Bidirectional `#[repr(C)]` state structs** (PascalCase fields)
//!   in `state/`. csbindgen emits matching `[StructLayout]` C# types.
//! - **Proto-driven vessel creation.** C# serialises
//!   `Proto.VesselStructure` + `Proto.VesselState` (the same protos
//!   already used for `.nvs` saves) and hands the bytes to
//!   `nova_vessel_new`. Static prefab data lives in a separate
//!   `PartDatabase` proto pushed at startup via
//!   `nova_world_set_part_database`.
//! - **No FFI surface in nova-sim.** The simulator stays Rust-
//!   idiomatic; this crate is the only one that touches `extern "C"`,
//!   `#[repr(C)]`, prost, or unsafe pointer arithmetic.
//!
//! Tick loop: pre-tick copies any C#-owned input fields out of the
//! arena into nova-sim's canonical state; post-tick mirrors Rust-
//! owned output fields back. Phase-1 has no inputs to copy in
//! (Battery + Command are output-only); the input-direction code
//! lights up alongside Engine throttle.

pub mod arena;
pub mod proto;
pub mod state;
pub mod topic;
pub mod vessel;
pub mod world;

pub use arena::{ComponentKind, ComponentSlot};
pub use state::{BatteryState, CommandState};
pub use topic::{nova_topic_subscribe, nova_topic_unsubscribe};
pub use vessel::{nova_vessel_new, nova_vessel_remove, VesselHandle};
pub use world::{
    nova_world_create, nova_world_destroy, nova_world_set_part_database, nova_world_tick,
    NovaWorld,
};
