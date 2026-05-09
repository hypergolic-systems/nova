//! Water-fill solver for Topological resources. Mirrors
//! `mod/Nova.Core/Systems/StagingFlowSystem.cs` minus drain priorities,
//! edge filters, and jettison handling (deferred).
//!
//! Algorithm (per Solve, per (resource, connected component)):
//!
//!   1. active = pools in component with contents > 0 and max_rate_out > 0
//!   2. remaining = total demand in component
//!   3. while active is non-empty and remaining > 0:
//!        sum_amount = Σ p.contents over active
//!        proposed_p = (p.contents / sum_amount) × remaining
//!        binding = pool with smallest α s.t. α × proposed_p ≥ headroom
//!        apply α-step: rates_p += α × proposed_p; remaining -= α × remaining
//!        if binding: pin it, drop from active, recurse
//!        else: done
//!
//! Coupling pass for multi-input consumers iterates per-resource
//! water-fill, scaling Activity by min input satisfaction until
//! convergence.

use crate::buffer::Buffer;
use crate::resource::Resource;
use crate::sim_clock::SimClock;

/// Smallest amount/rate magnitude treated as nonzero.
pub(crate) const EPSILON: f64 = 1e-12;

/// Per-buffer "is this buffer alive" tolerance, relative to capacity.
/// At non-trivial sim-UT the lerp arithmetic produces residuals
/// bounded by ULP(ut) × rate; an absolute epsilon would leave
/// should-be-empty buffers stuck just above zero. A capacity-relative
/// threshold makes the cliff scale with the buffer's noise floor.
pub(crate) const BUFFER_ALIVE_REL_TOL: f64 = 1e-9;

#[derive(Copy, Clone, Debug, PartialEq, Eq, Hash, PartialOrd, Ord)]
pub struct NodeId(pub u32);

#[derive(Copy, Clone, Debug, PartialEq, Eq, Hash, PartialOrd, Ord)]
pub struct BufferId(pub u32);

#[derive(Copy, Clone, Debug, PartialEq, Eq, Hash, PartialOrd, Ord)]
pub struct ConsumerId(pub u32);

#[derive(Clone, Debug)]
pub struct Node {
    pub id: NodeId,
    pub dry_mass_kg: f64,
    /// Buffer ids attached to this node.
    pub buffers: Vec<BufferId>,
    /// Children in the parent tree (for subtree walks).
    pub children: Vec<NodeId>,
}

#[derive(Debug, Clone)]
pub struct ConsumerInput {
    pub resource: Resource,
    pub max_rate: f64,
    /// Allocated rate this solve. Output of water-fill, before coupling.
    pub allocated: f64,
    /// Per-iter target rate fed into water-fill (activity × max_rate).
    pub target_rate: f64,
}

/// A coupled-input consumer of Topological resources at one node.
/// Caller sets `demand` (0..1) per tick; after solve, `activity`
/// reports the achieved fraction. Multi-input coupling enforces
/// "all inputs or none" — bottlenecked inputs scale activity down.
#[derive(Debug, Clone)]
pub struct Consumer {
    pub id: ConsumerId,
    pub node_id: NodeId,
    pub demand: f64,
    pub activity: f64,
    pub inputs: Vec<ConsumerInput>,
}

#[derive(Clone, Debug)]
pub struct StagingFlowSystem {
    clock: SimClock,
    nodes: Vec<Node>,
    buffers: Vec<Buffer>,
    consumers: Vec<Consumer>,
    edges: Vec<(NodeId, NodeId)>,
}

impl StagingFlowSystem {
    pub fn new(clock: SimClock) -> Self {
        StagingFlowSystem {
            clock,
            nodes: Vec::new(),
            buffers: Vec::new(),
            consumers: Vec::new(),
            edges: Vec::new(),
        }
    }

    pub fn clock(&self) -> &SimClock { &self.clock }

    // ── Adders ─────────────────────────────────────────────────────────

    pub fn add_node(&mut self, dry_mass_kg: f64) -> NodeId {
        let id = NodeId(self.nodes.len() as u32);
        self.nodes.push(Node {
            id,
            dry_mass_kg,
            buffers: Vec::new(),
            children: Vec::new(),
        });
        id
    }

    pub fn add_buffer(&mut self, node: NodeId, resource: Resource, capacity: f64) -> BufferId {
        let id = BufferId(self.buffers.len() as u32);
        let buffer = Buffer::new(resource, capacity, Some(self.clock.clone()));
        self.buffers.push(buffer);
        self.nodes[node.0 as usize].buffers.push(id);
        id
    }

    /// Register parent→child link in the topology tree. Also adds an
    /// undirected edge so the connectivity graph follows the tree by
    /// default; callers don't have to add edges separately.
    pub fn set_parent(&mut self, parent: NodeId, child: NodeId) {
        self.nodes[parent.0 as usize].children.push(child);
        self.add_edge(parent, child);
    }

