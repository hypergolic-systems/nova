//! `#[repr(C)]` state structs that round-trip through the per-vessel
//! arena. PascalCase field names (intentional — see the crate's
//! naming convention note below) so csbindgen emits idiomatic C#.
//!
//! Each component variant in nova-sim that wants a mirror slot has
//! exactly one struct here. Fields are bidirectional: some are
//! written by Rust post-tick (LP outputs, derived telemetry), some
//! by C# pre-tick (player toggles, throttle settings). Both sides
//! agree by convention which fields they own.

#![allow(non_snake_case)]

mod battery;
mod command;

pub use battery::BatteryState;
pub use command::CommandState;
