//! World handle + the world-level `extern "C"` entry points.

use std::collections::HashMap;

use nova_sim::{Body, World};
use nova_telemetry::TopicRegistry;
use prost::Message;

use crate::proto;
use crate::vessel::FfiVessel;

/// Opaque handle returned to C#. Owns:
///
/// - The nova-sim `World` (vessels, ephemeris, comms graph).
/// - The per-vessel `FfiVessel` map (mirror arenas + slot manifests).
/// - The prefab `PartDatabase` (`name → NovaPart`) used at vessel-add
///   time to discover which components a part contributes.
/// - The telemetry topic registry — refcounted snapshot buffers
///   for the `nova/*` topic family.
///
/// Layout is opaque to C#: callers only ever see `*mut NovaWorld`.
pub struct NovaWorld {
    pub(crate) world: World,
    pub(crate) ffi_vessels: HashMap<u32, FfiVessel>,
    pub(crate) part_db: HashMap<String, proto::NovaPart>,
    pub(crate) topic_registry: TopicRegistry,
}

impl NovaWorld {
    pub fn new() -> Self {
        // Phase-1 ships a hard-coded Kerbol body database — the C#
        // side doesn't pump bodies in over the FFI yet. Future PRs
        // add `nova_world_set_body_database` for parity with the
        // part-prefab path.
        NovaWorld {
            world: World::builder().bodies(kerbol_bodies()).build(),
            ffi_vessels: HashMap::new(),
            part_db: HashMap::new(),
            topic_registry: TopicRegistry::new(),
        }
    }
}

impl Default for NovaWorld {
    fn default() -> Self {
        Self::new()
    }
}

fn kerbol_bodies() -> Vec<Body> {
    nova_sim::fixtures::kerbol_bodies()
}

// ── extern "C" surface ──────────────────────────────────────────────

/// Allocate a fresh world and return an owning pointer. The caller
/// (C#) must release it via `nova_world_destroy`.
#[no_mangle]
pub extern "C" fn nova_world_create() -> *mut NovaWorld {
    Box::into_raw(Box::new(NovaWorld::new()))
}

/// Free a world allocated by `nova_world_create`. After return, the
/// pointer is invalid and must not be dereferenced.
///
/// # Safety
/// `world` must be a pointer previously returned by
/// `nova_world_create` and not yet freed.
#[no_mangle]
pub unsafe extern "C" fn nova_world_destroy(world: *mut NovaWorld) {
    if world.is_null() {
        return;
    }
    drop(Box::from_raw(world));
}

/// Replace the prefab part database with the supplied serialized
/// `nova.v1.PartDatabase` blob. Idempotent — call any time, but
/// existing `FfiVessel`s keep the slot manifest they were built
/// with. Returns 0 on success, non-zero on parse failure.
///
/// # Safety
/// `world` must be valid; `bytes` readable for `len` bytes.
#[no_mangle]
pub unsafe extern "C" fn nova_world_set_part_database(
    world: *mut NovaWorld,
    bytes: *const u8,
    len: u32,
) -> i32 {
    let w = &mut *world;
    let slice = std::slice::from_raw_parts(bytes, len as usize);
    let db = match proto::PartDatabase::decode(slice) {
        Ok(db) => db,
        Err(_) => return -1,
    };
    w.part_db.clear();
    for p in db.parts {
        w.part_db.insert(p.name.clone(), p);
    }
    0
}

/// Advance the world to `target_ut`. After return, every vessel's
/// arena holds the post-tick mirror values.
///
/// # Safety
/// `world` must be a valid pointer from `nova_world_create`.
#[no_mangle]
pub unsafe extern "C" fn nova_world_tick(world: *mut NovaWorld, target_ut: f64) {
    let w = &mut *world;
    w.world.tick(target_ut);
    for fv in w.ffi_vessels.values_mut() {
        fv.write_outputs(&w.world);
    }
    // Refresh every active topic's snapshot. Pointers handed out by
    // `nova_topic_get_payload` stay valid until the next tick.
    w.topic_registry.refresh(&w.world);
}