    /// Add an undirected edge to the connectivity graph. M3 has no
    /// resource filters or up-only direction.
    pub fn add_edge(&mut self, a: NodeId, b: NodeId) {
        self.edges.push((a, b));
    }

    pub fn add_consumer(
        &mut self,
        node: NodeId,
        inputs: Vec<(Resource, f64)>,
    ) -> ConsumerId {
        let id = ConsumerId(self.consumers.len() as u32);
        let inputs = inputs
            .into_iter()
            .map(|(resource, max_rate)| ConsumerInput {
                resource,
                max_rate,
                allocated: 0.0,
                target_rate: 0.0,
            })
            .collect();
        self.consumers.push(Consumer {
            id,
            node_id: node,
            demand: 0.0,
            activity: 0.0,
            inputs,
        });
        id
    }

    // ── Accessors ──────────────────────────────────────────────────────

    pub fn nodes(&self) -> &[Node] { &self.nodes }
    pub fn node(&self, id: NodeId) -> &Node { &self.nodes[id.0 as usize] }

    pub fn buffer(&self, id: BufferId) -> &Buffer { &self.buffers[id.0 as usize] }
    pub fn buffer_mut(&mut self, id: BufferId) -> &mut Buffer { &mut self.buffers[id.0 as usize] }

    pub fn consumer(&self, id: ConsumerId) -> &Consumer { &self.consumers[id.0 as usize] }
    pub fn consumer_mut(&mut self, id: ConsumerId) -> &mut Consumer {
        &mut self.consumers[id.0 as usize]
    }

    // ── Queries ────────────────────────────────────────────────────────

    /// Total mass = dry mass + Σ (buffer contents × resource density).
    pub fn node_mass(&self, id: NodeId) -> f64 {
        let n = self.node(id);
        let mut m = n.dry_mass_kg;
        for &bid in &n.buffers {
            let b = self.buffer(bid);
            m += b.contents() * b.resource.density();
        }
        m
    }

    /// `id` plus all descendants reachable by walking children
    /// transitively (DFS pre-order).
    pub fn subtree_nodes(&self, id: NodeId) -> Vec<NodeId> {
        let mut out = Vec::new();
        self.walk_subtree(id, &mut out);
        out
    }

    fn walk_subtree(&self, id: NodeId, out: &mut Vec<NodeId>) {
        out.push(id);
        for &child in &self.node(id).children {
            self.walk_subtree(child, out);
        }
    }

    pub(crate) fn is_buffer_alive(b: &Buffer) -> bool {
        b.contents() > BUFFER_ALIVE_REL_TOL * b.capacity
    }

    pub(crate) fn buffer_has_fill_headroom(b: &Buffer) -> bool {
        b.contents() < b.capacity * (1.0 - BUFFER_ALIVE_REL_TOL)
    }

    /// Time (s) to the next event affecting this node's buffers
    /// (buffer empties or fills). +∞ when nothing is moving.
    pub fn node_time_to_next_expiry(&self, id: NodeId) -> f64 {
        let mut earliest = f64::INFINITY;
        for &bid in &self.node(id).buffers {
            let t = Self::buffer_time_to_expiry(self.buffer(bid));
            if t < earliest { earliest = t; }
        }
        earliest
    }

    /// System-wide soonest buffer event (drain to empty, fill to
    /// capacity). +∞ when no rate is moving anything. The tick driver
    /// uses this to bound how far it can advance the clock without
    /// missing a state transition.
    pub fn max_tick_dt(&self) -> f64 {
        let mut earliest = f64::INFINITY;
        for b in &self.buffers {
            let t = Self::buffer_time_to_expiry(b);
            if t < earliest { earliest = t; }
        }
        earliest
    }

    fn buffer_time_to_expiry(b: &Buffer) -> f64 {
        let rate = b.rate();
        if rate < -EPSILON && Self::is_buffer_alive(b) {
            b.contents() / -rate
        } else if rate > EPSILON && Self::buffer_has_fill_headroom(b) {
            (b.capacity - b.contents()) / rate
        } else {
            f64::INFINITY
        }
    }

    // ── Solve ──────────────────────────────────────────────────────────

