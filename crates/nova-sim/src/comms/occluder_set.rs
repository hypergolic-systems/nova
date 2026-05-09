//! Builds the per-link occluder set: which celestial bodies could
//! potentially block the line of sight between two endpoints.
//! Mirrors `mod/Nova.Core/Communications/OccluderSet.cs`.
//!
//! Rule:
//!   LCA = lowest common ancestor of the two endpoints' parent chains
//!         in the SOI tree.
//!   Penultimate-A = body on A's chain just below LCA, or LCA itself
//!                   if A's primary body IS the LCA.
//!   Penultimate-B = same for B.
//!   Set = {LCA} ∪ subtree(penult_a) if penult_a ≠ LCA
//!                ∪ subtree(penult_b) if penult_b ≠ LCA
//!
//! Endpoints with no primary body yield an empty set — the link is
//! treated as always unblocked. This is the safe default for test
//! fixtures that don't wire body context.

use std::collections::HashSet;

use crate::ephem::{BodyId, Ephemeris};

/// Set of bodies that could occlude the chord between two endpoints
/// whose primary bodies are `a` and `b`. Symmetric in (a, b).
pub fn occluder_set(
    a: Option<BodyId>,
    b: Option<BodyId>,
    ephem: &Ephemeris,
) -> Vec<BodyId> {
    let (a, b) = match (a, b) {
        (Some(a), Some(b)) => (a, b),
        _ => return Vec::new(),
    };

    let chain_a = chain_to_root(a, ephem);
    let chain_b = chain_to_root(b, ephem);
    let in_b: HashSet<BodyId> = chain_b.iter().copied().collect();

    // First entry of A's chain that's also in B's chain is the LCA.
    let (idx_a, lca) = match chain_a.iter().enumerate().find(|(_, x)| in_b.contains(x)) {
        Some((i, x)) => (i, *x),
        None => return Vec::new(),
    };
    let idx_b = chain_b.iter().position(|x| *x == lca).unwrap();

    let penult_a = if idx_a == 0 { lca } else { chain_a[idx_a - 1] };
    let penult_b = if idx_b == 0 { lca } else { chain_b[idx_b - 1] };

    let mut set: HashSet<BodyId> = HashSet::new();
    set.insert(lca);
    if penult_a != lca {
        add_subtree(penult_a, ephem, &mut set);
    }
    if penult_b != lca {
        add_subtree(penult_b, ephem, &mut set);
    }
    set.into_iter().collect()
}

fn chain_to_root(leaf: BodyId, ephem: &Ephemeris) -> Vec<BodyId> {
    let mut chain = Vec::new();
    let mut cur = Some(leaf);
    while let Some(id) = cur {
        chain.push(id);
        cur = ephem.body(id).parent;
    }
    chain
}

fn add_subtree(root: BodyId, ephem: &Ephemeris, set: &mut HashSet<BodyId>) {
    if !set.insert(root) {
        return;
    }
    for &child in ephem.children(root) {
        add_subtree(child, ephem, set);
    }
}
