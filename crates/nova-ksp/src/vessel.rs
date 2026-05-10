//! `FfiVessel` (per-vessel arena owner) + the proto-driven
//! `nova_vessel_new` entry point.
//!
//! Construction is one-shot: C# serializes the
//! `nova.v1.VesselStructure` + `nova.v1.VesselState` for the vessel
//! and hands them to Rust. nova-ksp deserializes via prost, walks
//! the part tree, and consults the prefab `PartDatabase` (set
//! separately via `nova_world_set_part_database`) to decide which
//! arena slots to allocate.
//!
//! Rule of thumb for which side owns what:
//!  - **`PartDatabase` (prefab):** static, identical for every
//!    instance of a part (idle_draw, max_rate, ...).
//!  - **`PartStructure`:** dynamic per-instance, editor-configurable
//!    (battery capacity, tank loadout).
//!  - **`PartState`:** runtime-mutable (battery contents, tank
//!    amounts, part activation).
//!
//! Anything that doesn't fit one of those buckets has no business in
//! the proto.

#![allow(non_snake_case)]

use std::collections::HashMap;

use nova_sim::components::Command;
use nova_sim::{
    Battery, BodyId, Component, OrbitalElements, Situation, Vessel, VesselId, World, WorldContext,
};
use prost::Message;

use crate::arena::{ArenaPlan, ComponentKind, ComponentSlot};
use crate::proto;
use crate::state::{BatteryState, CommandState};
use crate::world::NovaWorld;

/// Per-vessel arena owner. Holds the heterogeneous mirror buffer and
/// the slot manifest. `arena` is a `Box<[u8]>` so its base pointer
/// is stable until `Drop`.
pub struct FfiVessel {
    pub vessel_id: VesselId,
    arena: Box<[u8]>,
    slots: Vec<ComponentSlot>,
    /// Map (part_id, kind) → byte offset into `arena`. Built once at
    /// finalize so `write_outputs` doesn't pay a linear scan per
    /// component on the hot path.
    by_part_kind: HashMap<(u32, u32), usize>,
}

impl FfiVessel {
    pub fn arena_base(&mut self) -> *mut u8 {
        self.arena.as_mut_ptr()
    }

    pub fn arena_len(&self) -> u32 {
        self.arena.len() as u32
    }

    pub fn slots(&self) -> &[ComponentSlot] {
        &self.slots
    }

    /// Mirror canonical nova-sim state into the arena. Called post-
    /// tick (and once after `initialize_solver` so C# observes a
    /// populated buffer immediately).
    pub fn write_outputs(&mut self, world: &World) {
        let vessel = match world.vessels.iter().find(|v| v.id == self.vessel_id) {
            Some(v) => v,
            None => return,
        };
        let systems = match vessel.systems.as_ref() {
            Some(s) => s,
            None => return,
        };

        for part in &vessel.parts {
            for c in &part.components {
                match c {
                    Component::Battery(b) => {
                        let key = (part.id, ComponentKind::Battery as u32);
                        let Some(&off) = self.by_part_kind.get(&key) else { continue };
                        let state = unsafe {
                            &mut *(self.arena.as_mut_ptr().add(off) as *mut BatteryState)
                        };
                        state.Capacity = b.capacity;
                        state.Contents = match b.buffer_id() {
                            Some(bid) => systems.process.buffer(bid).contents(),
                            None => 0.0,
                        };
                    }
                    Component::Command(cmd) => {
                        let key = (part.id, ComponentKind::Command as u32);
                        let Some(&off) = self.by_part_kind.get(&key) else { continue };
                        let state = unsafe {
                            &mut *(self.arena.as_mut_ptr().add(off) as *mut CommandState)
                        };
                        state.IdleActivity = cmd.idle_activity(systems);
                    }
                    _ => {}
                }
            }
        }
    }
}

/// Returned to C# from `nova_vessel_new`. Pointers reference data
/// owned by the `FfiVessel` (kept alive in `NovaWorld`); they are
/// stable until `nova_vessel_remove` is called for this vessel.
#[repr(C)]
#[derive(Copy, Clone, Debug)]
pub struct VesselHandle {
    pub VesselId: u32,
    pub ArenaBase: *mut u8,
    pub ArenaLen: u32,
    pub SlotCount: u32,
    pub SlotsPtr: *const ComponentSlot,
    /// Pointer to the vessel's GUID string (e.g.
    /// `"01d76ab5-6d88-4105-b7ae-504d77b978c1"`, lowercase, no
    /// braces). Stable until the matching `nova_vessel_remove` —
    /// the simulator never reassigns a vessel's GUID after creation.
    /// C# reads this once at registration and assigns it to
    /// `Vessel.id` via a Harmony patch on
    /// `ShipConstruction.AssembleForLaunch`, replacing KSP's
    /// would-be `Guid.NewGuid()`.
    pub GuidPtr: *const u8,
    pub GuidLen: u32,
}

