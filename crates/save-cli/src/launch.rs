//! `launch` subcommand — inject a craft (.nvc) into a save (.nvs) as a
//! vessel parked at a user-specified orbit.
//!
//! Replicates the proto-side outcome of the in-game launch path
//! (`NovaSaveBuilder.BuildVessel` + `BuildFlightState`) against on-disk
//! files, without booting KSP.
//!
//! Per-part state is copied verbatim from the craft — every
//! `VirtualComponent.Load(PartState)` override is null-guarded, so unset
//! sub-messages keep the structure-derived defaults (full tanks, full
//! batteries, etc.) that `LoadStructure` installs.

use anyhow::{anyhow, bail, Context, Result};
use clap::{Args, Subcommand};
use prost::Message;
use sha2::{Digest, Sha256};
use std::collections::HashMap;
use std::fs;
use std::path::PathBuf;
use uuid::Uuid;

use crate::io::{read_craft_file, read_save_file, write_save_file};
use crate::proto::*;

const SITUATION_ORBITING: i32 = 32; // Vessel.Situations.ORBITING

#[derive(Subcommand)]
pub enum LaunchCmd {
    /// Inject a craft at a circular orbit. SMA derived from body radius +
    /// altitude; eccentricity, AoP, MNA are zero; epoch = save UT.
    /// Body identified by stock name only (--body Kerbin, etc.); modded
    /// planet packs go through `keplerian` with `--body-index`.
    Circular(CircularArgs),
    /// Inject a craft at a fully-specified Keplerian orbit.
    Keplerian(KeplerianArgs),
}

#[derive(Args)]
pub struct CommonArgs {
    /// Save file (.nvs) to modify in place.
    #[arg(long)]
    save: PathBuf,
    /// Craft file (.nvc) to inject.
    #[arg(long)]
    craft: PathBuf,
    /// Override the vessel display name. Defaults to the craft's
    /// CraftMetadata.name.
    #[arg(long)]
    name: Option<String>,
    /// Override the vessel type (VesselType enum). Defaults to the
    /// craft's CraftMetadata.vessel_type.
    #[arg(long)]
    vessel_type: Option<i32>,
    /// Before overwriting, copy the existing .nvs to <save>.nvs.bak.
    #[arg(long)]
    backup: bool,
}

#[derive(Args)]
pub struct CircularArgs {
    #[command(flatten)]
    common: CommonArgs,
    /// Altitude above body sea level, metres.
    #[arg(long)]
    altitude: f64,
    /// Stock body name (Kerbin, Mun, Minmus, ...). Required so we can
    /// look up the equatorial radius.
    #[arg(long)]
    body: String,
    /// Inclination in degrees. Defaults to 0 (equatorial prograde).
    #[arg(long, default_value_t = 0.0)]
    inclination: f64,
    /// Longitude of ascending node, degrees. Defaults to 0.
    #[arg(long, default_value_t = 0.0)]
    lan: f64,
}

#[derive(Args)]
pub struct KeplerianArgs {
    #[command(flatten)]
    common: CommonArgs,
    /// Semi-major axis, metres.
    #[arg(long)]
    sma: f64,
    /// Eccentricity.
    #[arg(long)]
    ecc: f64,
    /// Inclination, degrees.
    #[arg(long)]
    inc: f64,
    /// Longitude of ascending node, degrees.
    #[arg(long)]
    lan: f64,
    /// Argument of periapsis, degrees.
    #[arg(long)]
    aop: f64,
    /// Mean anomaly at epoch, radians (stock KSP units).
    #[arg(long)]
    mna: f64,
    /// Epoch UT. Defaults to the save's universal_time.
    #[arg(long)]
    epoch: Option<f64>,
    /// FlightGlobals.Bodies index. Stock: Sun=0, Kerbin=1, Mun=2, ...
    #[arg(long)]
    body_index: i32,
}

pub fn run(cmd: LaunchCmd) -> Result<()> {
    match cmd {
        LaunchCmd::Circular(args) => {
            let body = lookup_body(&args.body)?;
            let common = args.common;
            launch(common, |save_ut| OrbitalState {
                inclination: args.inclination.to_radians(),
                eccentricity: 0.0,
                semi_major_axis: body.radius_m + args.altitude,
                lan: args.lan.to_radians(),
                argument_of_periapsis: 0.0,
                mean_anomaly_at_epoch: 0.0,
                epoch: save_ut,
                body_index: body.index,
            })
        }
        LaunchCmd::Keplerian(args) => {
            let common = args.common;
            launch(common, |save_ut| OrbitalState {
                inclination: args.inc.to_radians(),
                eccentricity: args.ecc,
                semi_major_axis: args.sma,
                lan: args.lan.to_radians(),
                argument_of_periapsis: args.aop.to_radians(),
                mean_anomaly_at_epoch: args.mna,
                epoch: args.epoch.unwrap_or(save_ut),
                body_index: args.body_index,
            })
        }
    }
}

