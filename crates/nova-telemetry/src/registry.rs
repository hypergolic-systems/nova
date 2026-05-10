//! Per-topic snapshot registry, keyed by topic name. Refcounted
//! subscriptions; the registry owns each subscription's buffer.
//!
//! ## Shared-buffer wire model
//!
//! Each subscribed topic owns a fixed-size `Box<[u8]>` whose heap
//! address is stable for the topic's lifetime. The buffer is laid
//! out as a small header followed by the JSON payload:
//!
//! ```text
//! offset 0..8   : version (u64, little-endian)
//! offset 8..12  : payload length (u32, little-endian)
//! offset 12..16 : payload capacity (u32, little-endian) — sanity slot
//! offset 16..   : payload bytes
//! ```
//!
//! `subscribe` returns the buffer's base pointer. The consumer
//! (C# via FFI) keeps the pointer for the topic's lifetime and
//! reads:
//!
//! - `*( *const u64 ) base` — version. Polled once per Unity Update;
//!   marks a managed-side topic dirty when it changes.
//! - `*( *const u32 ) (base + 8)` — current payload length.
//! - `(base + 16)..(base + 16 + len)` — payload bytes, ready to
//!   splice into the Dragonglass broadcaster's StringBuilder via
//!   `Json.WriteRaw`.
//!
//! Both Rust writes and C# reads happen on Unity's main thread
//! (writes inside `nova_world_tick` from FixedUpdate, reads inside
//! TopicBroadcaster.Tick from Update). No atomics needed; sequenced
//! by the runtime.
//!
//! `refresh` writes into a per-topic `scratch` Vec, compares to the
//! current payload bytes, and only commits + bumps version when
//! the bytes actually changed. C# polls version-equality and emits
//! nothing on no-change ticks.

use std::collections::HashMap;

use nova_sim::World;

use crate::topics;

/// Total per-topic buffer size, including the 16-byte header. Sized
/// for the largest topic we currently emit (vessel-structure for a
/// ~50-part craft) with comfortable margin. Topics whose serialized
/// payload exceeds the capacity are dropped with `len=0` and a
/// version bump — C# observes "no data" and the schema is still
/// valid, but the wire goes silent for that topic. (TODO: surface
/// an error path / per-topic capacity hint when this becomes a real
/// limit.)
pub const TOPIC_BUFFER_SIZE: usize = 8192;
const HEADER_VERSION_OFFSET: usize = 0;
const HEADER_LEN_OFFSET: usize = 8;
const HEADER_CAP_OFFSET: usize = 12;
const HEADER_SIZE: usize = 16;
const PAYLOAD_CAPACITY: usize = TOPIC_BUFFER_SIZE - HEADER_SIZE;

/// One subscription's state. Owned by `TopicRegistry`; the Box's
/// heap address is stable for the topic's lifetime, so the pointer
/// returned by `subscribe` is valid until the matching `unsubscribe`
/// drops the refcount to 0.
struct TopicState {
    refcount: u32,
    buffer: Box<[u8]>,
    /// Reused per-refresh scratch space. Serializes into here, then
    /// diffs against the live payload before deciding whether to
    /// commit a version bump.
    scratch: Vec<u8>,
}

impl TopicState {
    fn new() -> Self {
        let mut buffer = vec![0u8; TOPIC_BUFFER_SIZE].into_boxed_slice();
        // Stamp the capacity into the header so C# (and tests) can
        // sanity-check buffer layout once at subscribe time without
        // crossing the FFI for it.
        buffer[HEADER_CAP_OFFSET..HEADER_CAP_OFFSET + 4]
            .copy_from_slice(&(PAYLOAD_CAPACITY as u32).to_le_bytes());
        Self {
            refcount: 0,
            buffer,
            scratch: Vec::new(),
        }
    }

    fn version(&self) -> u64 {
        u64::from_le_bytes(self.buffer[HEADER_VERSION_OFFSET..HEADER_VERSION_OFFSET + 8]
            .try_into()
            .unwrap())
    }

    fn set_version(&mut self, v: u64) {
        self.buffer[HEADER_VERSION_OFFSET..HEADER_VERSION_OFFSET + 8]
            .copy_from_slice(&v.to_le_bytes());
    }

    fn len(&self) -> u32 {
        u32::from_le_bytes(self.buffer[HEADER_LEN_OFFSET..HEADER_LEN_OFFSET + 4]
            .try_into()
            .unwrap())
    }

    fn set_len(&mut self, len: u32) {
        self.buffer[HEADER_LEN_OFFSET..HEADER_LEN_OFFSET + 4]
            .copy_from_slice(&len.to_le_bytes());
    }

    fn current_payload(&self) -> &[u8] {
        let len = self.len() as usize;
        &self.buffer[HEADER_SIZE..HEADER_SIZE + len]
    }
}

#[derive(Default)]
pub struct TopicRegistry {
    states: HashMap<String, TopicState>,
}

impl TopicRegistry {
    pub fn new() -> Self {
        Self::default()
    }

