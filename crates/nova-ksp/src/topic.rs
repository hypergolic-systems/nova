//! Topic FFI — extern "C" wrappers around `nova-telemetry`.
//!
//! ## Shared-buffer model
//!
//! Each subscribed topic owns a stable Rust-side buffer. C# subscribes
//! once, gets back the buffer's base pointer, and reads from it
//! directly thereafter:
//!
//! ```text
//! offset 0..8   : version (u64)   — bumps when payload changes
//! offset 8..12  : len (u32)       — current payload length
//! offset 12..16 : cap (u32)       — payload capacity sanity slot
//! offset 16..   : payload bytes
//! ```
//!
//! C# polls the version word in `Update()` (two pointer derefs, no
//! FFI, no allocs); when it changes, the topic is marked dirty for
//! Dragonglass's broadcaster, which splices `len` bytes from
//! `base + 16` into the wire envelope. No drain step, no per-frame
//! FFI.
//!
//! `nova_world_tick` writes into every subscribed topic's buffer
//! during its post-sim refresh, diffing the new bytes against the
//! current payload and only bumping version on actual change.

#![allow(non_snake_case)]

use crate::world::NovaWorld;

/// Read a UTF-8 topic name from the (`name`, `name_len`) pair.
unsafe fn name_from_ffi<'a>(name: *const u8, name_len: u32) -> Option<&'a str> {
    if name.is_null() {
        return None;
    }
    let bytes = std::slice::from_raw_parts(name, name_len as usize);
    std::str::from_utf8(bytes).ok()
}

// ── extern "C" surface ──────────────────────────────────────────────

/// Add a refcount on the topic and return the stable pointer to its
/// buffer (16-byte header + payload). The pointer is valid until
/// the matching `nova_topic_unsubscribe` drops the refcount to 0.
/// Returns null on any error (null world, invalid name).
///
/// # Safety
/// `world` must be a valid `NovaWorld` pointer; `name` must be
/// readable for `name_len` bytes.
#[no_mangle]
pub unsafe extern "C" fn nova_topic_subscribe(
    world: *mut NovaWorld,
    name: *const u8,
    name_len: u32,
) -> *const u8 {
    if world.is_null() {
        return std::ptr::null();
    }
    let Some(s) = name_from_ffi(name, name_len) else {
        return std::ptr::null();
    };
    let w = &mut *world;
    w.topic_registry.subscribe(s)
}

/// Drop a refcount. Returns the remaining refcount; the buffer is
/// freed when it hits 0 (caller must drop the pointer it had).
///
/// # Safety
/// `world` must be a valid `NovaWorld` pointer; `name` must be
/// readable for `name_len` bytes.
#[no_mangle]
pub unsafe extern "C" fn nova_topic_unsubscribe(
    world: *mut NovaWorld,
    name: *const u8,
    name_len: u32,
) -> u32 {
    if world.is_null() {
        return 0;
    }
    let Some(s) = name_from_ffi(name, name_len) else {
        return 0;
    };
    let w = &mut *world;
    w.topic_registry.unsubscribe(s)
}
