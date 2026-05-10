//! `nova/part/{persistentId}` — per-part snapshot.
//!
//! Wire shape (positional array, matching the legacy
//! `mod/Nova/_legacy/Telemetry/NovaPartTopic.cs:46-54` schema for
//! component frames; the legacy outer `[partId, resources, components]`
//! envelope is *not* preserved — components self-describe their
//! buffer state, so the separate resources slot was redundant):
//!
//! ```text
//! [partId, [componentFrames]]
//! ```
//!
//! Each component on the part contributes one frame, prefixed with a
//! single-char kind tag:
//!
//! - `["B", soc(0..1), capacity, currentRate]` — Battery
//! - `["C", idleRate, testLoadRate, testLoadMaxRate, testLoadActive]` — Command
//! - `["F", currentEcOutput, maxEcOutput, isActive, validUntilSec, manifoldFraction, refillActive]` — FuelCell
//! - `["S", currentEcRate, maxEcRate, deployed, sunlit, retractable]` — SolarPanel
//!
//! Components not yet ported to nova-sim (ReactionWheel, Light) are
//! absent. Components that exist in nova-sim but live on their own
//! topic (Comms) are also absent. Engine and TankVolume are absent
//! pending a schema decision (Engine has its own `nova/engine/{id}`
//! in the legacy code; TankVolume's wire shape is `["T", volume]`
//! and can land later without changing the envelope).
//!
//! `key` is the part's persistent id. Resolved by walking
//! `World.vessels[*].parts[*]` — O(parts) per call. Refresh runs
//! post-sim-tick so the search window is bounded by vessel/part
//! counts (both O(100) in normal play). If no part matches the key,
//! emits nothing (reader sees empty payload).

use nova_sim::{Component, Part, Vessel, World};

use crate::frame;

pub fn serialize(world: &World, key: u32, out: &mut Vec<u8>) {
    let (vessel, part) = match find_part(world, key) {
        Some(pair) => pair,
        None => return,
    };

    frame::write_array_open(out);
    let mut first = true;

    frame::write_sep(out, &mut first);
    frame::write_u32_as_string(out, part.id);

    frame::write_sep(out, &mut first);
    frame::write_array_open(out);
    let mut frame_first = true;
    for c in &part.components {
        write_component_frame(out, c, vessel, &mut frame_first);
    }
    frame::write_array_close(out);

    frame::write_array_close(out);
}

fn find_part(world: &World, key: u32) -> Option<(&Vessel, &Part)> {
    for v in &world.vessels {
        for p in &v.parts {
            if p.id == key {
                return Some((v, p));
            }
        }
    }
    None
}

fn write_component_frame(out: &mut Vec<u8>, c: &Component, vessel: &Vessel, first: &mut bool) {
    match c {
        Component::Battery(b) => write_battery(out, b, vessel, first),
        Component::Command(cmd) => write_command(out, cmd, vessel, first),
        Component::FuelCell(fc) => write_fuel_cell(out, fc, first),
        Component::SolarPanel(sp) => write_solar_panel(out, sp, first),
        // Comms: separate `nova/comms` topic. Engine: separate
        // `nova/engine/{id}`. TankVolume: future `["T", volume]`
        // frame, not in this PR.
        Component::Comms(_) | Component::Engine(_) | Component::TankVolume(_) => {}
    }
}

fn write_battery(out: &mut Vec<u8>, b: &nova_sim::Battery, vessel: &Vessel, first: &mut bool) {
    let bid = match b.buffer_id() {
        Some(id) => id,
        None => return,
    };
    let sys = match vessel.systems.as_ref() {
        Some(s) => s,
        None => return,
    };
    let buf = sys.process.buffer(bid);
    let soc = if buf.capacity > 0.0 {
        buf.contents() / buf.capacity
    } else {
        0.0
    };

    frame::write_sep(out, first);
    frame::write_array_open(out);
    let mut f = true;
    write_kind(out, "B", &mut f);
    write_num(out, soc, &mut f);
    write_num(out, buf.capacity, &mut f);
    write_num(out, buf.rate(), &mut f);
    frame::write_array_close(out);
}

fn write_command(out: &mut Vec<u8>, cmd: &nova_sim::Command, vessel: &Vessel, first: &mut bool) {
    let sys = match vessel.systems.as_ref() {
        Some(s) => s,
        None => return,
    };
    let activity = cmd.idle_activity(sys);
    let idle_rate = cmd.idle_draw * activity;

    frame::write_sep(out, first);
    frame::write_array_open(out);
    let mut f = true;
    write_kind(out, "C", &mut f);
    write_num(out, idle_rate, &mut f);
    // testLoadRate, testLoadMaxRate, testLoadActive — the schema
    // slots are preserved for the future Test-Load machinery (which
    // returns alongside Engine throttle, when the FFI input direction
    // lights up). Emit zeros for now.
    write_num(out, 0.0, &mut f);
    write_num(out, 0.0, &mut f);
    write_bit(out, false, &mut f);
    frame::write_array_close(out);
}

fn write_fuel_cell(out: &mut Vec<u8>, fc: &nova_sim::FuelCell, first: &mut bool) {
    let manifold_fraction = if fc.manifold.capacity > 0.0 {
        fc.manifold.contents() / fc.manifold.capacity
    } else {
        0.0
    };

    frame::write_sep(out, first);
    frame::write_array_open(out);
    let mut f = true;
    write_kind(out, "F", &mut f);
    write_num(out, fc.current_output(), &mut f);
    write_num(out, fc.ec_output, &mut f);
    write_bit(out, fc.is_active, &mut f);
    write_num(out, fc.valid_until_seconds, &mut f);
    write_num(out, manifold_fraction, &mut f);
    write_bit(out, fc.refill_active, &mut f);
    frame::write_array_close(out);
}

fn write_solar_panel(out: &mut Vec<u8>, p: &nova_sim::SolarPanel, first: &mut bool) {
    frame::write_sep(out, first);
    frame::write_array_open(out);
    let mut f = true;
    write_kind(out, "S", &mut f);
    write_num(out, p.current_rate, &mut f);
    write_num(out, p.effective_rate, &mut f);
    write_bit(out, p.is_deployed, &mut f);
    write_bit(out, p.is_sunlit, &mut f);
    write_bit(out, p.is_retractable, &mut f);
    frame::write_array_close(out);
}

fn write_kind(out: &mut Vec<u8>, kind: &str, first: &mut bool) {
    frame::write_sep(out, first);
    frame::write_str(out, kind);
}

fn write_num(out: &mut Vec<u8>, v: f64, first: &mut bool) {
    frame::write_sep(out, first);
    frame::write_f64(out, v);
}

fn write_bit(out: &mut Vec<u8>, v: bool, first: &mut bool) {
    frame::write_sep(out, first);
    frame::write_bool_as_bit(out, v);
}
