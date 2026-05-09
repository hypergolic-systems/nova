//! Max-min fair allocator over a multi-commodity flow graph.
//! Iterative water-filling: each step, give every unsaturated flow
//! the smallest increment that any of its edges (or its own ceiling)
//! permits; saturate the flow/edge that hit the bound; repeat until
//! nothing can grow. Mirrors
//! `mod/Nova.Core/Communications/BandwidthAllocator.cs`.

/// Allocator-side view of an edge: real link, or virtual broadcast
/// budget. `capacity` is fixed at construction; `used` grows during
/// water-filling. `backing_link` is the index into `graph.links` for
/// real edges; `None` for broadcast-budget edges.
#[derive(Clone, Debug)]
pub struct AllocEdge {
    pub capacity: f64,
    pub used: f64,
    pub backing_link: Option<usize>,
    pub flow_indices: Vec<usize>,
    pub saturated: bool,
}

/// One single-rate flow. `job_index` (if set) names the Packet job in
/// `jobs[]` that owns this flow. `broadcast_index` and `receive_index`
/// (added in step 17) name the Broadcast/Receive jobs feeding this
/// flow. `edge_indices` is the path through `edges[]`.
#[derive(Clone, Debug)]
pub struct AllocFlow {
    pub job_index: Option<usize>,
    pub broadcast_index: Option<usize>,
    pub receive_index: Option<usize>,
    pub ceiling: f64,
    pub edge_indices: Vec<usize>,
    pub rate: f64,
    pub saturated: bool,
}

const ALLOC_EPS: f64 = 1e-12;

/// Run iterative water-filling until no flow can grow further. After
/// return: each flow's `rate` is its allocated bandwidth; each edge's
/// `used` reflects the total demand on it.
pub fn allocate(flows: &mut [AllocFlow], edges: &mut [AllocEdge]) {
    for e in edges.iter_mut() {
        e.used = 0.0;
    }

    loop {
        if !flows.iter().any(|f| !f.saturated) {
            break;
        }

        // delta = min over { unsaturated flow ceilings, per-flow share
        // of edge slack }.
        let mut delta = f64::INFINITY;
        for f in flows.iter() {
            if f.saturated {
                continue;
            }
            let hr = f.ceiling - f.rate;
            if hr < delta {
                delta = hr;
            }
        }
        for e in edges.iter() {
            if e.saturated {
                continue;
            }
            let n = e.flow_indices.iter().filter(|&&fi| !flows[fi].saturated).count();
            if n == 0 {
                continue;
            }
            let per_flow = (e.capacity - e.used) / (n as f64);
            if per_flow < delta {
                delta = per_flow;
            }
        }

        if delta <= 0.0 || delta.is_infinite() {
            for f in flows.iter_mut() {
                f.saturated = true;
            }
            break;
        }

        // Grow all unsaturated flows.
        for f in flows.iter_mut() {
            if !f.saturated {
                f.rate += delta;
            }
        }
        // Edge `used` advances by (active flow count on edge × delta).
        // Active flow count snapshotted before mutation to avoid the
        // borrow conflict.
        let active_per_edge: Vec<usize> = edges
            .iter()
            .map(|e| {
                if e.saturated {
                    0
                } else {
                    e.flow_indices.iter().filter(|&&fi| !flows[fi].saturated).count()
                }
            })
            .collect();
        for (e, &n) in edges.iter_mut().zip(&active_per_edge) {
            if !e.saturated && n > 0 {
                e.used += delta * (n as f64);
            }
        }

        // Saturate flows that hit their ceiling.
        for f in flows.iter_mut() {
            if f.saturated {
                continue;
            }
            if f.rate + ALLOC_EPS >= f.ceiling {
                f.rate = f.ceiling;
                f.saturated = true;
            }
        }
        // Saturate edges that hit capacity.
        for e in edges.iter_mut() {
            if e.saturated {
                continue;
            }
            if e.used + ALLOC_EPS >= e.capacity {
                e.used = e.capacity;
                e.saturated = true;
            }
        }
        // Cascade: any flow on a now-saturated edge is bottlenecked
        // here and must stop growing.
        let saturated_edges: Vec<Vec<usize>> = edges
            .iter()
            .filter_map(|e| if e.saturated { Some(e.flow_indices.clone()) } else { None })
            .collect();
        for indices in saturated_edges {
            for fi in indices {
                if !flows[fi].saturated {
                    flows[fi].saturated = true;
                }
            }
        }
    }
}
