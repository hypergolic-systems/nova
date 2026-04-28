//! `dump` subcommand — reads the 8-byte HGS header, decodes the proto
//! payload (CraftFile or SaveFile), and writes a structured text dump.
//!
//! Wire format mirrors `mod/Nova.Core/Persistence/NovaFileFormat.cs`:
//!   bytes 0..3: 'H' 'G' 'S'
//!   byte 3:     'C' (craft) | 'S' (save)
//!   bytes 4..8: u32 LE version
//!   bytes 8..:  prost-encoded CraftFile or SaveFile

use anyhow::{bail, Context, Result};
use prost::Message;
use std::fs::File;
use std::io::Read;
use std::path::Path;

use crate::proto::*;

const MAGIC: [u8; 3] = *b"HGS";

pub fn dump(path: &Path) -> Result<()> {
    let mut file = File::open(path).with_context(|| format!("open {}", path.display()))?;
    let mut header = [0u8; 8];
    file.read_exact(&mut header).context("read HGS header")?;

    if header[..3] != MAGIC {
        bail!(
            "Invalid HGS magic: {:?}",
            String::from_utf8_lossy(&header[..3])
        );
    }
    let kind = header[3] as char;
    let version = u32::from_le_bytes([header[4], header[5], header[6], header[7]]);

    println!("HGS file type='{kind}' version={version}");

    let mut payload = Vec::new();
    file.read_to_end(&mut payload).context("read payload")?;

    match kind {
        'C' => {
            let craft = CraftFile::decode(payload.as_slice()).context("decode CraftFile")?;
            dump_craft(&craft);
        }
        'S' => {
            let save = SaveFile::decode(payload.as_slice()).context("decode SaveFile")?;
            dump_save(&save);
        }
        other => bail!("Unknown HGS file type: '{other}'"),
    }
    Ok(())
}

// --- Top-level dumpers -----------------------------------------------------

fn dump_craft(craft: &CraftFile) {
    if let Some(meta) = &craft.metadata {
        println!("Craft: {}", meta.name);
        if !meta.description.is_empty() {
            println!("Description: {}", meta.description);
        }
        let facility = match meta.facility {
            1 => "VAB".to_string(),
            2 => "SPH".to_string(),
            n => format!("Unknown({n})"),
        };
        println!(
            "Facility: {}, VesselType: {}",
            facility, meta.vessel_type
        );
        println!("Parts: {}, Stages: {}", meta.part_count, meta.stage_count);
        println!("Cost: {:.0}, Mass: {:.3}t", meta.total_cost, meta.total_mass);
        println!("Thumbnail: {} bytes", meta.thumbnail.len());
    }
    if let Some(rot) = &craft.rotation {
        println!(
            "Rotation: ({:.3}, {:.3}, {:.3}, {:.3})",
            rot.x, rot.y, rot.z, rot.w
        );
    }
    println!();
    if let Some(vessel) = &craft.vessel {
        dump_vessel(vessel, 0);
    } else {
        println!("(no vessel)");
    }
}

fn dump_save(save: &SaveFile) {
    println!(
        "Save File — UT: {:.1}s, Vessels: {}, Active: {}",
        save.universal_time,
        save.vessels.len(),
        save.active_vessel_index
    );
    if let Some(g) = &save.game {
        let mode = match g.mode {
            0 => "Sandbox".to_string(),
            1 => "Science".to_string(),
            2 => "Career".to_string(),
            n => format!("Unknown({n})"),
        };
        println!(
            "Game: {} ({}), Seed: {}, LaunchID: {}",
            g.title, mode, g.seed, g.launch_id
        );
    }
    if !save.crew.is_empty() {
        println!("Crew ({}):", save.crew.len());
        for k in &save.crew {
            let assignment = if k.assigned_vessel_id != 0 {
                format!(
                    "vessel={} part={} seat={}",
                    k.assigned_vessel_id, k.assigned_part_id, k.seat_index
                )
            } else {
                "unassigned".to_string()
            };
            println!(
                "  {} ({}, state={}) — {}",
                k.name, k.r#trait, k.state, assignment
            );
        }
    }
    println!();
    for (i, vessel) in save.vessels.iter().enumerate() {
        println!("--- Vessel {i} ---");
        dump_vessel(vessel, 0);
        println!();
    }
}

// --- Vessel / part walk ----------------------------------------------------

