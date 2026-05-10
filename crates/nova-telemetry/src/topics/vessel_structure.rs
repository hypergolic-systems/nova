//! `nova/vessel-structure/{guid}` — per-vessel part graph.
//!
//! Wire shape (positional):
//!
//! ```text
//! [vesselId, vesselName, [partFrame, ...]]
//! ```
//!
//! Per-part frame:
//!
//! ```text
//! [partId, partName, partTitle, parentId|null, [kindChar, ...]]
//! ```
//!
//! `kindChar` is the same single-character prefix that introduces
//! component frames in `nova/part/{id}` — `"B"` Battery, `"C"`
//! Command, `"F"` FuelCell, `"S"` SolarPanel, `"E"` Engine,
//! `"T"` TankVolume, `"M"` Comms. The list lets a UI view filter
//! parts by which kinds they own ("PowerView wants parts with
//! `B`, `C`, `F`, `S`") without subscribing to every part on the
//! vessel just to read its components.
//!
//! Tag synthesis (the legacy `power-gen` / `power-consume` /
//! `storage` namespace) is gone — a wire-level taxonomy decides
//! presentation choices that should live in the UI. Each view
//! understands the kinds it cares about and filters locally.
//!
//! `key` is the vessel's GUID (KSP `Vessel.id.ToString("D")`). The
//! GUID matches what Dragonglass's `flight.vesselId` emits, so the
//! UI subscribes with `nova/vessel-structure/{flight.vesselId}` and
//! the same string lands here.

use nova_sim::{Component, Part, World};

use crate::frame;

pub fn serialize(world: &World, guid: &str, out: &mut Vec<u8>) {
    let vessel = match world.vessels.iter().find(|v| v.guid == guid) {
        Some(v) => v,
        None => return,
    };

    frame::write_array_open(out);
    let mut first = true;

    frame::write_sep(out, &mut first);
    frame::write_str(out, &vessel.guid);

    frame::write_sep(out, &mut first);
    frame::write_str(out, &vessel.name);

    frame::write_sep(out, &mut first);
    frame::write_array_open(out);
    let mut part_first = true;
    for part in &vessel.parts {
        frame::write_sep(out, &mut part_first);
        write_part(out, part);
    }
    frame::write_array_close(out);

    frame::write_array_close(out);
}

fn write_part(out: &mut Vec<u8>, part: &Part) {
    frame::write_array_open(out);
    let mut first = true;

    frame::write_sep(out, &mut first);
    frame::write_u32_as_string(out, part.id);

    frame::write_sep(out, &mut first);
    frame::write_str(out, &part.name);

    frame::write_sep(out, &mut first);
    let title = if part.display_title.is_empty() {
        &part.name
    } else {
        &part.display_title
    };
    frame::write_str(out, title);

    frame::write_sep(out, &mut first);
    match part.parent {
        Some(pid) => frame::write_u32_as_string(out, pid),
        None => out.extend_from_slice(b"null"),
    }

    frame::write_sep(out, &mut first);
    write_kinds(out, part);

    frame::write_array_close(out);
}

fn write_kinds(out: &mut Vec<u8>, part: &Part) {
    frame::write_array_open(out);
    let mut first = true;
    for c in &part.components {
        let kind = match c {
            Component::Battery(_) => "B",
            Component::Command(_) => "C",
            Component::FuelCell(_) => "F",
            Component::SolarPanel(_) => "S",
            Component::Engine(_) => "E",
            Component::TankVolume(_) => "T",
            Component::Comms(_) => "M",
        };
        frame::write_sep(out, &mut first);
        frame::write_str(out, kind);
    }
    frame::write_array_close(out);
}
