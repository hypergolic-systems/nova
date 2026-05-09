//! Communications jobs — Packet (one-shot transfer),
//! `Broadcast<K>` / `Receive<K>` (typed pub/sub). Mirrors
//! `mod/Nova.Core/Communications/{Job,Packet,Broadcast,Receive}.cs`
//! but homogeneous over a single `Job` enum to avoid trait-object
//! bookkeeping.
//!
//! C# uses `Type` reflection to match `Broadcast<K>` and `Receive<K>`.
//! The Rust port hashes `(TypeId, key)` once at construction into a
//! `TopicKey`; matching is plain `==`. Hash collisions across distinct
//! keys would mismatch broadcasts to wrong receivers — for sub-1k
//! topic counts that's negligible. The `(TypeId, u64)` tuple matches
//! C# `(KeyType, KeyAsObject)` modulo collision.

use std::any::TypeId;
use std::collections::hash_map::DefaultHasher;
use std::hash::{Hash, Hasher};

use super::endpoint::EndpointId;

/// Stable identifier for a job. `CommsSystem` mints these via a
/// monotonic counter starting at 1.
#[derive(Copy, Clone, Debug, PartialEq, Eq, Hash, PartialOrd, Ord)]
pub struct JobId(pub u64);

#[derive(Copy, Clone, Debug, PartialEq, Eq)]
pub enum JobStatus {
    Active,
    Completed,
    Cancelled,
}

/// Type-erased pub/sub topic key. Matching is `==` on both fields.
#[derive(Copy, Clone, Debug, PartialEq, Eq, Hash)]
pub struct TopicKey {
    pub type_id: TypeId,
    pub key_hash: u64,
}

impl TopicKey {
    pub fn of<K: Hash + 'static>(key: &K) -> Self {
        let mut h = DefaultHasher::new();
        key.hash(&mut h);
        TopicKey { type_id: TypeId::of::<K>(), key_hash: h.finish() }
    }
}

/// A communications job. Heterogeneous shapes share a single enum so
/// the network can hold them in a `Vec<Job>` and iterate cheaply.
#[derive(Clone, Debug)]
pub enum Job {
    /// One-shot byte transfer between two endpoints. Routes via the
    /// max-rate path through the current graph; `status` flips to
    /// `Completed` once `delivered_bytes` reaches `total_bytes`.
    Packet {
        id: JobId,
        source: EndpointId,
        dest: EndpointId,
        total_bytes: u64,
        delivered_bytes: u64,
        allocated_rate_bps: f64,
        status: JobStatus,
    },
    /// Typed pub/sub source. The network divides `target_rate_bps`
    /// among every matching `Receive` (any source → any receiver
    /// with the same `topic`); each receiver's slice is further
    /// capped by its own `max_rate_bps` and by path bandwidth.
    Broadcast {
        id: JobId,
        source: EndpointId,
        topic: TopicKey,
        target_rate_bps: f64,
        bytes_sent: u64,
        allocated_rate_bps: f64,
        status: JobStatus,
    },
    /// Typed pub/sub sink. Pulls from any active `Broadcast` whose
    /// `topic` matches; rate capped by `max_rate_bps`, by the
    /// broadcast's `target_rate_bps` share, and by path bandwidth
    /// from each broadcast source to this receiver.
    Receive {
        id: JobId,
        receiver: EndpointId,
        topic: TopicKey,
        max_rate_bps: f64,
        bytes_received: u64,
        allocated_rate_bps: f64,
        status: JobStatus,
    },
}

impl Job {
    /// Create a Packet job. `id` is `JobId(0)`; `CommsSystem::submit`
    /// rewrites it to the next minted id.
    pub fn packet(source: EndpointId, dest: EndpointId, total_bytes: u64) -> Self {
        Job::Packet {
            id: JobId(0),
            source,
            dest,
            total_bytes,
            delivered_bytes: 0,
            allocated_rate_bps: 0.0,
            status: JobStatus::Active,
        }
    }

    pub fn broadcast<K: Hash + 'static>(
        source: EndpointId,
        key: &K,
        target_rate_bps: f64,
    ) -> Self {
        Job::Broadcast {
            id: JobId(0),
            source,
            topic: TopicKey::of(key),
            target_rate_bps,
            bytes_sent: 0,
            allocated_rate_bps: 0.0,
            status: JobStatus::Active,
        }
    }

    pub fn receive<K: Hash + 'static>(
        receiver: EndpointId,
        key: &K,
        max_rate_bps: f64,
    ) -> Self {
        Job::Receive {
            id: JobId(0),
            receiver,
            topic: TopicKey::of(key),
            max_rate_bps,
            bytes_received: 0,
            allocated_rate_bps: 0.0,
            status: JobStatus::Active,
        }
    }

    pub fn id(&self) -> JobId {
        match self {
            Job::Packet { id, .. } | Job::Broadcast { id, .. } | Job::Receive { id, .. } => *id,
        }
    }

    pub fn status(&self) -> JobStatus {
        match self {
            Job::Packet { status, .. }
            | Job::Broadcast { status, .. }
            | Job::Receive { status, .. } => *status,
        }
    }

    pub fn allocated_rate_bps(&self) -> f64 {
        match self {
            Job::Packet { allocated_rate_bps, .. }
            | Job::Broadcast { allocated_rate_bps, .. }
            | Job::Receive { allocated_rate_bps, .. } => *allocated_rate_bps,
        }
    }

    /// Endpoint the job was submitted at — transmitter for
    /// Packet/Broadcast, receiver for Receive.
    pub fn endpoint(&self) -> EndpointId {
        match self {
            Job::Packet { source, .. } => *source,
            Job::Broadcast { source, .. } => *source,
            Job::Receive { receiver, .. } => *receiver,
        }
    }

    pub(crate) fn set_id(&mut self, new_id: JobId) {
        match self {
            Job::Packet { id, .. } | Job::Broadcast { id, .. } | Job::Receive { id, .. } => {
                *id = new_id;
            }
        }
    }

    pub(crate) fn set_status(&mut self, new_status: JobStatus) {
        match self {
            Job::Packet { status, .. }
            | Job::Broadcast { status, .. }
            | Job::Receive { status, .. } => *status = new_status,
        }
    }

    pub(crate) fn reset_allocated_rate(&mut self) {
        match self {
            Job::Packet { allocated_rate_bps, .. }
            | Job::Broadcast { allocated_rate_bps, .. }
            | Job::Receive { allocated_rate_bps, .. } => *allocated_rate_bps = 0.0,
        }
    }
}