impl VesselHandle {
    pub const NULL: VesselHandle = VesselHandle {
        VesselId: 0,
        ArenaBase: std::ptr::null_mut(),
        ArenaLen: 0,
        SlotCount: 0,
        SlotsPtr: std::ptr::null(),
        GuidPtr: std::ptr::null(),
        GuidLen: 0,
    };
}

// ── extern "C" surface ──────────────────────────────────────────────

/// Build a vessel from serialized `VesselStructure` + `VesselState`
/// proto blobs (the same protos `.nvs` saves use). Allocates the
/// per-vessel mirror arena, runs nova-sim's `initialize_solver`,
/// mirrors initial state, and returns the `VesselHandle`. Pointers
/// are valid until `nova_vessel_remove`.
///
/// On any decode/build error returns `VesselHandle::NULL`.
///
/// # Safety
/// `world` must be valid; the two byte slices must be readable for
/// their declared lengths.
#[no_mangle]
pub unsafe extern "C" fn nova_vessel_new(
    world: *mut NovaWorld,
    structure_bytes: *const u8,
    structure_len: u32,
    state_bytes: *const u8,
    state_len: u32,
    ut: f64,
) -> VesselHandle {
    let w = &mut *world;

    let structure_slice = std::slice::from_raw_parts(structure_bytes, structure_len as usize);
    let state_slice = std::slice::from_raw_parts(state_bytes, state_len as usize);

    let structure = match proto::VesselStructure::decode(structure_slice) {
        Ok(s) => s,
        Err(_) => return VesselHandle::NULL,
    };
    let state = match proto::VesselState::decode(state_slice) {
        Ok(s) => s,
        Err(_) => return VesselHandle::NULL,
    };

    // The proto's persistent_id IS the vessel id we expose to C#.
    // (KSP's persistentId is uint32; our FFI lane preserves that.)
    let vessel_id = VesselId(structure.persistent_id);
    if w.world.vessels.iter().any(|v| v.id == vessel_id) {
        return VesselHandle::NULL;
    }

    // Pull orbit out of the structure. If absent — VAB pre-launch,
    // editor preview, anything host hasn't placed yet — register the
    // vessel as `Abstract`. The host calls `nova_vessel_set_situation`
    // later to transition it to `Orbit` when the data is ready.
    //
    // Phase-1 hardcodes Kerbin as the parent body until
    // `nova_world_set_body_database` lands; the body_index in the
    // proto is captured but ignored.
    const KERBIN_BODY_ID: u32 = 1;
    let situation = match structure.orbit.as_ref() {
        Some(o) => Situation::orbit(
            BodyId(KERBIN_BODY_ID),
            OrbitalElements {
                semi_major_axis: o.semi_major_axis,
                eccentricity: o.eccentricity,
                inclination: o.inclination,
                lan: o.lan,
                arg_periapsis: o.argument_of_periapsis,
                mean_anomaly_at_epoch: o.mean_anomaly_at_epoch,
                epoch: o.epoch,
            },
        ),
        None => Situation::Abstract,
    };

    let name = state.name.clone();
    // Rust owns vessel GUIDs. Save loads carry one in the proto;
    // editor launches send empty and we mint a fresh v4 here. C#
    // reads the assigned GUID back through `VesselHandle.GuidPtr`
    // and overrides KSP's `Guid.NewGuid()` (via Harmony on
    // `ShipConstruction.AssembleForLaunch`) so KSP and the
    // simulator agree from the start.
    let guid = if structure.vessel_id.is_empty() {
        uuid::Uuid::new_v4().to_string()
    } else {
        structure.vessel_id.clone()
    };
    let mut vessel = Vessel::new(vessel_id, name, situation).with_guid(guid);

    // Walk parts, build components from PartStructure + PartState +
    // prefab database lookup.
    let state_by_id: HashMap<u32, &proto::PartState> =
        state.parts.iter().map(|p| (p.id, p)).collect();

    let mut plan = ArenaPlan::new();

    for ps in &structure.parts {
        let prefab = w.part_db.get(&ps.part_name);
        let prefab_mass = prefab.map(|n| n.dry_mass_kg).unwrap_or(0.0);
        let display_title = prefab
            .map(|n| n.display_title.clone())
            .filter(|t| !t.is_empty())
            .unwrap_or_else(|| ps.part_name.clone());

        vessel.add_part(ps.id, ps.part_name.clone(), prefab_mass, Vec::new());
        vessel.part_mut(ps.id).display_title = display_title;
        if ps.parent_index >= 0 {
            let parent_idx = ps.parent_index as usize;
            if parent_idx < structure.parts.len() {
                let parent_id = structure.parts[parent_idx].id;
                vessel.set_parent(ps.id, parent_id);
            }
        }

        let part_state = state_by_id.get(&ps.id).copied();

        // Battery — capacity comes from PartStructure (editor
        // configurable in principle), contents from PartState,
        // flow caps from the prefab.
        if let Some(struct_battery) = ps.battery.as_ref() {
            let max_rate = prefab
                .and_then(|n| n.battery.as_ref())
                .map(|b| b.max_rate)
                .unwrap_or(10.0);
            let capacity = struct_battery.capacity;
            let initial = part_state
                .and_then(|s| s.battery.as_ref())
                .map(|b| b.value)
                .unwrap_or(capacity);
            let battery = Battery::new(capacity)
                .with_contents(initial)
                .with_flow_limits(max_rate, max_rate);
            vessel.part_mut(ps.id).components.push(Component::Battery(battery));
            plan.allocate(ps.id, ComponentKind::Battery);
        }

        // Command — entirely prefab-driven. Presence determined by
        // the prefab having a CommandPrefab; `idle_draw` from there.
        if let Some(cmd_prefab) = prefab.and_then(|n| n.command.as_ref()) {
            if cmd_prefab.idle_draw > 0.0 {
                vessel
                    .part_mut(ps.id)
                    .components
                    .push(Component::Command(Command::new(cmd_prefab.idle_draw)));
                plan.allocate(ps.id, ComponentKind::Command);
            }
        }
    }

    // Allocate the arena. Zero-initialised so the first read sees
    // ZERO before any tick lands.
    let arena: Box<[u8]> = vec![0u8; plan.total_len as usize].into_boxed_slice();

    let mut by_part_kind = HashMap::with_capacity(plan.slots.len());
    for s in &plan.slots {
        by_part_kind.insert((s.PartId, s.Kind), s.StateOffset as usize);
    }

    let mut fv = FfiVessel {
        vessel_id,
        arena,
        slots: plan.slots,
        by_part_kind,
    };

    // Hand the vessel to nova-sim, run initialize_solver, seed the
    // arena. Split-borrow on world so we can hold &ephemeris while
    // mutating &vessels.
    w.world.vessels.push(vessel);
    let ctx = WorldContext::new(&w.world.ephemeris);
    let v_mut = w
        .world
        .vessels
        .iter_mut()
        .find(|v| v.id == vessel_id)
        .unwrap();
    v_mut.initialize_solver(&ctx, ut);
    fv.write_outputs(&w.world);

    // Borrow the (now-immutable) Vessel.guid to expose its bytes
    // through the handle. `vessel.guid` is a `String` we set above
    // and never reassign; its allocation moves only on Vec
    // reallocation, which doesn't happen between the push above
    // and the read here.
    let v_ref = w
        .world
        .vessels
        .iter()
        .find(|v| v.id == vessel_id)
        .unwrap();
    let guid_bytes = v_ref.guid.as_bytes();

    let handle = VesselHandle {
        VesselId: structure.persistent_id,
        ArenaBase: fv.arena_base(),
        ArenaLen: fv.arena_len(),
        SlotCount: fv.slots().len() as u32,
        SlotsPtr: fv.slots().as_ptr(),
        GuidPtr: guid_bytes.as_ptr(),
        GuidLen: guid_bytes.len() as u32,
    };

    w.ffi_vessels.insert(structure.persistent_id, fv);
    handle
}