    /// Compute drain rates for all consumers; populate `Buffer.rate`
    /// and `Consumer.activity`. M3 ships the topological side only;
    /// no drain priorities, no edge filters.
    ///
    /// Outer loop: water-fill across (resource, component) +
    /// coupling-pass scale-down for any consumer whose worst input
    /// satisfaction is below 1. Activity decreases monotonically;
    /// converges in ≤ |consumers| iterations.
    pub fn solve(&mut self) {
        // Initial activity = demand for everyone.
        for c in &mut self.consumers {
            c.activity = c.demand;
        }

        let components = self.compute_components();
        let max_iters = self.consumers.len() + 1;

        for _ in 0..max_iters {
            // Each coupling iter starts from a clean slate so prior-
            // iter rates don't accumulate.
            for b in &mut self.buffers {
                b.set_rate(0.0);
            }
            for c in &mut self.consumers {
                for input in &mut c.inputs {
                    input.allocated = 0.0;
                    input.target_rate = c.activity * input.max_rate;
                }
            }

            let resources = self.distinct_active_resources();
            for &resource in &resources {
                for comp in &components {
                    self.water_fill_pool(resource, comp);
                }
            }

            // Coupling pass: any consumer with worst-input satisfaction
            // < 1 gets activity scaled by that min satisfaction. Same
            // semantics for single-input consumers — their activity
            // collapses to actual achievement (e.g. cap-bound supply).
            let mut converged = true;
            for c in &mut self.consumers {
                if c.activity <= EPSILON || c.inputs.is_empty() {
                    continue;
                }
                let mut min_sat = 1.0_f64;
                for input in &c.inputs {
                    let sat = if input.target_rate > EPSILON {
                        (input.allocated / input.target_rate).min(1.0)
                    } else {
                        1.0
                    };
                    if sat < min_sat { min_sat = sat; }
                }
                if min_sat < 1.0 - EPSILON {
                    c.activity *= min_sat;
                    converged = false;
                }
            }

            if converged { break; }
        }
    }

    /// Resources any active consumer is currently asking for. Used
    /// to skip the per-(resource, component) loop for resources no
    /// one is requesting this iteration.
    fn distinct_active_resources(&self) -> Vec<Resource> {
        let mut out: Vec<Resource> = Vec::new();
        for c in &self.consumers {
            if c.activity <= EPSILON { continue; }
            for input in &c.inputs {
                if !out.contains(&input.resource) {
                    out.push(input.resource);
                }
            }
        }
        out
    }

    /// Connected components of the node graph (DFS).
    fn compute_components(&self) -> Vec<Vec<NodeId>> {
        let n = self.nodes.len();
        let mut adj: Vec<Vec<NodeId>> = vec![Vec::new(); n];
        for &(a, b) in &self.edges {
            adj[a.0 as usize].push(b);
            adj[b.0 as usize].push(a);
        }

        let mut visited = vec![false; n];
        let mut comps: Vec<Vec<NodeId>> = Vec::new();
        for start in 0..n {
            if visited[start] { continue; }
            let mut comp = Vec::new();
            let mut stack = vec![NodeId(start as u32)];
            while let Some(node) = stack.pop() {
                let idx = node.0 as usize;
                if visited[idx] { continue; }
                visited[idx] = true;
                comp.push(node);
                for &nbr in &adj[idx] {
                    if !visited[nbr.0 as usize] {
                        stack.push(nbr);
                    }
                }
            }
            comps.push(comp);
        }
        comps
    }

    /// Distribute the total demand for `resource` within `component`
    /// across the alive buffers in that pool, proportional to
    /// current contents. Iterative α-step: per round, find the
    /// binding pool (smallest α s.t. α × proposed ≥ headroom),
    /// apply α × remaining demand across the active set, pin the
    /// binding pool, recurse on the residual.
    fn water_fill_pool(&mut self, resource: Resource, component: &[NodeId]) {
        let mut active: Vec<BufferId> = self
            .buffers_in_component(resource, component)
            .into_iter()
            .filter(|&b| Self::is_buffer_alive(self.buffer(b)))
            .collect();
        let consumers: Vec<ConsumerId> =
            self.consumers_in_component_for(resource, component);

        let total_demand: f64 = consumers
            .iter()
            .map(|&cid| self.consumer_input(cid, resource).target_rate)
            .sum();
        if total_demand <= EPSILON { return; }
        if active.is_empty() { return; }

        let mut remaining = total_demand;
        // Loop bound mirrors the C# safety: at worst we pin one pool
        // per iter, so |active| iters covers the worst case. +1 for
        // the final all-α=1 iter.
        let max_iters = active.len() + 1;
        for _ in 0..max_iters {
            if active.is_empty() || remaining <= EPSILON { break; }

            let total_contents: f64 =
                active.iter().map(|&b| self.buffer(b).contents()).sum();
            if total_contents <= EPSILON { break; }

            // Find binding pool: smallest α among active.
            let mut alpha = 1.0_f64;
            let mut binding_idx: Option<usize> = None;
            for (i, &b) in active.iter().enumerate() {
                let proposed = (self.buffer(b).contents() / total_contents) * remaining;
                if proposed <= EPSILON { continue; }
                let cur_drain = -self.buffer(b).rate();
                let headroom = self.buffer(b).max_rate_out - cur_drain;
                if headroom <= EPSILON {
                    alpha = 0.0;
                    binding_idx = Some(i);
                    break;
                }
                let p_alpha = headroom / proposed;
                if p_alpha < alpha {
                    alpha = p_alpha;
                    binding_idx = Some(i);
                }
            }

            // Apply α-step across active pools.
            for &b in &active {
                let proposed = (self.buffer(b).contents() / total_contents) * remaining;
                let drain_step = alpha * proposed;
                let cur = self.buffer(b).rate();
                self.buffer_mut(b).set_rate(cur - drain_step);
            }
            remaining -= alpha * remaining;

            if alpha >= 1.0 - EPSILON { break; }
            match binding_idx {
                Some(i) => { active.remove(i); }
                None => break,
            }
        }

        let supplied = total_demand - remaining;
        let supply_ratio = supplied / total_demand;
        for &cid in &consumers {
            let idx = self.consumer_input_index(cid, resource).unwrap();
            let target = self.consumers[cid.0 as usize].inputs[idx].target_rate;
            self.consumers[cid.0 as usize].inputs[idx].allocated = target * supply_ratio;
        }
    }

