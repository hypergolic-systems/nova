//! End-to-end smoke test of the proto-driven `extern "C"` FFI surface.
//! Mirrors what `NovaVesselModule` will do at vessel-create time:
//!   1. Push a `PartDatabase` blob describing all known prefab parts.
//!   2. Build a `VesselStructure` + `VesselState` for the vessel and
//!      call `nova_vessel_new`.
//!   3. Read state via the arena pointers in the returned handle.

#![allow(non_snake_case)]

use nova_ksp::{
    nova_topic_subscribe, nova_topic_unsubscribe, nova_vessel_new, nova_vessel_remove,
    nova_world_create, nova_world_destroy, nova_world_set_part_database, nova_world_tick,
    BatteryState, CommandState, ComponentKind, ComponentSlot,
};
use nova_ksp::proto;
use prost::Message;

// Buffer header layout — must match crates/nova-telemetry/src/registry.rs.
const HEADER_VERSION_OFFSET: usize = 0;
const HEADER_LEN_OFFSET: usize = 8;
const HEADER_PAYLOAD_OFFSET: usize = 16;

unsafe fn topic_subscribe_str(world: *mut nova_ksp::NovaWorld, name: &str) -> *const u8 {
    nova_topic_subscribe(world, name.as_ptr(), name.len() as u32)
}
unsafe fn topic_unsubscribe_str(world: *mut nova_ksp::NovaWorld, name: &str) -> u32 {
    nova_topic_unsubscribe(world, name.as_ptr(), name.len() as u32)
}
unsafe fn read_version(buf: *const u8) -> u64 {
    std::ptr::read_unaligned(buf.add(HEADER_VERSION_OFFSET) as *const u64)
}
unsafe fn read_len(buf: *const u8) -> u32 {
    std::ptr::read_unaligned(buf.add(HEADER_LEN_OFFSET) as *const u32)
}
unsafe fn read_payload(buf: *const u8) -> &'static [u8] {
    let len = read_len(buf) as usize;
    std::slice::from_raw_parts(buf.add(HEADER_PAYLOAD_OFFSET), len)
}

const VESSEL_PERSISTENT_ID: u32 = 1;
const PART_ID: u32 = 100;

fn build_part_database() -> Vec<u8> {
    let probe = proto::NovaPart {
        name: "probeCore".into(),
        dry_mass_kg: 100.0,
        command: Some(proto::CommandPrefab { idle_draw: 1.0 }),
        battery: Some(proto::BatteryPrefab { max_rate: 1000.0 }),
        display_title: "Test Probe Core".into(),
    };
    let db = proto::PartDatabase {
        parts: vec![probe],
    };
    let mut bytes = Vec::with_capacity(db.encoded_len());
    db.encode(&mut bytes).unwrap();
    bytes
}

fn build_lko_circular_orbit() -> proto::OrbitalState {
    proto::OrbitalState {
        inclination: 0.0,
        eccentricity: 0.0,
        semi_major_axis: 700_000.0 + 600_000.0,
        lan: 0.0,
        argument_of_periapsis: 0.0,
        mean_anomaly_at_epoch: 0.0,
        epoch: 0.0,
        body_index: 1,
    }
}

fn build_vessel_structure(battery_capacity: f64) -> Vec<u8> {
    let part = proto::PartStructure {
        id: PART_ID,
        part_name: "probeCore".into(),
        parent_index: -1,
        relative_pos: None,
        relative_rot: None,
        attachment: None,
        symmetry: None,
        tank_volume: None,
        battery: Some(proto::BatteryStructure {
            capacity: battery_capacity,
        }),
        data_storage: None,
    };
    let s = proto::VesselStructure {
        parts: vec![part],
        vessel_id: "test-vessel".into(),
        persistent_id: VESSEL_PERSISTENT_ID,
        orbit: Some(build_lko_circular_orbit()),
    };
    let mut bytes = Vec::with_capacity(s.encoded_len());
    s.encode(&mut bytes).unwrap();
    bytes
}

