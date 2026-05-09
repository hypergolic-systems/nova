//! Max-bottleneck-path search through a directed comms graph.
//! Mirrors `mod/Nova.Core/Communications/MaxRatePath.cs`.
//!
//! Distance to a node = max over reaching paths of that path's
//! minimum-edge rate. Modified Dijkstra: relax with
//! `min(dist[u], rate(u→v))`, pop with `argmax dist`. Linear-scan PQ
//! — endpoint counts are small.

use std::collections::{HashMap, HashSet};

use super::endpoint::EndpointId;
use super::link::GraphSnapshot;

/// Returns the path from `source` to `dest` as ordered indices into
/// `graph.links`, or `None` if no positive-rate path exists (or if
/// `source == dest`).
pub fn find(
    graph: &GraphSnapshot,
    source: EndpointId,
    dest: EndpointId,
) -> Option<Vec<usize>> {
    if source == dest {
        return None;
    }

    // Adjacency: from-id → outgoing link indices.
    let mut adj: HashMap<EndpointId, Vec<usize>> = HashMap::new();
    for (i, link) in graph.links.iter().enumerate() {
        if link.rate_bps <= 0.0 {
            continue;
        }
        adj.entry(link.from).or_default().push(i);
    }

    let mut dist: HashMap<EndpointId, f64> = HashMap::new();
    dist.insert(source, f64::INFINITY);
    let mut prev_edge: HashMap<EndpointId, usize> = HashMap::new();
    let mut visited: HashSet<EndpointId> = HashSet::new();

    loop {
        // Pop max-distance unvisited node.
        let mut u: Option<EndpointId> = None;
        let mut best: f64 = 0.0;
        for (k, v) in &dist {
            if visited.contains(k) {
                continue;
            }
            if u.is_none() || *v > best {
                u = Some(*k);
                best = *v;
            }
        }
        let u = match u {
            Some(u) => u,
            None => break,
        };
        if best <= 0.0 {
            break;
        }

        visited.insert(u);
        if u == dest {
            break;
        }

        let outs = match adj.get(&u) {
            Some(o) => o,
            None => continue,
        };
        for &li in outs {
            let link = &graph.links[li];
            if visited.contains(&link.to) {
                continue;
            }
            let bottleneck = best.min(link.rate_bps);
            let existing = dist.get(&link.to).copied().unwrap_or(f64::NEG_INFINITY);
            if bottleneck > existing {
                dist.insert(link.to, bottleneck);
                prev_edge.insert(link.to, li);
            }
        }
    }

    if !prev_edge.contains_key(&dest) {
        return None;
    }

    let mut path = Vec::new();
    let mut cur = dest;
    while cur != source {
        let edge_idx = *prev_edge.get(&cur).unwrap();
        path.push(edge_idx);
        cur = graph.links[edge_idx].from;
    }
    path.reverse();
    Some(path)
}

#[cfg(test)]
mod tests {
    use super::super::link::Link;
    use super::*;
    use crate::world::VesselId;

    fn vid(n: u32) -> EndpointId {
        EndpointId::Vessel(VesselId(n))
    }

    fn link(from: u32, to: u32, rate_bps: f64) -> Link {
        Link::new(vid(from), vid(to), 0.0, 0.0, rate_bps)
    }

    fn snapshot(links: Vec<Link>) -> GraphSnapshot {
        GraphSnapshot { links, solved_ut: 0.0 }
    }

    #[test]
    fn direct_link_chosen_when_better_than_relay() {
        // A→B direct rate 100; A→C 50, C→B 50 (relay path bottleneck = 50).
        let g = snapshot(vec![link(1, 2, 100.0), link(1, 3, 50.0), link(3, 2, 50.0)]);
        let path = find(&g, vid(1), vid(2)).unwrap();
        assert_eq!(path.len(), 1);
        assert_eq!(g.links[path[0]].from, vid(1));
        assert_eq!(g.links[path[0]].to, vid(2));
    }

    #[test]
    fn relay_chosen_when_direct_too_weak() {
        // A→B direct rate 10; A→C 50, C→B 50 (relay bottleneck 50 > 10).
        let g = snapshot(vec![link(1, 2, 10.0), link(1, 3, 50.0), link(3, 2, 50.0)]);
        let path = find(&g, vid(1), vid(2)).unwrap();
        assert_eq!(path.len(), 2);
        assert_eq!(g.links[path[0]].from, vid(1));
        assert_eq!(g.links[path[0]].to, vid(3));
        assert_eq!(g.links[path[1]].from, vid(3));
        assert_eq!(g.links[path[1]].to, vid(2));
    }

    #[test]
    fn no_path_returns_none() {
        // A→B and C→D are disconnected.
        let g = snapshot(vec![link(1, 2, 100.0), link(3, 4, 100.0)]);
        assert!(find(&g, vid(1), vid(4)).is_none());
    }

    #[test]
    fn zero_rate_edges_dropped_from_routing() {
        // A→C rate 0 (blocked) — must not be used even though it
        // exists in the graph.
        let g = snapshot(vec![link(1, 3, 0.0), link(1, 2, 50.0), link(2, 3, 50.0)]);
        let path = find(&g, vid(1), vid(3)).unwrap();
        assert_eq!(path.len(), 2);
        assert_eq!(g.links[path[0]].to, vid(2));
        assert_eq!(g.links[path[1]].to, vid(3));
    }

    #[test]
    fn source_equals_dest_returns_none() {
        let g = snapshot(vec![link(1, 2, 100.0)]);
        assert!(find(&g, vid(1), vid(1)).is_none());
    }

    #[test]
    fn four_node_diamond_picks_higher_bottleneck_branch() {
        // 1 → 2 (50) → 4 (50) bottleneck 50
        // 1 → 3 (80) → 4 (70) bottleneck 70
        let g = snapshot(vec![
            link(1, 2, 50.0),
            link(2, 4, 50.0),
            link(1, 3, 80.0),
            link(3, 4, 70.0),
        ]);
        let path = find(&g, vid(1), vid(4)).unwrap();
        assert_eq!(path.len(), 2);
        assert_eq!(g.links[path[0]].to, vid(3));
        assert_eq!(g.links[path[1]].to, vid(4));
    }
}
