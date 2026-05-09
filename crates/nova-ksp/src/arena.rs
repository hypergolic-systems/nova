//! Per-vessel mirror arena. One `Box<[u8]>` sized at finalize time;
//! stable pointers for the vessel's lifetime. C# walks the slot
//! manifest once at vessel-add and indexes the arena by
//! `(part_id, kind) → byte offset`.

use crate::state::{BatteryState, CommandState};

/// Tag for the slot table — the kind of mirror struct stored at a
/// given offset. C# binds the same enum (csbindgen). Keep the
/// discriminants stable: they're written into the arena manifest and
/// read by C# at runtime, so renumbering on either side breaks the
/// world-add path.
#[repr(u32)]
#[derive(Copy, Clone, Debug, PartialEq, Eq)]
pub enum ComponentKind {
    Battery = 1,
    Command = 2,
}

impl ComponentKind {
    pub fn slot_size(self) -> usize {
        match self {
            ComponentKind::Battery => std::mem::size_of::<BatteryState>(),
            ComponentKind::Command => std::mem::size_of::<CommandState>(),
        }
    }

    pub fn slot_align(self) -> usize {
        match self {
            ComponentKind::Battery => std::mem::align_of::<BatteryState>(),
            ComponentKind::Command => std::mem::align_of::<CommandState>(),
        }
    }
}

/// Slot manifest entry: one (part, component-kind) pair points at
/// `StateOffset` bytes into the arena, holding `StateLen` bytes of
/// the kind-specific struct. PascalCase fields per the FFI convention
/// (see `state/mod.rs`).
#[repr(C)]
#[derive(Copy, Clone, Debug)]
#[allow(non_snake_case)]
pub struct ComponentSlot {
    pub PartId: u32,
    pub Kind: u32,
    pub StateOffset: u32,
    pub StateLen: u32,
}

/// Internal allocator for the per-vessel arena. Sizes the buffer up
/// at `finalize` time given the manifest, then hands out stable
/// offsets. We pay one alignment-padding word per slot; slots are
/// emitted in the order they were registered (matches the build
/// order of nova-sim parts/components on the same vessel).
pub(crate) struct ArenaPlan {
    pub slots: Vec<ComponentSlot>,
    pub total_len: u32,
}

impl ArenaPlan {
    pub fn new() -> Self {
        ArenaPlan {
            slots: Vec::new(),
            total_len: 0,
        }
    }

    pub fn allocate(&mut self, part_id: u32, kind: ComponentKind) -> u32 {
        let align = kind.slot_align() as u32;
        let size = kind.slot_size() as u32;
        let aligned = (self.total_len + align - 1) & !(align - 1);
        self.slots.push(ComponentSlot {
            PartId: part_id,
            Kind: kind as u32,
            StateOffset: aligned,
            StateLen: size,
        });
        self.total_len = aligned + size;
        aligned
    }
}