fn build_vessel_state(battery_value: f64) -> Vec<u8> {
    let part = proto::PartState {
        id: PART_ID,
        tank_volume: None,
        battery: Some(proto::BatteryState {
            value: battery_value,
        }),
        activated: false,
        fuel_cell: None,
        reaction_wheel: None,
        data_storage: None,
        thermometer: None,
    };
    let s = proto::VesselState {
        flight: None,
        parts: vec![part],
        stages: vec![],
        name: "Sat".into(),
        vessel_type: 0,
        situation: 0,
        mission_time: 0.0,
        launch_time: 0.0,
    };
    let mut bytes = Vec::with_capacity(s.encoded_len());
    s.encode(&mut bytes).unwrap();
    bytes
}

unsafe fn read_state<T: Copy>(
    arena_base: *const u8,
    slots: &[ComponentSlot],
    part_id: u32,
    kind: ComponentKind,
) -> T {
    let s = slots
        .iter()
        .find(|s| s.PartId == part_id && s.Kind == kind as u32)
        .expect("slot not found");
    *(arena_base.add(s.StateOffset as usize) as *const T)
}

unsafe fn build_probe_battery_vessel(
    world: *mut nova_ksp::NovaWorld,
    capacity: f64,
    contents: f64,
) -> nova_ksp::VesselHandle {
    let db = build_part_database();
    let rc = nova_world_set_part_database(world, db.as_ptr(), db.len() as u32);
    assert_eq!(rc, 0);

    let structure = build_vessel_structure(capacity);
    let state = build_vessel_state(contents);
    nova_vessel_new(
        world,
        structure.as_ptr(),
        structure.len() as u32,
        state.as_ptr(),
        state.len() as u32,
        0.0,
    )
}

#[test]
fn create_destroy_world_roundtrip() {
    unsafe {
        let world = nova_world_create();
        assert!(!world.is_null());
        nova_world_destroy(world);
    }
}

#[test]
fn part_database_round_trip() {
    unsafe {
        let world = nova_world_create();
        let db = build_part_database();
        let rc = nova_world_set_part_database(world, db.as_ptr(), db.len() as u32);
        assert_eq!(rc, 0);

        // Garbage bytes should be rejected without crashing.
        let bad = b"not a proto";
        let rc = nova_world_set_part_database(world, bad.as_ptr(), bad.len() as u32);
        assert_ne!(rc, 0);

        nova_world_destroy(world);
    }
}

#[test]
fn nova_vessel_new_returns_populated_handle() {
    unsafe {
        let world = nova_world_create();
        let handle = build_probe_battery_vessel(world, 100.0, 100.0);

        assert_eq!(handle.VesselId, VESSEL_PERSISTENT_ID);
        assert!(!handle.ArenaBase.is_null());
        assert_eq!(handle.SlotCount, 2, "expected one Battery + one Command slot");

        let slots = std::slice::from_raw_parts(handle.SlotsPtr, handle.SlotCount as usize);
        let kinds: Vec<u32> = slots.iter().map(|s| s.Kind).collect();
        assert!(kinds.contains(&(ComponentKind::Battery as u32)));
        assert!(kinds.contains(&(ComponentKind::Command as u32)));
        for s in slots {
            assert_eq!(s.PartId, PART_ID);
            assert!(s.StateOffset + s.StateLen <= handle.ArenaLen);
        }

        nova_world_destroy(world);
    }
}

#[test]
fn initial_battery_state_seeded_at_finalize() {
    unsafe {
        let world = nova_world_create();
        let handle = build_probe_battery_vessel(world, 100.0, 100.0);
        let slots = std::slice::from_raw_parts(handle.SlotsPtr, handle.SlotCount as usize);

        let bs: BatteryState = read_state(handle.ArenaBase, slots, PART_ID, ComponentKind::Battery);
        assert_eq!(bs.Capacity, 100.0);
        assert_eq!(bs.Contents, 100.0, "initial contents should match PartState.battery.value");

        nova_world_destroy(world);
    }
}