fn launch(common: CommonArgs, mk_orbit: impl FnOnce(f64) -> OrbitalState) -> Result<()> {
    let craft = read_craft_file(&common.craft)?;
    let mut save = read_save_file(&common.save)?;

    let craft_vessel = craft
        .vessel
        .ok_or_else(|| anyhow!("craft file has no vessel"))?;
    let mut structure = craft_vessel
        .structure
        .ok_or_else(|| anyhow!("craft has no VesselStructure"))?;
    let mut state = craft_vessel.state.unwrap_or_default();

    let orbit = mk_orbit(save.universal_time);

    // ID allocation — no collisions with anything already in the save.
    let (next_vessel_id, next_part_id) = scan_max_ids(&save);
    let new_persistent_id = next_vessel_id;
    let part_id_map = build_part_id_map(&structure, next_part_id);

    // Identity
    structure.vessel_id = Uuid::new_v4().to_string();
    structure.persistent_id = new_persistent_id;
    remap_structure_part_ids(&mut structure, &part_id_map);
    remap_state_part_ids(&mut state, &part_id_map);

    // Scalars that BuildVessel writes from the live KSP Vessel
    let craft_meta = craft.metadata.unwrap_or_default();
    state.name = common.name.unwrap_or_else(|| craft_meta.name.clone());
    state.vessel_type = common.vessel_type.unwrap_or(craft_meta.vessel_type);
    state.situation = SITUATION_ORBITING;
    state.mission_time = 0.0;
    state.launch_time = save.universal_time;

    // FlightState — orbit only; leave position = None so the loader's
    // orbitDriver.updateFromParameters() derives lat/lon/alt from orbit
    // at load time (NovaSaveLoader.cs:200-212 + 216 guard).
    state.flight = Some(FlightState {
        orbit: Some(orbit),
        position: None,
        action_groups: 0,
        main_throttle: 0.0,
    });

    // Mirrors NovaVesselBuilder.ComputeStructureHash.
    let structure_hash = Sha256::digest(structure.encode_to_vec()).to_vec();

    save.vessels.push(Vessel {
        structure: Some(structure),
        state: Some(state),
        structure_hash,
    });

    if common.backup {
        let bak = with_suffix(&common.save, ".bak");
        fs::copy(&common.save, &bak)
            .with_context(|| format!("backup {} → {}", common.save.display(), bak.display()))?;
    }

    write_save_file(&common.save, &save)?;
    println!(
        "Injected vessel '{}' (persistentId={}) into {} — {} vessel(s) total.",
        save.vessels.last().unwrap().state.as_ref().unwrap().name,
        new_persistent_id,
        common.save.display(),
        save.vessels.len()
    );
    Ok(())
}

fn scan_max_ids(save: &SaveFile) -> (u32, u32) {
    let mut max_vessel = 0u32;
    let mut max_part = 0u32;
    for v in &save.vessels {
        if let Some(s) = &v.structure {
            if s.persistent_id > max_vessel {
                max_vessel = s.persistent_id;
            }
            for p in &s.parts {
                if p.id > max_part {
                    max_part = p.id;
                }
            }
        }
    }
    (max_vessel + 1, max_part + 1)
}

fn build_part_id_map(structure: &VesselStructure, mut next_id: u32) -> HashMap<u32, u32> {
    let mut map = HashMap::with_capacity(structure.parts.len());
    for p in &structure.parts {
        map.insert(p.id, next_id);
        next_id += 1;
    }
    map
}

fn remap_structure_part_ids(structure: &mut VesselStructure, map: &HashMap<u32, u32>) {
    for part in &mut structure.parts {
        if let Some(&new_id) = map.get(&part.id) {
            part.id = new_id;
        }
        if let Some(sym) = &mut part.symmetry {
            for partner in &mut sym.partners {
                if let Some(&new_id) = map.get(partner) {
                    *partner = new_id;
                }
            }
        }
    }
}

fn remap_state_part_ids(state: &mut VesselState, map: &HashMap<u32, u32>) {
    for ps in &mut state.parts {
        if let Some(&new_id) = map.get(&ps.id) {
            ps.id = new_id;
        }
    }
    for stage in &mut state.stages {
        for id in &mut stage.part_ids {
            if let Some(&new_id) = map.get(id) {
                *id = new_id;
            }
        }
    }
}

fn with_suffix(path: &std::path::Path, suffix: &str) -> PathBuf {
    let mut s = path.as_os_str().to_owned();
    s.push(suffix);
    PathBuf::from(s)
}

// --- Stock-Kerbol body table ----------------------------------------------

struct StockBody {
    index: i32,
    radius_m: f64,
}

fn lookup_body(name: &str) -> Result<StockBody> {
    // Equatorial radii match KSP 1.12 stock CelestialBody.Radius.
    let n = name.to_ascii_lowercase();
    let (index, radius_m) = match n.as_str() {
        "sun" | "kerbol" => (0, 261_600_000.0),
        "kerbin" => (1, 600_000.0),
        "mun" => (2, 200_000.0),
        "minmus" => (3, 60_000.0),
        "moho" => (4, 250_000.0),
        "eve" => (5, 700_000.0),
        "duna" => (6, 320_000.0),
        "ike" => (7, 130_000.0),
        "jool" => (8, 6_000_000.0),
        "laythe" => (9, 500_000.0),
        "vall" => (10, 300_000.0),
        "tylo" => (11, 600_000.0),
        "bop" => (12, 65_000.0),
        "pol" => (13, 44_000.0),
        "dres" => (14, 138_000.0),
        "eeloo" => (15, 210_000.0),
        "gilly" => (16, 13_000.0),
        _ => bail!(
            "unknown stock body '{name}' — pass --body-index N with the keplerian subcommand for modded systems"
        ),
    };
    Ok(StockBody { index, radius_m })
}
