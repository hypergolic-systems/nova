//! End-to-end smoke test: build a CraftFile fixture in Rust, prepend
//! the HGS header, write to a temp file, spawn `nova-save-cli dump`,
//! and verify it exits clean with sensible output.

use prost::Message;
use std::fs;
use std::io::Write;
use std::process::Command;

mod proto {
    #![allow(clippy::all)]
    include!(concat!(env!("OUT_DIR"), "/nova.v1.rs"));
}
use proto::*;

fn write_hgs_fixture(kind: char, payload: &[u8], path: &std::path::Path) {
    let mut f = fs::File::create(path).expect("create fixture");
    f.write_all(b"HGS").unwrap();
    f.write_all(&[kind as u8]).unwrap();
    f.write_all(&2u32.to_le_bytes()).unwrap(); // version 2
    f.write_all(payload).unwrap();
}

fn run_dump(path: &std::path::Path) -> (i32, String, String) {
    let bin = env!("CARGO_BIN_EXE_nova-save-cli");
    let out = Command::new(bin)
        .args(["dump", path.to_str().unwrap()])
        .output()
        .expect("spawn nova-save-cli");
    let code = out.status.code().unwrap_or(-1);
    let stdout = String::from_utf8_lossy(&out.stdout).into_owned();
    let stderr = String::from_utf8_lossy(&out.stderr).into_owned();
    (code, stdout, stderr)
}

#[test]
fn dump_craft_roundtrip() {
    let mut craft = CraftFile::default();
    craft.metadata = Some(CraftMetadata {
        name: "TestRocket".into(),
        description: "A small test craft".into(),
        facility: 1, // VAB
        part_count: 2,
        stage_count: 1,
        total_cost: 1234.0,
        total_mass: 5.5,
        vessel_type: 0,
        thumbnail: vec![],
        size: Some(Vec3 {
            x: 1.0,
            y: 2.0,
            z: 3.0,
        }),
    });
    craft.rotation = Some(Quat {
        x: 0.0,
        y: 0.0,
        z: 0.0,
        w: 1.0,
    });
    let mut vessel = Vessel::default();
    let mut structure = VesselStructure::default();
    structure.vessel_id = "test-id".into();
    structure.persistent_id = 42;
    structure.parts.push(PartStructure {
        id: 0,
        part_name: "mk1pod_v2".into(),
        parent_index: -1,
        relative_pos: Some(Vec3 {
            x: 0.0,
            y: 0.0,
            z: 0.0,
        }),
        relative_rot: None,
        attachment: None,
        symmetry: None,
        tank_volume: None,
        battery: Some(BatteryStructure { capacity: 50.0 }),
        data_storage: None,
        thermometer: None,
    });
    vessel.structure = Some(structure);
    craft.vessel = Some(vessel);

    let payload = craft.encode_to_vec();
    let dir = tempdir();
    let path = dir.join("test.hgc");
    write_hgs_fixture('C', &payload, &path);

    let (code, stdout, stderr) = run_dump(&path);
    assert_eq!(code, 0, "exit code: stdout={stdout}\nstderr={stderr}");
    assert!(stdout.contains("HGS file type='C' version=2"), "stdout={stdout}");
    assert!(stdout.contains("Craft: TestRocket"), "stdout={stdout}");
    assert!(stdout.contains("Facility: VAB"), "stdout={stdout}");
    assert!(stdout.contains("mk1pod_v2"), "stdout={stdout}");
    assert!(stdout.contains("Battery: NaN/50.0"), "stdout={stdout}");
}

#[test]
fn dump_save_roundtrip() {
    let mut save = SaveFile::default();
    save.universal_time = 1234.5;
    save.active_vessel_index = 0;
    save.game = Some(GameMetadata {
        title: "Test Career".into(),
        mode: 2, // career
        seed: 42,
        flag: "Squad/Flags/default".into(),
        launch_id: 7,
        scene: 5,
    });
    save.crew.push(Kerbal {
        name: "Jeb Kerman".into(),
        gender: 0,
        r#trait: "Pilot".into(),
        state: 1, // assigned
        courage: 0.5,
        stupidity: 0.5,
        veteran: true,
        assigned_vessel_id: 100,
        assigned_part_id: 200,
        seat_index: 0,
    });

    let payload = save.encode_to_vec();
    let dir = tempdir();
    let path = dir.join("test.hgs");
    write_hgs_fixture('S', &payload, &path);

    let (code, stdout, stderr) = run_dump(&path);
    assert_eq!(code, 0, "exit code: stdout={stdout}\nstderr={stderr}");
    assert!(stdout.contains("HGS file type='S' version=2"), "stdout={stdout}");
    assert!(stdout.contains("UT: 1234.5s"), "stdout={stdout}");
    assert!(stdout.contains("Test Career (Career)"), "stdout={stdout}");
    assert!(
        stdout.contains("Jeb Kerman (Pilot, state=1) — vessel=100 part=200 seat=0"),
        "stdout={stdout}"
    );
}

#[test]
fn rejects_bad_magic() {
    let dir = tempdir();
    let path = dir.join("garbage.hgs");
    fs::write(&path, b"not a real save").unwrap();

    let (code, _, stderr) = run_dump(&path);
    assert_ne!(code, 0);
    assert!(stderr.contains("Invalid HGS magic"), "stderr={stderr}");
}

// --- temp dir helper (unique per test, cleaned by the OS) ---

fn tempdir() -> std::path::PathBuf {
    let mut dir = std::env::temp_dir();
    let pid = std::process::id();
    let nonce = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .unwrap()
        .as_nanos();
    dir.push(format!("nova-save-cli-test-{pid}-{nonce}"));
    fs::create_dir_all(&dir).unwrap();
    dir
}