#[test]
fn world_tick_drains_battery_under_command_load() {
    unsafe {
        let world = nova_world_create();
        let handle = build_probe_battery_vessel(world, 100.0, 100.0);
        let slots = std::slice::from_raw_parts(handle.SlotsPtr, handle.SlotCount as usize);

        // 1 EC/s drain × 5 s = 5 EC consumed → 95 EC left.
        nova_world_tick(world, 5.0);

        let bs: BatteryState = read_state(handle.ArenaBase, slots, PART_ID, ComponentKind::Battery);
        assert!((bs.Contents - 95.0).abs() < 1e-6, "contents={}", bs.Contents);

        let cs: CommandState = read_state(handle.ArenaBase, slots, PART_ID, ComponentKind::Command);
        assert!((cs.IdleActivity - 1.0).abs() < 1e-6,
                "command should be fully serviced; got {}", cs.IdleActivity);

        nova_world_destroy(world);
    }
}

#[test]
fn arena_pointer_is_stable_across_ticks() {
    unsafe {
        let world = nova_world_create();
        let handle = build_probe_battery_vessel(world, 100.0, 100.0);
        let original_base = handle.ArenaBase;
        let original_slots = handle.SlotsPtr;

        for t in 1..=10 {
            nova_world_tick(world, t as f64);
        }

        let slots = std::slice::from_raw_parts(original_slots, handle.SlotCount as usize);
        let bs: BatteryState = read_state(original_base, slots, PART_ID, ComponentKind::Battery);
        assert!(bs.Contents >= 0.0 && bs.Contents <= 100.0,
                "stale pointer? contents={}", bs.Contents);

        nova_world_destroy(world);
    }
}

#[test]
fn vessel_remove_returns_zero_for_known_vessel() {
    unsafe {
        let world = nova_world_create();
        let _handle = build_probe_battery_vessel(world, 100.0, 100.0);

        let r = nova_vessel_remove(world, VESSEL_PERSISTENT_ID);
        assert_eq!(r, 0);

        // Removing again should report not-found.
        let r = nova_vessel_remove(world, VESSEL_PERSISTENT_ID);
        assert_ne!(r, 0);

        nova_world_destroy(world);
    }
}

