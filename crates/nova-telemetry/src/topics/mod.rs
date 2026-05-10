//! Topic name → serializer dispatch.
//!
//! Topic names are wire-level identifiers; the registry keys the
//! snapshot buffers by name verbatim. This module owns the parse
//! that maps a name to its per-kind serializer:
//!
//! - `nova/part/{persistentId}` → `part::serialize`
//! - `nova/vessel-structure/{guid}` → `vessel_structure::serialize`
//!
//! Adding a new topic family = one new prefix branch here + a
//! sibling module.

use nova_sim::World;

pub mod part;
pub mod vessel_structure;

const PART_PREFIX: &str = "nova/part/";
const VESSEL_STRUCTURE_PREFIX: &str = "nova/vessel-structure/";

/// Serialize the snapshot for `name` into `out`. `out` is the
/// caller's pre-cleared buffer. Unknown / malformed names emit
/// nothing — readers see empty bytes and skip the broadcast.
pub fn serialize(name: &str, world: &World, out: &mut Vec<u8>) {
    if let Some(rest) = name.strip_prefix(PART_PREFIX) {
        if let Ok(part_id) = rest.parse::<u32>() {
            part::serialize(world, part_id, out);
        }
        return;
    }
    if let Some(guid) = name.strip_prefix(VESSEL_STRUCTURE_PREFIX) {
        vessel_structure::serialize(world, guid, out);
        return;
    }
    // Unknown prefix — leave `out` empty.
}
