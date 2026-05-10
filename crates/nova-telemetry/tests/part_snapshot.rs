//! Snapshot test for `nova/part/{persistentId}` wire format.
//!
//! Builds a probe-pod craft (Battery + Command on a single part),
//! drives a world tick, then asserts the JSON output matches the
//! contract documented at the top of `topics/part.rs`.

use nova_sim::components::{Battery, Command, Component};
use nova_sim::fixtures::{ids, kerbol_bodies, kerbol_ctx};
use nova_sim::orbit::OrbitalElements;
use nova_sim::{Vessel, VesselId, World};

use nova_telemetry::topics::part;
use nova_telemetry::TopicRegistry;

const PART_ID: u32 = 42;
const TOPIC_NAME: &str = "nova/part/42";

fn pod_world() -> World {
    let mut v = Vessel::in_orbit(
        VesselId(1),
        "ProbeCore",
        ids::KERBIN,
        OrbitalElements::circular(700_000.0 + 600_000.0),
    );
    v.add_part(
        PART_ID,
        "probe",
        100.0,
        vec![
            // 100 EC battery, 1000 EC/s flow caps so the LP saturates
            // command demand instead of being rate-limited.
            Component::Battery(Battery::new(100.0).with_flow_limits(1000.0, 1000.0)),
            // 1 EC/s avionics — keeps the math obvious.
            Component::Command(Command::new(1.0)),
        ],
    );
    let mut w = World::builder().bodies(kerbol_bodies()).vessel(v).build();
    w.vessels[0].initialize_solver(&kerbol_ctx(), 0.0);
    w
}

#[test]
fn empty_world_emits_nothing_for_unknown_part() {
    let w = World::builder().bodies(kerbol_bodies()).build();
    let mut out = Vec::new();
    part::serialize(&w, PART_ID, &mut out);
    assert!(out.is_empty());
}

#[test]
fn part_snapshot_at_t0_has_full_battery_and_active_command() {
    let mut w = pod_world();
    // Tick to t=1 so the LP solves and rates settle before we read.
    // (At t=0, contents are 100 but rate hasn't been written yet.)
    w.tick(1.0);

    let mut out = Vec::new();
    part::serialize(&w, PART_ID, &mut out);
    let json = std::str::from_utf8(&out).unwrap();

    // ["42", [["B", 0.99, 100, -1], ["C", 1, 0, 0, 0]]]
    //  ^partId  ^battery: soc=99%   ^command: idle=1 EC/s
    //                   capacity=100
    //                   rate=-1 (1 EC/s drain)
    assert!(json.starts_with(r#"["42",[["B","#), "got: {}", json);
    assert!(json.contains(r#"["C",1,0,0,0]"#), "got: {}", json);
    assert!(json.ends_with("]]"));

    // Battery rate is exactly -1 (1 EC/s avionics). Capacity is 100.
    // SoC at t=1 = (100 - 1) / 100 = 0.99.
    assert!(json.contains(",100,"), "expected capacity 100; got: {}", json);
    assert!(json.contains(",-1]"), "expected rate -1; got: {}", json);
}

#[test]
fn battery_drains_visibly_between_snapshots() {
    let mut w = pod_world();

    w.tick(1.0);
    let mut out_a = Vec::new();
    part::serialize(&w, PART_ID, &mut out_a);

    w.tick(50.0);
    let mut out_b = Vec::new();
    part::serialize(&w, PART_ID, &mut out_b);

    // Two distinct snapshots — drain over 49 s changes the SoC slot.
    assert_ne!(out_a, out_b);
    let json_b = std::str::from_utf8(&out_b).unwrap();
    // After 50 s drain at 1 EC/s, contents = 50 → soc = 0.5.
    assert!(
        json_b.contains(r#"["B",0.5,100,-1]"#),
        "expected SoC=0.5 at t=50; got: {}",
        json_b
    );
}

#[test]
fn registry_refresh_populates_payload_with_bumping_version() {
    let mut w = pod_world();
    let mut reg = TopicRegistry::new();

    // Subscribe → stable buffer pointer, version starts at 0.
    let p = reg.subscribe(TOPIC_NAME);
    assert!(!p.is_null());
    let (bytes_init, v_init) = reg.payload(TOPIC_NAME).unwrap();
    assert_eq!(bytes_init, b"");
    assert_eq!(v_init, 0);

    // Tick + refresh with state changing → bytes appear, version > 0.
    w.tick(1.0);
    reg.refresh(&w);
    let (bytes_a, v_a) = reg.payload(TOPIC_NAME).unwrap();
    assert!(bytes_a.starts_with(br#"["42","#));
    assert!(v_a > v_init);
    let bytes_a = bytes_a.to_vec();

    // Tick more + refresh → drained battery, fresh version.
    w.tick(50.0);
    reg.refresh(&w);
    let (bytes_b, v_b) = reg.payload(TOPIC_NAME).unwrap();
    assert!(v_b > v_a);
    assert_ne!(bytes_a, bytes_b);

    // Refresh with no sim change → bytes identical, version unchanged.
    reg.refresh(&w);
    let (_, v_c) = reg.payload(TOPIC_NAME).unwrap();
    assert_eq!(v_c, v_b, "version must not advance when bytes unchanged");

    // Unsubscribe drops the subscription entirely.
    assert_eq!(reg.unsubscribe(TOPIC_NAME), 0);
    assert!(reg.payload(TOPIC_NAME).is_none());
}