/// Update a vessel's situation. The host registers a vessel as
/// `Abstract` when it isn't placed yet (KSP fires `Vessel.Initialize`
/// before `orbitDriver` is wired, the editor preview never has an
/// orbit, etc.) and calls this to transition to `Orbit` once the
/// host's orbit data is ready.
///
/// Returns `0` on success, `-1` if `vessel_id` is unknown.
///
/// # Safety
/// `world` must be a valid `NovaWorld` pointer.
#[no_mangle]
pub unsafe extern "C" fn nova_vessel_set_situation_orbit(
    world: *mut NovaWorld,
    vessel_id: u32,
    semi_major_axis: f64,
    eccentricity: f64,
    inclination: f64,
    lan: f64,
    arg_periapsis: f64,
    mean_anomaly_at_epoch: f64,
    epoch: f64,
    body_index: u32,
) -> i32 {
    let _ = body_index; // Phase-1: Kerbin only, see nova_vessel_new.
    const KERBIN_BODY_ID: u32 = 1;
    let w = &mut *world;
    let vid = VesselId(vessel_id);
    let v = match w.world.vessels.iter_mut().find(|v| v.id == vid) {
        Some(v) => v,
        None => return -1,
    };
    v.set_situation(Situation::orbit(
        BodyId(KERBIN_BODY_ID),
        OrbitalElements {
            semi_major_axis,
            eccentricity,
            inclination,
            lan,
            arg_periapsis,
            mean_anomaly_at_epoch,
            epoch,
        },
    ));
    0
}

/// Drop a vessel and its arena. Any `VesselHandle` previously
/// returned for this vessel is invalid after this call — C# must
/// drop its cached pointers.
#[no_mangle]
pub unsafe extern "C" fn nova_vessel_remove(world: *mut NovaWorld, vessel_id: u32) -> i32 {
    let w = &mut *world;
    w.ffi_vessels.remove(&vessel_id);
    let before = w.world.vessels.len();
    w.world.vessels.retain(|v| v.id != VesselId(vessel_id));
    if w.world.vessels.len() == before {
        -1
    } else {
        0
    }
}
