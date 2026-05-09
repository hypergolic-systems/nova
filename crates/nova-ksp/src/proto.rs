//! prost-generated Rust bindings for `proto/nova.proto`. We re-export
//! under a single module so call sites don't have to know about the
//! cargo `OUT_DIR` mechanism.
//!
//! Keep this module thin — no business logic. Conversion from these
//! types into nova-sim's idiomatic types lives in `vessel.rs`.

#![allow(clippy::all)]

include!(concat!(env!("OUT_DIR"), "/nova.v1.rs"));