    fn buffers_in_component(&self, resource: Resource, component: &[NodeId]) -> Vec<BufferId> {
        let mut out = Vec::new();
        for &n in component {
            for &bid in &self.node(n).buffers {
                if self.buffer(bid).resource == resource {
                    out.push(bid);
                }
            }
        }
        out
    }

    fn consumers_in_component_for(
        &self,
        resource: Resource,
        component: &[NodeId],
    ) -> Vec<ConsumerId> {
        self.consumers
            .iter()
            .filter(|c| {
                c.demand > EPSILON
                    && component.contains(&c.node_id)
                    && c.inputs.iter().any(|i| i.resource == resource)
            })
            .map(|c| c.id)
            .collect()
    }

    fn consumer_input_index(&self, cid: ConsumerId, resource: Resource) -> Option<usize> {
        self.consumers[cid.0 as usize]
            .inputs
            .iter()
            .position(|i| i.resource == resource)
    }

    fn consumer_input(&self, cid: ConsumerId, resource: Resource) -> &ConsumerInput {
        let idx = self.consumer_input_index(cid, resource).unwrap();
        &self.consumers[cid.0 as usize].inputs[idx]
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use approx::assert_relative_eq;

    fn fresh_sys() -> StagingFlowSystem {
        StagingFlowSystem::new(SimClock::new(0.0))
    }

    #[test]
    fn add_node_assigns_sequential_ids() {
        let mut sys = fresh_sys();
        assert_eq!(sys.add_node(10.0), NodeId(0));
        assert_eq!(sys.add_node(20.0), NodeId(1));
        assert_eq!(sys.add_node(30.0), NodeId(2));
    }

    #[test]
    fn add_buffer_attaches_to_node() {
        let mut sys = fresh_sys();
        let n = sys.add_node(0.0);
        let b1 = sys.add_buffer(n, Resource::Hydrazine, 100.0);
        let b2 = sys.add_buffer(n, Resource::Rp1, 250.0);
        assert_eq!(sys.node(n).buffers, vec![b1, b2]);
        assert_eq!(sys.buffer(b1).resource, Resource::Hydrazine);
        assert_eq!(sys.buffer(b2).capacity, 250.0);
    }

    #[test]
    fn buffers_share_clock_so_lerp_sees_advance() {
        let clock = SimClock::new(0.0);
        let mut sys = StagingFlowSystem::new(clock.clone());
        let n = sys.add_node(0.0);
        let b = sys.add_buffer(n, Resource::Hydrazine, 100.0);
        sys.buffer_mut(b).set_rate(-1.0);
        clock.advance(20.0);
        assert_relative_eq!(sys.buffer(b).contents(), 80.0);
    }

    #[test]
    fn set_parent_records_child_and_adds_edge() {
        let mut sys = fresh_sys();
        let p = sys.add_node(0.0);
        let c = sys.add_node(0.0);
        sys.set_parent(p, c);
        assert_eq!(sys.node(p).children, vec![c]);
    }

    #[test]
    fn subtree_nodes_walks_children_pre_order() {
        let mut sys = fresh_sys();
        // p → c1, c2; c1 → g1
        let p = sys.add_node(0.0);
        let c1 = sys.add_node(0.0);
        let c2 = sys.add_node(0.0);
        let g1 = sys.add_node(0.0);
        sys.set_parent(p, c1);
        sys.set_parent(p, c2);
        sys.set_parent(c1, g1);
        assert_eq!(sys.subtree_nodes(p), vec![p, c1, g1, c2]);
        assert_eq!(sys.subtree_nodes(c2), vec![c2]);
    }

    #[test]
    fn node_mass_sums_dry_plus_buffer_mass() {
        let mut sys = fresh_sys();
        let n = sys.add_node(500.0); // 500 kg dry
        // 100 L × 1.0 kg/L = 100 kg of hydrazine
        let _ = sys.add_buffer(n, Resource::Hydrazine, 100.0);
        // 50 L × 0.8 kg/L = 40 kg of RP-1
        let _ = sys.add_buffer(n, Resource::Rp1, 50.0);
        assert_relative_eq!(sys.node_mass(n), 640.0);
    }

    #[test]
    fn add_consumer_creates_inputs_with_zeroed_outputs() {
        let mut sys = fresh_sys();
        let n = sys.add_node(0.0);
        let c = sys.add_consumer(
            n,
            vec![(Resource::Rp1, 2.0), (Resource::LiquidOxygen, 3.0)],
        );
        let consumer = sys.consumer(c);
        assert_eq!(consumer.node_id, n);
        assert_eq!(consumer.inputs.len(), 2);
        assert_relative_eq!(consumer.inputs[0].max_rate, 2.0);
        assert_relative_eq!(consumer.inputs[1].max_rate, 3.0);
        assert_relative_eq!(consumer.demand, 0.0);
        assert_relative_eq!(consumer.activity, 0.0);
    }

    #[test]
    fn time_to_next_expiry_picks_earliest_drain() {
        let clock = SimClock::new(0.0);
        let mut sys = StagingFlowSystem::new(clock.clone());
        let n = sys.add_node(0.0);
        let b1 = sys.add_buffer(n, Resource::Hydrazine, 100.0);
        let b2 = sys.add_buffer(n, Resource::Rp1, 200.0);
        // b1: drains at 5/s → 20s to empty
        sys.buffer_mut(b1).set_rate(-5.0);
        // b2: drains at 100/s → 2s to empty
        sys.buffer_mut(b2).set_rate(-100.0);
        assert_relative_eq!(sys.node_time_to_next_expiry(n), 2.0);
    }

    #[test]
    fn max_tick_dt_aggregates_across_all_nodes() {
        let mut sys = StagingFlowSystem::new(SimClock::new(0.0));
        let n1 = sys.add_node(0.0);
        let n2 = sys.add_node(0.0);
        let b1 = sys.add_buffer(n1, Resource::Hydrazine, 100.0);
        let b2 = sys.add_buffer(n2, Resource::Rp1, 50.0);
        // n1's buffer empties in 10s; n2's in 5s — system-wide min = 5s.
        sys.buffer_mut(b1).set_rate(-10.0);
        sys.buffer_mut(b2).set_rate(-10.0);
        assert_relative_eq!(sys.max_tick_dt(), 5.0);
    }

    #[test]
    fn max_tick_dt_is_infinity_when_nothing_is_moving() {
        let mut sys = StagingFlowSystem::new(SimClock::new(0.0));
        let n = sys.add_node(0.0);
        let _ = sys.add_buffer(n, Resource::Hydrazine, 100.0);
        assert!(sys.max_tick_dt().is_infinite());
    }

    #[test]
    fn max_tick_dt_picks_fill_event_when_filling() {
        let mut sys = StagingFlowSystem::new(SimClock::new(0.0));
        let n = sys.add_node(0.0);
        let b = sys.add_buffer(n, Resource::Hydrazine, 100.0);
        sys.buffer_mut(b).set_contents(40.0);
        // 60 L of headroom at 5/s → 12s to fill.
        sys.buffer_mut(b).set_rate(5.0);
        assert_relative_eq!(sys.max_tick_dt(), 12.0);
    }

    // ── Water-fill solver tests ───────────────────────────────────────

    fn one_pool_sys() -> (StagingFlowSystem, NodeId) {
        let mut sys = StagingFlowSystem::new(SimClock::new(0.0));
        let n = sys.add_node(0.0);
        (sys, n)
    }

    /// Default-flow-limited buffer add — most tests don't care about
    /// MaxRateOut binding and want effectively-unlimited flow.
    fn add_open_buffer(sys: &mut StagingFlowSystem, n: NodeId, r: Resource, cap: f64) -> BufferId {
        let id = sys.add_buffer(n, r, cap);
        sys.buffer_mut(id).flow_limits(1.0e9, 1.0e9);
        id
    }

    #[test]
    fn solve_with_no_consumers_is_a_noop() {
        let (mut sys, n) = one_pool_sys();
        let b = add_open_buffer(&mut sys, n, Resource::Hydrazine, 100.0);
        sys.solve();
        assert_relative_eq!(sys.buffer(b).rate(), 0.0);
    }

    #[test]
    fn solve_with_zero_demand_leaves_buffer_rate_at_zero() {
        let (mut sys, n) = one_pool_sys();
        let b = add_open_buffer(&mut sys, n, Resource::Hydrazine, 100.0);
        let c = sys.add_consumer(n, vec![(Resource::Hydrazine, 5.0)]);
        sys.consumer_mut(c).demand = 0.0;
        sys.solve();
        assert_relative_eq!(sys.buffer(b).rate(), 0.0);
        assert_relative_eq!(sys.consumer(c).activity, 0.0);
    }

    #[test]
    fn single_tank_single_consumer_full_demand_drains_at_max_rate() {
        let (mut sys, n) = one_pool_sys();
        let b = add_open_buffer(&mut sys, n, Resource::Hydrazine, 100.0);
        let c = sys.add_consumer(n, vec![(Resource::Hydrazine, 5.0)]);
        sys.consumer_mut(c).demand = 1.0;
        sys.solve();
        assert_relative_eq!(sys.buffer(b).rate(), -5.0);
        assert_relative_eq!(sys.consumer(c).activity, 1.0);
    }

    #[test]
    fn single_tank_half_throttle_drains_at_half_max_rate() {
        let (mut sys, n) = one_pool_sys();
        let b = add_open_buffer(&mut sys, n, Resource::Hydrazine, 100.0);
        let c = sys.add_consumer(n, vec![(Resource::Hydrazine, 10.0)]);
        sys.consumer_mut(c).demand = 0.5;
        sys.solve();
        assert_relative_eq!(sys.buffer(b).rate(), -5.0);
    }

    #[test]
    fn two_equal_tanks_split_drain_evenly() {
        let (mut sys, n) = one_pool_sys();
        let b1 = add_open_buffer(&mut sys, n, Resource::Hydrazine, 100.0);
        let b2 = add_open_buffer(&mut sys, n, Resource::Hydrazine, 100.0);
        let c = sys.add_consumer(n, vec![(Resource::Hydrazine, 10.0)]);
        sys.consumer_mut(c).demand = 1.0;
        sys.solve();
        assert_relative_eq!(sys.buffer(b1).rate(), -5.0);
        assert_relative_eq!(sys.buffer(b2).rate(), -5.0);
    }

    #[test]
    fn two_unequal_tanks_drain_proportional_to_contents() {
        let (mut sys, n) = one_pool_sys();
        let b1 = add_open_buffer(&mut sys, n, Resource::Hydrazine, 100.0);
        let b2 = add_open_buffer(&mut sys, n, Resource::Hydrazine, 100.0);
        sys.buffer_mut(b1).set_contents(80.0);
        sys.buffer_mut(b2).set_contents(20.0);
        let c = sys.add_consumer(n, vec![(Resource::Hydrazine, 10.0)]);
        sys.consumer_mut(c).demand = 1.0;
        sys.solve();
        // 80:20 contents → 80% / 20% drain split.
        assert_relative_eq!(sys.buffer(b1).rate(), -8.0);
        assert_relative_eq!(sys.buffer(b2).rate(), -2.0);
    }

    #[test]
    fn empty_tank_filtered_out_of_active_pool() {
        let (mut sys, n) = one_pool_sys();
        let b1 = add_open_buffer(&mut sys, n, Resource::Hydrazine, 100.0);
        let b2 = add_open_buffer(&mut sys, n, Resource::Hydrazine, 100.0);
        sys.buffer_mut(b2).set_contents(0.0);
        let c = sys.add_consumer(n, vec![(Resource::Hydrazine, 10.0)]);
        sys.consumer_mut(c).demand = 1.0;
        sys.solve();
        assert_relative_eq!(sys.buffer(b1).rate(), -10.0);
        assert_relative_eq!(sys.buffer(b2).rate(), 0.0);
    }

    #[test]
    fn two_consumers_share_one_tank_proportionally() {
        let (mut sys, n) = one_pool_sys();
        let b = add_open_buffer(&mut sys, n, Resource::Hydrazine, 100.0);
        let c1 = sys.add_consumer(n, vec![(Resource::Hydrazine, 6.0)]);
        let c2 = sys.add_consumer(n, vec![(Resource::Hydrazine, 4.0)]);
        sys.consumer_mut(c1).demand = 1.0;
        sys.consumer_mut(c2).demand = 1.0;
        sys.solve();
        // Both fully supplied — total drain == sum of demands.
        assert_relative_eq!(sys.buffer(b).rate(), -10.0);
    }

    #[test]
    fn buffers_in_disconnected_components_dont_share() {
        let mut sys = StagingFlowSystem::new(SimClock::new(0.0));
        let n1 = sys.add_node(0.0);
        let n2 = sys.add_node(0.0);
        // No edge between n1 and n2 → separate components.
        let b1 = add_open_buffer(&mut sys, n1, Resource::Hydrazine, 100.0);
        let b2 = add_open_buffer(&mut sys, n2, Resource::Hydrazine, 100.0);
        let c = sys.add_consumer(n1, vec![(Resource::Hydrazine, 5.0)]);
        sys.consumer_mut(c).demand = 1.0;
        sys.solve();
        // Only n1's buffer drains; n2 is unreachable.
        assert_relative_eq!(sys.buffer(b1).rate(), -5.0);
        assert_relative_eq!(sys.buffer(b2).rate(), 0.0);
    }

    #[test]
    fn buffers_via_edge_pool_together() {
        let mut sys = StagingFlowSystem::new(SimClock::new(0.0));
        let n1 = sys.add_node(0.0);
        let n2 = sys.add_node(0.0);
        sys.add_edge(n1, n2);
        let b1 = add_open_buffer(&mut sys, n1, Resource::Hydrazine, 100.0);
        let b2 = add_open_buffer(&mut sys, n2, Resource::Hydrazine, 100.0);
        let c = sys.add_consumer(n1, vec![(Resource::Hydrazine, 10.0)]);
        sys.consumer_mut(c).demand = 1.0;
        sys.solve();
        // Edge connects → one component → split 50/50.
        assert_relative_eq!(sys.buffer(b1).rate(), -5.0);
        assert_relative_eq!(sys.buffer(b2).rate(), -5.0);
    }

    // ── Binding-pool detection tests ──────────────────────────────────

    #[test]
    fn single_tank_clips_at_max_rate_out() {
        let (mut sys, n) = one_pool_sys();
        let b = sys.add_buffer(n, Resource::Hydrazine, 100.0);
        sys.buffer_mut(b).flow_limits(1.0e9, 5.0); // out cap = 5/s
        let c = sys.add_consumer(n, vec![(Resource::Hydrazine, 10.0)]);
        sys.consumer_mut(c).demand = 1.0;
        sys.solve();
        // Tank caps at 5/s; consumer wanted 10 → allocated == 5,
        // satisfaction == 0.5.
        assert_relative_eq!(sys.buffer(b).rate(), -5.0);
        let allocated = sys.consumer(c).inputs[0].allocated;
        assert_relative_eq!(allocated, 5.0);
    }

    #[test]
    fn binding_pool_residual_redistributes_to_other_pools() {
        let (mut sys, n) = one_pool_sys();
        let b1 = sys.add_buffer(n, Resource::Hydrazine, 100.0);
        let b2 = sys.add_buffer(n, Resource::Hydrazine, 100.0);
        // b1 caps at 3/s; b2 unlimited.
        sys.buffer_mut(b1).flow_limits(1.0e9, 3.0);
        sys.buffer_mut(b2).flow_limits(1.0e9, 1.0e9);
        let c = sys.add_consumer(n, vec![(Resource::Hydrazine, 10.0)]);
        sys.consumer_mut(c).demand = 1.0;
        sys.solve();
        // 50/50 proposed initially, b1 binds at 3 (alpha = 3/5 = 0.6),
        // each pool gets 0.6 × 5 = 3. b1 pinned. Residual = 4 goes to b2.
        // b2 final drain = 3 + 4 = 7.
        assert_relative_eq!(sys.buffer(b1).rate(), -3.0);
        assert_relative_eq!(sys.buffer(b2).rate(), -7.0);
        assert_relative_eq!(sys.consumer(c).inputs[0].allocated, 10.0);
    }

    #[test]
    fn all_pools_binding_caps_total_supply() {
        let (mut sys, n) = one_pool_sys();
        let b1 = sys.add_buffer(n, Resource::Hydrazine, 100.0);
        let b2 = sys.add_buffer(n, Resource::Hydrazine, 100.0);
        sys.buffer_mut(b1).flow_limits(1.0e9, 2.0);
        sys.buffer_mut(b2).flow_limits(1.0e9, 3.0);
        let c = sys.add_consumer(n, vec![(Resource::Hydrazine, 100.0)]);
        sys.consumer_mut(c).demand = 1.0;
        sys.solve();
        // Both pools cap; total supply = 2 + 3 = 5; consumer wanted 100.
        assert_relative_eq!(sys.buffer(b1).rate(), -2.0);
        assert_relative_eq!(sys.buffer(b2).rate(), -3.0);
        assert_relative_eq!(sys.consumer(c).inputs[0].allocated, 5.0);
    }

    // ── Coupling-pass tests ───────────────────────────────────────────

    #[test]
    fn single_input_activity_collapses_to_cap_bound_supply() {
        // When the single buffer's MaxRateOut binds below the
        // consumer's request, activity should reflect the achieved
        // ratio — same coupling logic as multi-input, just trivially.
        let (mut sys, n) = one_pool_sys();
        let b = sys.add_buffer(n, Resource::Hydrazine, 100.0);
        sys.buffer_mut(b).flow_limits(1.0e9, 4.0);
        let c = sys.add_consumer(n, vec![(Resource::Hydrazine, 10.0)]);
        sys.consumer_mut(c).demand = 1.0;
        sys.solve();
        // 4/10 satisfied → activity = 0.4.
        assert_relative_eq!(sys.consumer(c).activity, 0.4);
        assert_relative_eq!(sys.buffer(b).rate(), -4.0);
    }

    #[test]
    fn two_propellant_consumer_fully_fueled_runs_at_full_activity() {
        let (mut sys, n) = one_pool_sys();
        let b_rp1 = add_open_buffer(&mut sys, n, Resource::Rp1, 100.0);
        let b_lox = add_open_buffer(&mut sys, n, Resource::LiquidOxygen, 100.0);
        let c = sys.add_consumer(
            n,
            vec![(Resource::Rp1, 2.0), (Resource::LiquidOxygen, 3.0)],
        );
        sys.consumer_mut(c).demand = 1.0;
        sys.solve();
        assert_relative_eq!(sys.consumer(c).activity, 1.0);
        assert_relative_eq!(sys.buffer(b_rp1).rate(), -2.0);
        assert_relative_eq!(sys.buffer(b_lox).rate(), -3.0);
    }

    #[test]
    fn two_propellant_consumer_one_starved_runs_at_zero_activity() {
        let (mut sys, n) = one_pool_sys();
        let b_rp1 = add_open_buffer(&mut sys, n, Resource::Rp1, 100.0);
        let b_lox = add_open_buffer(&mut sys, n, Resource::LiquidOxygen, 100.0);
        // LOX tank empty.
        sys.buffer_mut(b_lox).set_contents(0.0);
        let c = sys.add_consumer(
            n,
            vec![(Resource::Rp1, 2.0), (Resource::LiquidOxygen, 3.0)],
        );
        sys.consumer_mut(c).demand = 1.0;
        sys.solve();
        assert_relative_eq!(sys.consumer(c).activity, 0.0);
        // RP-1 should NOT drain — coupling pinned activity to 0,
        // and the next iter ran with target_rate = 0 for both inputs.
        assert_relative_eq!(sys.buffer(b_rp1).rate(), 0.0);
        assert_relative_eq!(sys.buffer(b_lox).rate(), 0.0);
    }

    #[test]
    fn two_propellant_consumer_one_cap_bound_scales_activity_by_min_sat() {
        let (mut sys, n) = one_pool_sys();
        let b_rp1 = add_open_buffer(&mut sys, n, Resource::Rp1, 100.0);
        let b_lox = sys.add_buffer(n, Resource::LiquidOxygen, 100.0);
        // LOX tank capped at 1.5/s — half of the 3.0/s the engine wants.
        sys.buffer_mut(b_lox).flow_limits(1.0e9, 1.5);
        let c = sys.add_consumer(
            n,
            vec![(Resource::Rp1, 2.0), (Resource::LiquidOxygen, 3.0)],
        );
        sys.consumer_mut(c).demand = 1.0;
        sys.solve();
        // LOX min_sat = 1.5/3 = 0.5 → activity = 0.5.
        assert_relative_eq!(sys.consumer(c).activity, 0.5);
        // After coupling, both inputs drain at activity * max_rate.
        assert_relative_eq!(sys.buffer(b_rp1).rate(), -1.0);
        assert_relative_eq!(sys.buffer(b_lox).rate(), -1.5);
    }

    #[test]
    fn coupling_resolves_two_consumers_competing_with_shared_starvation() {
        // Both engines want LOX; total capped tank can't satisfy
        // both at full throttle.
        let (mut sys, n) = one_pool_sys();
        let b_rp1 = add_open_buffer(&mut sys, n, Resource::Rp1, 1000.0);
        let b_lox = sys.add_buffer(n, Resource::LiquidOxygen, 1000.0);
        sys.buffer_mut(b_lox).flow_limits(1.0e9, 3.0); // total LOX cap = 3/s
        let e1 = sys.add_consumer(
            n,
            vec![(Resource::Rp1, 2.0), (Resource::LiquidOxygen, 3.0)],
        );
        let e2 = sys.add_consumer(
            n,
            vec![(Resource::Rp1, 2.0), (Resource::LiquidOxygen, 3.0)],
        );
        sys.consumer_mut(e1).demand = 1.0;
        sys.consumer_mut(e2).demand = 1.0;
        sys.solve();
        // LOX demand = 6/s, supply = 3/s → each engine gets half →
        // activity = 0.5 for both.
        assert_relative_eq!(sys.consumer(e1).activity, 0.5);
        assert_relative_eq!(sys.consumer(e2).activity, 0.5);
        // Total drain on LOX = 3/s (the cap); on RP-1 = 2/s (1/s each).
        assert_relative_eq!(sys.buffer(b_lox).rate(), -3.0);
        assert_relative_eq!(sys.buffer(b_rp1).rate(), -2.0);
    }
}