fn dump_vessel(vessel: &Vessel, indent: usize) {
    let p = pad(indent);
    let p2 = pad(indent + 2);

    if let Some(structure) = &vessel.structure {
        if !structure.vessel_id.is_empty() {
            println!(
                "{p}VesselId: {}, PersistentId: {}",
                structure.vessel_id, structure.persistent_id
            );
        }
        if !vessel.structure_hash.is_empty() {
            println!("{p}StructureHash: {}", hex(&vessel.structure_hash));
        }

        if let Some(state) = &vessel.state {
            if !state.name.is_empty() {
                println!(
                    "{p}Name: {}, Type: {}, Situation: {}",
                    state.name, state.vessel_type, state.situation
                );
            }
            if state.mission_time != 0.0 || state.launch_time != 0.0 {
                println!(
                    "{p}MissionTime: {:.1}s, LaunchTime: {:.1}s",
                    state.mission_time, state.launch_time
                );
            }
            if let Some(flight) = &state.flight {
                dump_flight(flight, indent);
            }
            if !state.stages.is_empty() {
                println!("{p}Stages ({}):", state.stages.len());
                for (s, stage) in state.stages.iter().enumerate() {
                    let ids: Vec<String> = stage.part_ids.iter().map(|id| id.to_string()).collect();
                    println!("{p2}[{s}] {}", ids.join(", "));
                }
            }
        }

        println!("{p}Parts ({}):", structure.parts.len());
        let part_state_lookup: Vec<&PartState> = vessel
            .state
            .as_ref()
            .map(|s| s.parts.iter().collect())
            .unwrap_or_default();
        for (i, part) in structure.parts.iter().enumerate() {
            let state = part_state_lookup.get(i).copied();
            dump_part(part, state, indent + 2);
        }
    } else {
        println!("{p}(no structure)");
    }
}

fn dump_flight(flight: &FlightState, indent: usize) {
    let p = pad(indent);
    if let Some(o) = &flight.orbit {
        println!(
            "{p}Orbit: body={} sma={:.0} ecc={:.4} inc={:.2}",
            o.body_index, o.semi_major_axis, o.eccentricity, o.inclination
        );
    }
    if let Some(pos) = &flight.position {
        println!(
            "{p}Position: ({:.4}, {:.4}) alt={:.0}m hat={:.0}m",
            pos.latitude, pos.longitude, pos.altitude, pos.height_above_terrain
        );
        if let Some(r) = &pos.rotation {
            println!(
                "{p}SrfRot: ({:.6}, {:.6}, {:.6}, {:.6})",
                r.x, r.y, r.z, r.w
            );
        }
        if let Some(v) = &pos.velocity {
            println!("{p}Velocity: ({:.1}, {:.1}, {:.1})", v.x, v.y, v.z);
        }
    }
    println!("{p}ActionGroups: 0x{:X}", flight.action_groups);
}

fn dump_part(part: &PartStructure, state: Option<&PartState>, indent: usize) {
    let p0 = pad(indent);
    let p2 = pad(indent + 2);
    let p4 = pad(indent + 4);

    let parent = if part.parent_index >= 0 {
        format!("parent={}", part.parent_index)
    } else {
        "root".to_string()
    };
    println!("{p0}[{}] {} ({parent})", part.id, part.part_name);

    if let Some(rp) = &part.relative_pos {
        println!("{p2}RelPos: ({:.3}, {:.3}, {:.3})", rp.x, rp.y, rp.z);
    }
    if let Some(r) = &part.relative_rot {
        println!(
            "{p2}RelRot: ({:.6}, {:.6}, {:.6}, {:.6})",
            r.x, r.y, r.z, r.w
        );
    }

    if let Some(a) = &part.attachment {
        let mut attach = if a.parent_node_index >= 0 {
            format!("stack p={} c={}", a.parent_node_index, a.child_node_index)
        } else {
            "surface".to_string()
        };
        if let Some(sp) = &a.srf_attach_pos {
            attach.push_str(&format!(" pos=({:.2},{:.2},{:.2})", sp.x, sp.y, sp.z));
        }
        println!("{p2}Attach: {attach}");
    }

    if let Some(s) = &part.symmetry {
        let partners: Vec<String> = s.partners.iter().map(|p| p.to_string()).collect();
        let mode = if s.mirror.is_some() { "mirror" } else { "radial" };
        println!("{p2}Symmetry: {mode} partners=[{}]", partners.join(", "));
    }

    if let Some(tv) = &part.tank_volume {
        println!(
            "{p2}TankVolume: {:.1}L, {} tank(s)",
            tv.volume,
            tv.tanks.len()
        );
        let amounts = state
            .and_then(|s| s.tank_volume.as_ref())
            .map(|t| t.amounts.as_slice())
            .unwrap_or(&[]);
        for (t, tank) in tv.tanks.iter().enumerate() {
            let amount = amounts.get(t).copied().unwrap_or(f64::NAN);
            println!(
                "{p4}{}: {:.1}/{:.1} (out={:.1}, in={:.1})",
                tank.resource, amount, tank.capacity, tank.max_rate_out, tank.max_rate_in
            );
        }
    }

    if let Some(b) = &part.battery {
        let value = state
            .and_then(|s| s.battery.as_ref())
            .map(|x| x.value)
            .unwrap_or(f64::NAN);
        println!("{p2}Battery: {:.1}/{:.1}", value, b.capacity);
    }

    if let Some(fc) = state.and_then(|s| s.fuel_cell.as_ref()) {
        println!(
            "{p2}FuelCell: lh2={:.4}L, lox={:.4}L (active={}, refill={})",
            fc.lh2_manifold_contents, fc.lox_manifold_contents, fc.is_active, fc.refill_active
        );
    }
}

// --- Helpers ---------------------------------------------------------------

fn pad(n: usize) -> String {
    " ".repeat(n)
}

fn hex(bytes: &[u8]) -> String {
    let mut s = String::with_capacity(bytes.len() * 2);
    for b in bytes {
        s.push_str(&format!("{b:02X}"));
    }
    s
}