    /// Add a subscription. Returns the stable pointer to the
    /// topic's buffer (or `None` if the name is empty). First
    /// subscriber for `name` allocates the buffer; subsequent
    /// subscribers get the same pointer and just bump the refcount.
    pub fn subscribe(&mut self, name: &str) -> *const u8 {
        if name.is_empty() {
            return std::ptr::null();
        }
        let state = self
            .states
            .entry(name.to_owned())
            .or_insert_with(TopicState::new);
        state.refcount += 1;
        state.buffer.as_ptr()
    }

    /// Drop a subscription. Returns the remaining refcount. When it
    /// hits 0 the entry is removed and the buffer freed — the
    /// pointer previously returned to the caller is no longer valid.
    pub fn unsubscribe(&mut self, name: &str) -> u32 {
        let remaining = match self.states.get_mut(name) {
            None => return 0,
            Some(state) => {
                state.refcount = state.refcount.saturating_sub(1);
                state.refcount
            }
        };
        if remaining == 0 {
            self.states.remove(name);
        }
        remaining
    }

    /// Read the latest payload bytes + version for `name`. Returns
    /// `None` if the topic isn't subscribed. Used by tests; the
    /// production reader path is direct memory access through the
    /// pointer `subscribe` returned.
    pub fn payload(&self, name: &str) -> Option<(&[u8], u64)> {
        let state = self.states.get(name)?;
        Some((state.current_payload(), state.version()))
    }

    /// Diagnostic — number of active subscriptions.
    pub fn subscription_count(&self) -> usize {
        self.states.len()
    }

    /// Re-serialize every subscribed topic. Called once per
    /// `nova_world_tick` post-sim-tick. Diffs the new bytes against
    /// the current buffer payload; commits + bumps version only on
    /// change. Topics whose new payload exceeds `PAYLOAD_CAPACITY`
    /// emit `len=0` with a version bump (consumers see "no data"
    /// and can log).
    pub fn refresh(&mut self, world: &World) {
        for (name, state) in self.states.iter_mut() {
            state.scratch.clear();
            topics::serialize(name, world, &mut state.scratch);

            let scratch_len = state.scratch.len();
            // Confine the scratch + payload borrows to this single
            // expression so the subsequent mutable accesses to
            // version / len / buffer don't conflict.
            let unchanged = state.scratch.as_slice() == state.current_payload();
            if unchanged {
                continue;
            }

            let v = state.version().wrapping_add(1);
            state.set_version(v);

            if scratch_len > PAYLOAD_CAPACITY {
                // TODO: fallback path for oversize topics. For now
                // emit a zero-length payload so the wire envelope
                // stays valid; the version bump makes C# notice.
                state.set_len(0);
                continue;
            }

            // Copy scratch → payload region. SAFETY: scratch is a
            // Vec and buffer is a Box<[u8]>; their backing storage
            // is disjoint. Borrowck can't see through the struct
            // field projection so we drop to raw ptr.
            unsafe {
                let src = state.scratch.as_ptr();
                let dst = state.buffer.as_mut_ptr().add(HEADER_SIZE);
                std::ptr::copy_nonoverlapping(src, dst, scratch_len);
            }
            // Zero any tail left from a previous longer payload so
            // a curious reader past `len` sees zeros, not stale bytes.
            for i in HEADER_SIZE + scratch_len..HEADER_SIZE + PAYLOAD_CAPACITY {
                state.buffer[i] = 0;
            }
            state.set_len(scratch_len as u32);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn subscribe_increments_refcount_and_returns_stable_pointer() {
        let mut r = TopicRegistry::new();
        let p1 = r.subscribe("nova/part/1");
        assert!(!p1.is_null());
        let p2 = r.subscribe("nova/part/1");
        // Same name → same pointer.
        assert_eq!(p1, p2);
        let p3 = r.subscribe("nova/part/2");
        assert_ne!(p1, p3);
        assert_eq!(r.subscription_count(), 2);
    }

    #[test]
    fn unsubscribe_drops_to_zero_and_removes() {
        let mut r = TopicRegistry::new();
        r.subscribe("nova/part/1");
        r.subscribe("nova/part/1");
        assert_eq!(r.unsubscribe("nova/part/1"), 1);
        assert_eq!(r.unsubscribe("nova/part/1"), 0);
        assert_eq!(r.subscription_count(), 0);
    }

    #[test]
    fn unsubscribe_unknown_returns_zero() {
        let mut r = TopicRegistry::new();
        assert_eq!(r.unsubscribe("nova/part/9"), 0);
    }

    #[test]
    fn payload_starts_empty_with_version_zero() {
        let mut r = TopicRegistry::new();
        r.subscribe("nova/part/1");
        let (bytes, v) = r.payload("nova/part/1").unwrap();
        assert_eq!(bytes, b"");
        assert_eq!(v, 0);
    }

    #[test]
    fn header_layout_constants_are_what_csharp_expects() {
        // C# reads version at +0, len at +8, payload at +16.
        assert_eq!(HEADER_VERSION_OFFSET, 0);
        assert_eq!(HEADER_LEN_OFFSET, 8);
        assert_eq!(HEADER_SIZE, 16);
    }
}