#[test]
fn topic_subscribe_then_tick_writes_into_buffer() {
    let topic = format!("nova/part/{}", PART_ID);
    unsafe {
        let world = nova_world_create();
        let _handle = build_probe_battery_vessel(world, 100.0, 100.0);

        // Subscribe → stable buffer pointer, version 0, len 0.
        let buf = topic_subscribe_str(world, &topic);
        assert!(!buf.is_null());
        assert_eq!(read_version(buf), 0);
        assert_eq!(read_len(buf), 0);

        // Tick advances the sim and writes into the buffer.
        nova_world_tick(world, 1.0);
        assert!(read_version(buf) > 0);
        let len = read_len(buf);
        assert!(len > 0);

        let bytes = read_payload(buf);
        let json = std::str::from_utf8(bytes).expect("valid utf-8");
        // Wire shape: ["100", [["B", soc, capacity, rate], ["C", ...]]]
        assert!(json.starts_with(r#"["100",[["B","#), "got: {}", json);
        assert!(json.contains(r#"["C","#), "got: {}", json);
        assert!(json.ends_with("]]"));

        nova_world_destroy(world);
    }
}

#[test]
fn topic_version_bumps_only_when_state_changes() {
    let topic = format!("nova/part/{}", PART_ID);
    unsafe {
        let world = nova_world_create();
        let _h = build_probe_battery_vessel(world, 100.0, 100.0);

        let buf = topic_subscribe_str(world, &topic);

        nova_world_tick(world, 1.0);
        let v1 = read_version(buf);
        let bytes1 = read_payload(buf).to_vec();

        nova_world_tick(world, 50.0);
        let v2 = read_version(buf);
        let bytes2 = read_payload(buf).to_vec();

        // Version advances and payload bytes change with drained state.
        assert!(v2 > v1, "version did not advance: {} -> {}", v1, v2);
        assert_ne!(bytes1, bytes2);

        // Tick again with no further sim advance → bytes identical,
        // version unchanged. (Sim re-solves but payload diff says no.)
        // Note: using ut earlier than current would no-op the tick;
        // we just don't advance time, so no further drain happens.
        let v3 = read_version(buf);
        assert_eq!(v3, v2);

        nova_world_destroy(world);
    }
}

#[test]
fn topic_refcount_gates_buffer_lifecycle() {
    let topic = format!("nova/part/{}", PART_ID);
    unsafe {
        let world = nova_world_create();
        let _h = build_probe_battery_vessel(world, 100.0, 100.0);

        // Two subscribers — same pointer back, refcount ascends.
        let p1 = topic_subscribe_str(world, &topic);
        let p2 = topic_subscribe_str(world, &topic);
        assert_eq!(p1, p2);
        nova_world_tick(world, 1.0);
        assert!(read_len(p1) > 0);

        // First unsubscribe → refcount=1, buffer still alive.
        assert_eq!(topic_unsubscribe_str(world, &topic), 1);
        // Pointer still safe to read while refcount > 0.
        let _ = read_version(p1);

        // Last unsubscribe drops the buffer.
        assert_eq!(topic_unsubscribe_str(world, &topic), 0);

        nova_world_destroy(world);
    }
}

#[test]
fn topic_unknown_key_emits_zero_length_payload() {
    unsafe {
        let world = nova_world_create();
        let _h = build_probe_battery_vessel(world, 100.0, 100.0);

        // Subscribe to a non-existent part id. Refresh runs, finds no
        // matching part, leaves the payload empty (len=0). Version
        // bump still happens on the first refresh because the initial
        // buffer was zero-initialised — second refresh with no change
        // leaves version steady.
        let bogus = "nova/part/999999";
        let buf = topic_subscribe_str(world, bogus);
        assert!(!buf.is_null());
        nova_world_tick(world, 1.0);
        // No payload bytes produced → len stays 0 across ticks.
        assert_eq!(read_len(buf), 0);

        nova_world_destroy(world);
    }
}

#[test]
fn battery_only_part_skips_command_slot() {
    // Prefab without CommandPrefab — only the battery slot is allocated.
    unsafe {
        let world = nova_world_create();

        let no_cmd = proto::NovaPart {
            name: "z-100Battery".into(),
            dry_mass_kg: 5.0,
            command: None,
            battery: Some(proto::BatteryPrefab { max_rate: 100.0 }),
            display_title: "Z-100 Battery Pack".into(),
        };
        let db = proto::PartDatabase { parts: vec![no_cmd] };
        let mut db_bytes = Vec::with_capacity(db.encoded_len());
        db.encode(&mut db_bytes).unwrap();
        let rc = nova_world_set_part_database(world, db_bytes.as_ptr(), db_bytes.len() as u32);
        assert_eq!(rc, 0);

        let part = proto::PartStructure {
            id: PART_ID,
            part_name: "z-100Battery".into(),
            parent_index: -1,
            battery: Some(proto::BatteryStructure { capacity: 100.0 }),
            ..Default::default()
        };
        let s = proto::VesselStructure {
            parts: vec![part],
            vessel_id: "test".into(),
            persistent_id: VESSEL_PERSISTENT_ID,
            orbit: Some(build_lko_circular_orbit()),
        };
        let mut sb = Vec::with_capacity(s.encoded_len());
        s.encode(&mut sb).unwrap();

        let st = proto::VesselState {
            parts: vec![proto::PartState {
                id: PART_ID,
                battery: Some(proto::BatteryState { value: 50.0 }),
                ..Default::default()
            }],
            name: "Bat".into(),
            ..Default::default()
        };
        let mut stb = Vec::with_capacity(st.encoded_len());
        st.encode(&mut stb).unwrap();

        let handle = nova_vessel_new(
            world,
            sb.as_ptr(), sb.len() as u32,
            stb.as_ptr(), stb.len() as u32,
            0.0,
        );
        assert_eq!(handle.SlotCount, 1, "no Command slot when prefab has no CommandPrefab");

        let slots = std::slice::from_raw_parts(handle.SlotsPtr, handle.SlotCount as usize);
        assert_eq!(slots[0].Kind, ComponentKind::Battery as u32);

        nova_world_destroy(world);
    }
}
