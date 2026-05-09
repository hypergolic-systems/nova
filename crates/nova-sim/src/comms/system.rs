//! World-level communications solver. Mirrors
//! `mod/Nova.Core/Communications/CommunicationsNetwork.cs`.
//!
//! Steady-state, event-driven graph of antenna-bearing endpoints.
//! Vessel endpoints are synthesised at every solve from
//! `world.vessels`; ground stations are registered explicitly via
//! `add_ground_station` and persist across solves.

use std::collections::HashMap;

use crate::ephem::BodyId;
use crate::math::Vec3d;
use crate::world::World;

use super::allocator::{allocate, AllocEdge, AllocFlow};
use super::endpoint::{Endpoint, EndpointId, EndpointKind, PathSummary};
use super::ground_station::GroundStationSpec;
use super::job::{Job, JobId, JobStatus};
use super::link::{GraphSnapshot, Link};
use super::link_horizon;
use super::max_rate_path;
use super::motion::MotionModel;
use super::occluder_set::occluder_set;
use super::occlusion::{is_any_blocked, is_blocked};
use super::parameters::{
    BUCKET_COUNT, MAX_HORIZON_SECONDS, NOISE_FLOOR, PRESCREEN_SAMPLES,
};
use super::rate_buckets::{bucket_index, quantize};

#[derive(Clone, Debug, Default)]
pub struct CommsSystem {
    /// Pre-built ground endpoints. Cloned at every solve to seed the
    /// fresh `endpoints` vec; ground identity (`Ground(n)` id) is
    /// preserved across solves.
    ground_endpoints: Vec<Endpoint>,
    /// Counter for `Ground(n)` ids; bumped on every
    /// `add_ground_station`.
    next_ground_id: u32,
    /// Designated home endpoint id (typically KSC). Used by
    /// `RefreshHomePathSummaries` and the path-to-home telemetry on
    /// each `Endpoint`.
    home: Option<EndpointId>,
    /// All endpoints synthesised at the most recent solve. Vessel
    /// endpoints rebuilt fresh each solve; ground endpoints cloned
    /// from `ground_endpoints`.
    endpoints: Vec<Endpoint>,
    /// Active comms jobs. Persistent across solves; the integration
    /// step advances byte counters and the allocation step writes
    /// `allocated_rate_bps`.
    jobs: Vec<Job>,
    /// Monotonic JobId counter. Starts at 1 so `JobId(0)` from
    /// `Job::*` constructors signals "unsubmitted."
    next_job_id: u64,
    graph: GraphSnapshot,
    needs_solve: bool,
    simulation_time: Option<f64>,
    /// Per-directed-link cache of the most recently computed
    /// `next_event_ut`, keyed by `(from, to)`. Cleared on
    /// `invalidate()`. Hits dominate at scale: most pairs are
    /// rate-stable within their last forecast window, so the bisection
    /// only runs on links whose events have actually arrived (or new
    /// pairs).
    horizon_cache: HashMap<(EndpointId, EndpointId), f64>,
    /// Per-unordered-pair occluder set, keyed by lex-ordered (a, b).
    /// Symmetric in (a, b) and invariant between SOI transitions of
    /// either endpoint, so it shares the horizon-cache lifecycle.
    occluder_cache: HashMap<(EndpointId, EndpointId), Vec<BodyId>>,
}

impl CommsSystem {
    pub fn new() -> Self {
        Self::default()
    }

    /// Register a ground station. The returned `EndpointId::Ground(n)`
    /// is stable across solves.
    pub fn add_ground_station(&mut self, spec: GroundStationSpec) -> EndpointId {
        let id = self.next_ground_id;
        self.next_ground_id += 1;
        let ep = spec.into_endpoint(id);
        let endpoint_id = ep.id;
        self.ground_endpoints.push(ep);
        self.needs_solve = true;
        endpoint_id
    }

    /// Designate `id` as the home endpoint for `RefreshHomePathSummaries`.
    pub fn set_home(&mut self, id: EndpointId) {
        self.home = Some(id);
    }

    pub fn home(&self) -> Option<EndpointId> {
        self.home
    }

    pub fn graph(&self) -> &GraphSnapshot {
        &self.graph
    }

    pub fn endpoints(&self) -> &[Endpoint] {
        &self.endpoints
    }

    /// Lookup an endpoint by its id from the most recent solve. Empty
    /// before the first solve.
    pub fn endpoint(&self, id: EndpointId) -> Option<&Endpoint> {
        self.endpoints.iter().find(|e| e.id == id)
    }

    pub fn simulation_time(&self) -> Option<f64> {
        self.simulation_time
    }

    pub fn needs_solve(&self) -> bool {
        self.needs_solve
    }

    /// Submit a comms job. Mints a fresh `JobId`, marks the network
    /// dirty, and returns the assigned id. Source/destination
    /// validation is deferred — flows whose endpoints don't exist at
    /// solve time naturally fall out of routing (no path → no rate).
    pub fn submit(&mut self, mut job: Job) -> JobId {
        self.next_job_id += 1;
        let id = JobId(self.next_job_id);
        job.set_id(id);
        self.jobs.push(job);
        self.needs_solve = true;
        id
    }

    /// Mark an active job as cancelled. No-op (returns false) if the
    /// job is missing or already non-active.
    pub fn cancel(&mut self, id: JobId) -> bool {
        let job = self.jobs.iter_mut().find(|j| j.id() == id);
        let job = match job {
            Some(j) => j,
            None => return false,
        };
        if job.status() != JobStatus::Active {
            return false;
        }
        job.set_status(JobStatus::Cancelled);
        job.reset_allocated_rate();
        self.needs_solve = true;
        true
    }

    pub fn jobs(&self) -> &[Job] {
        &self.jobs
    }

    pub fn job(&self, id: JobId) -> Option<&Job> {
        self.jobs.iter().find(|j| j.id() == id)
    }

    /// Earliest forecasted state-change horizon, in seconds from the
    /// last solve UT. Folds two event sources:
    ///   - next active packet completion (`remaining_bytes / allocated_rate`)
    ///   - next link bucket / occlusion crossing (each link's `next_event_ut`)
    /// `+∞` if neither has a finite event. The driver advances UT in
    /// steps no larger than this so the next event lands on a Solve
    /// boundary.
    pub fn max_tick_dt(&self) -> f64 {
        let mut min = f64::INFINITY;
        for j in &self.jobs {
            if j.status() != JobStatus::Active {
                continue;
            }
            if let Job::Packet { allocated_rate_bps, total_bytes, delivered_bytes, .. } = j {
                if *allocated_rate_bps > 0.0 {
                    let remaining = total_bytes.saturating_sub(*delivered_bytes) as f64;
                    let t = remaining / *allocated_rate_bps;
                    if t < min {
                        min = t;
                    }
                }
            }
        }
        if let Some(sim_t) = self.simulation_time {
            for link in &self.graph.links {
                let dt = link.next_event_ut - sim_t;
                if dt > 0.0 && dt < min {
                    min = dt;
                }
            }
        }
        min
    }

    /// Reactive bucket-watch for an endpoint with no `MotionModel`
    /// (active-flight / closure-only). Walks every link involving
    /// `ep`, recomputes the current quantised rate from the live
    /// position evaluator, and returns true if any link has stepped
    /// out of its cached bucket since the last Solve. The driver
    /// (KSP-side, every FixedUpdate) uses this to invalidate the
    /// network when a player vessel crosses a bucket boundary —
    /// predictive bisection on a non-Kepler trajectory gives wrong
    /// answers under thrust, so we react instead.
    pub fn any_link_bucket_difference(&self, world: &World, ep: EndpointId, ut: f64) -> bool {
        for link in &self.graph.links {
            if link.from != ep && link.to != ep {
                continue;
            }
            let from_ep = match self.endpoints.iter().find(|e| e.id == link.from) {
                Some(e) => e,
                None => continue,
            };
            let to_ep = match self.endpoints.iter().find(|e| e.id == link.to) {
                Some(e) => e,
                None => continue,
            };
            let pa = from_ep.position_at(world, ut);
            let pb = to_ep.position_at(world, ut);
            let d = (pb - pa).norm();
            let (_, rate) = best_pair(from_ep, to_ep, d);
            let max_ceiling = link_max_ceiling(from_ep, to_ep);
            let quantised = quantize(rate, max_ceiling);
            let occluders = occluder_set(
                from_ep.primary_body,
                to_ep.primary_body,
                &world.ephemeris,
            );
            let blocked = is_any_blocked(&occluders, &world.ephemeris, pa, pb, ut);
            let effective = if blocked { 0.0 } else { quantised };
            if (effective - link.rate_bps).abs() > 1e-6 {
                return true;
            }
        }
        false
    }

    /// Re-solve the graph at `ut`. Direct port of
    /// `CommunicationsNetwork.cs:177-201`.
    ///
    /// 1. Pre-settle: if dirty since last solve, BuildGraph + AllocateJobs
    ///    at the prior UT so the upcoming integration uses post-mutation
    ///    rates.
    /// 2. Integrate: advance each active job's byte counters by
    ///    `allocated_rate · dt`.
    /// 3. BuildGraph at `ut`.
    /// 4. ComputeLinkHorizons (bucket + occlusion bisection, cached).
    /// 5. AllocateJobs (water-fill water over Packet flows).
    /// 6. Refresh path-to-home summaries if a home is registered.
    pub fn solve(&mut self, world: &World, ut: f64) -> &GraphSnapshot {
        // 1. Pre-settle.
        if self.needs_solve {
            if let Some(prior_ut) = self.simulation_time {
                let prior_endpoints: Vec<Endpoint> = self
                    .ground_endpoints
                    .iter()
                    .cloned()
                    .chain(synthesise_vessel_endpoints(world))
                    .collect();
                let mut prior_graph =
                    build_graph(&prior_endpoints, world, prior_ut, &mut self.occluder_cache);
                allocate_jobs(&mut self.jobs, &mut prior_graph);
                self.graph = prior_graph;
            }
        }

        // 2. Integrate from prior_ut to ut.
        if let Some(prior_ut) = self.simulation_time {
            let dt = ut - prior_ut;
            if dt > 0.0 {
                integrate(&mut self.jobs, dt);
            }
        }
        self.simulation_time = Some(ut);

        // 3. Fresh endpoint list and graph at the new UT.
        let mut endpoints: Vec<Endpoint> =
            self.ground_endpoints.iter().cloned().collect();
        endpoints.extend(synthesise_vessel_endpoints(world));

        self.graph = build_graph(&endpoints, world, ut, &mut self.occluder_cache);

        // 4. ComputeLinkHorizons.
        compute_link_horizons(
            &endpoints,
            world,
            ut,
            &mut self.graph,
            &mut self.horizon_cache,
            &mut self.occluder_cache,
        );

        // 5. AllocateJobs.
        allocate_jobs(&mut self.jobs, &mut self.graph);

        self.endpoints = endpoints;

        // 6. Path-to-home summaries.
        if let Some(home) = self.home {
            refresh_home_path_summaries(&mut self.endpoints, &self.graph, home);
        }

        self.needs_solve = false;
        &self.graph
    }

    /// Mark the graph dirty and clear the horizon and occluder caches.
    /// Used when topology / motion / SOI changes invalidate any cached
    /// horizon or occluder set.
    pub fn invalidate(&mut self) {
        self.needs_solve = true;
        self.horizon_cache.clear();
        self.occluder_cache.clear();
    }
}

/// Walk every non-home endpoint and store its path-to-home summary on
/// the endpoint itself. Designed to run once per `solve` so per-frame
/// readers can poll cached fields without re-running `MaxRatePath::find`
/// every update — that search allocates several maps and burns GC at
/// scale. The home endpoint's own summary is reset to default; the
/// network has no notion of "home" outside this call, so leaving the
/// field set would lie about a self-link.
fn refresh_home_path_summaries(
    endpoints: &mut [Endpoint],
    graph: &GraphSnapshot,
    home: EndpointId,
) {
    // Snapshot the (id, link_max_ceiling-to-home) data we need before
    // the mutable iteration so the borrow stays clean.
    let home_ep = match endpoints.iter().find(|e| e.id == home).cloned() {
        Some(e) => e,
        None => return,
    };

    let summaries: Vec<(EndpointId, PathSummary)> = endpoints
        .iter()
        .map(|ep| {
            if ep.id == home {
                return (ep.id, PathSummary::default());
            }
            let mut s = PathSummary::default();

            if let Some(path) = max_rate_path::find(graph, ep.id, home) {
                s.has_path = true;
                let mut min_rate = f64::INFINITY;
                for &li in &path {
                    if graph.links[li].rate_bps < min_rate {
                        min_rate = graph.links[li].rate_bps;
                    }
                }
                s.bottleneck_bps = if min_rate.is_finite() { min_rate } else { 0.0 };
            }

            for l in &graph.links {
                if l.from == ep.id && l.to == home {
                    s.direct_snr = l.snr;
                    s.direct_rate_bps = l.rate_bps;
                    break;
                }
            }
            s.direct_max_rate_bps = link_max_ceiling(ep, &home_ep);

            (ep.id, s)
        })
        .collect();

    for (id, s) in summaries {
        if let Some(ep) = endpoints.iter_mut().find(|e| e.id == id) {
            ep.path_to_home = s;
        }
    }
}

/// Walk `world.vessels`, materialising one `Endpoint` per vessel that
/// carries at least one `Component::Comms`. Motion is `Kepler` around
/// the vessel's parent body (M6 has no off-rails surface — every
/// rails-tracked vessel is predictable).
fn synthesise_vessel_endpoints(world: &World) -> Vec<Endpoint> {
    let mut out = Vec::new();
    for v in &world.vessels {
        let antennas = v.comms_antennas();
        if antennas.is_empty() {
            continue;
        }
        // Abstract vessels (editor preview, pre-launch) have no orbit
        // and therefore no comms motion model — skip. Once the host
        // transitions to `Orbit`, the endpoint appears next solve.
        let Some((parent, elements)) = v.situation.as_orbit() else {
            continue;
        };
        let motion = MotionModel::Kepler { parent, elements };
        out.push(Endpoint {
            id: EndpointId::Vessel(v.id),
            name: v.name.clone(),
            kind: EndpointKind::Vessel(v.id),
            motion: Some(motion),
            primary_body: Some(parent),
            antennas,
            is_predictable: true,
            path_to_home: PathSummary::default(),
        });
    }
    out
}

/// Direct port of `CommunicationsNetwork.cs:271-307`. For every (i, j)
/// endpoint pair with antennas on both sides, compute distance, run
/// `BestPair` to find the antenna combo maximising rate, quantise
/// against the link's hardware ceiling, and gate by occlusion. Insert
/// one directed `Link` per direction.
fn build_graph(
    endpoints: &[Endpoint],
    world: &World,
    ut: f64,
    occluder_cache: &mut HashMap<(EndpointId, EndpointId), Vec<BodyId>>,
) -> GraphSnapshot {
    let n = endpoints.len();
    let mut positions: Vec<Vec3d> = Vec::with_capacity(n);
    for ep in endpoints {
        positions.push(ep.position_at(world, ut));
    }

    let mut links = Vec::new();
    for i in 0..n {
        let from = &endpoints[i];
        if from.antennas.is_empty() {
            continue;
        }
        for j in 0..n {
            if i == j {
                continue;
            }
            let to = &endpoints[j];
            if to.antennas.is_empty() {
                continue;
            }
            let distance = (positions[j] - positions[i]).norm();
            let (snr, rate) = best_pair(from, to, distance);
            let max_ceiling = link_max_ceiling(from, to);
            let quantised = quantize(rate, max_ceiling);
            let occluders = lookup_occluder_set(from, to, world, occluder_cache);
            let blocked = is_any_blocked(
                occluders,
                &world.ephemeris,
                positions[i],
                positions[j],
                ut,
            );
            let effective = if blocked { 0.0 } else { quantised };
            let mut link = Link::new(from.id, to.id, distance, snr, effective);
            link.blocked = blocked;
            links.push(link);
        }
    }

    GraphSnapshot { links, solved_ut: ut }
}

/// Lex-ordered unordered-pair key for the occluder cache.
fn unordered_key(a: EndpointId, b: EndpointId) -> (EndpointId, EndpointId) {
    if a <= b {
        (a, b)
    } else {
        (b, a)
    }
}

fn lookup_occluder_set<'a>(
    a: &Endpoint,
    b: &Endpoint,
    world: &World,
    cache: &'a mut HashMap<(EndpointId, EndpointId), Vec<BodyId>>,
) -> &'a [BodyId] {
    let key = unordered_key(a.id, b.id);
    cache
        .entry(key)
        .or_insert_with(|| occluder_set(a.primary_body, b.primary_body, &world.ephemeris))
        .as_slice()
}

/// Per-link bucket-crossing + occlusion forecast. Iterates UNORDERED
/// endpoint pairs (one pass per `{a, b}`) so the pair-level pre-screen
/// is shared between A→B and B→A. Direct port of
/// `CommunicationsNetwork.cs:309-431`.
fn compute_link_horizons(
    endpoints: &[Endpoint],
    world: &World,
    ut: f64,
    graph: &mut GraphSnapshot,
    horizon_cache: &mut HashMap<(EndpointId, EndpointId), f64>,
    occluder_cache: &mut HashMap<(EndpointId, EndpointId), Vec<BodyId>>,
) {
    let horizon_cap_ut = ut + MAX_HORIZON_SECONDS;
    let n = endpoints.len();

    // Pre-build a (from, to) → link-index map so per-direction lookups
    // are O(1) inside the unordered-pair loop.
    let mut link_by_pair: HashMap<(EndpointId, EndpointId), usize> = HashMap::new();
    for (i, link) in graph.links.iter().enumerate() {
        link_by_pair.insert((link.from, link.to), i);
    }

    for i in 0..n {
        let a = &endpoints[i];
        if a.antennas.is_empty() {
            continue;
        }
        for j in (i + 1)..n {
            let b = &endpoints[j];
            if b.antennas.is_empty() {
                continue;
            }

            let ab_idx = link_by_pair.get(&(a.id, b.id)).copied();
            let ba_idx = link_by_pair.get(&(b.id, a.id)).copied();
            if ab_idx.is_none() && ba_idx.is_none() {
                continue;
            }

            // Skip predictive bisection when either endpoint declares
            // itself unpredictable (off-rails / under-thrust).
            if !a.is_predictable || !b.is_predictable {
                if let Some(idx) = ab_idx {
                    set_and_cache_horizon(graph, idx, horizon_cap_ut, horizon_cache);
                }
                if let Some(idx) = ba_idx {
                    set_and_cache_horizon(graph, idx, horizon_cap_ut, horizon_cache);
                }
                continue;
            }

            // Cache hit on both directions: assign from cache, skip work.
            let ab_cached = ab_idx
                .and_then(|idx| {
                    horizon_cache
                        .get(&(graph.links[idx].from, graph.links[idx].to))
                        .copied()
                        .filter(|c| *c > ut)
                })
                .map(|c| (ab_idx.unwrap(), c));
            let ba_cached = ba_idx
                .and_then(|idx| {
                    horizon_cache
                        .get(&(graph.links[idx].from, graph.links[idx].to))
                        .copied()
                        .filter(|c| *c > ut)
                })
                .map(|c| (ba_idx.unwrap(), c));
            let both_cached = (ab_idx.is_none() || ab_cached.is_some())
                && (ba_idx.is_none() || ba_cached.is_some());
            if both_cached {
                if let Some((idx, ne)) = ab_cached {
                    graph.links[idx].next_event_ut = ne;
                }
                if let Some((idx, ne)) = ba_cached {
                    graph.links[idx].next_event_ut = ne;
                }
                continue;
            }

            // Per-pair filter: max distance at which any antenna pair
            // could deliver a bucket-≥1 link.
            let pair_r_max = pair_max_useful_range(a, b);

            if prescreen_always_out_of_range(a, b, world, ut, pair_r_max) {
                if let Some(idx) = ab_idx {
                    set_and_cache_horizon(graph, idx, horizon_cap_ut, horizon_cache);
                }
                if let Some(idx) = ba_idx {
                    set_and_cache_horizon(graph, idx, horizon_cap_ut, horizon_cache);
                }
                continue;
            }

            // Detailed per-direction bisection. Asymmetric antennas
            // mean each direction can transition at a different UT, so
            // we can't share the bisection result.
            if let Some(idx) = ab_idx {
                let ne = if let Some((_, c)) = ab_cached {
                    c
                } else {
                    horizon_for_link(a, b, world, ut, occluder_cache)
                };
                set_and_cache_horizon(graph, idx, ne, horizon_cache);
            }
            if let Some(idx) = ba_idx {
                let ne = if let Some((_, c)) = ba_cached {
                    c
                } else {
                    horizon_for_link(b, a, world, ut, occluder_cache)
                };
                set_and_cache_horizon(graph, idx, ne, horizon_cache);
            }
        }
    }
}

fn set_and_cache_horizon(
    graph: &mut GraphSnapshot,
    link_idx: usize,
    next_event_ut: f64,
    horizon_cache: &mut HashMap<(EndpointId, EndpointId), f64>,
) {
    graph.links[link_idx].next_event_ut = next_event_ut;
    let key = (graph.links[link_idx].from, graph.links[link_idx].to);
    horizon_cache.insert(key, next_event_ut);
}

/// Two parallel state functions per link: bucket index and occlusion
/// blocked/clear. Bisect each independently; take the min — combined
/// state would collapse adjacent transitions of distinct kinds.
fn horizon_for_link(
    from: &Endpoint,
    to: &Endpoint,
    world: &World,
    ut: f64,
    occluder_cache: &mut HashMap<(EndpointId, EndpointId), Vec<BodyId>>,
) -> f64 {
    let max_ceiling = link_max_ceiling(from, to);
    let bucket_at = |t: f64| -> i32 {
        let pa = from.position_at(world, t);
        let pb = to.position_at(world, t);
        let d = (pb - pa).norm();
        let (_, r) = best_pair(from, to, d);
        bucket_index(r, max_ceiling)
    };
    let bucket_horizon = link_horizon::next_discrete_change(ut, bucket_at);

    let occluders = lookup_occluder_set(from, to, world, occluder_cache).to_vec();
    let occlusion_horizon = if occluders.is_empty() {
        f64::INFINITY
    } else {
        let block_at = |t: f64| -> i32 {
            let pa = from.position_at(world, t);
            let pb = to.position_at(world, t);
            for bid in &occluders {
                let centre = world.ephemeris.body_position_absolute(*bid, t);
                let radius = world.ephemeris.body(*bid).radius;
                if is_blocked(pa, pb, centre, radius) {
                    return 1;
                }
            }
            0
        };
        link_horizon::next_discrete_change(ut, block_at)
    };

    bucket_horizon.min(occlusion_horizon)
}

/// Maximum range, over all `(tx, rx)` and `(rx, tx)` antenna combos,
/// at which the link could transition out of bucket 0. Beyond this,
/// both directions are permanently bucket 0.
fn pair_max_useful_range(a: &Endpoint, b: &Endpoint) -> f64 {
    let mut max_r2: f64 = 0.0;
    let n = f64::from(BUCKET_COUNT);
    let n0 = NOISE_FLOOR;

    for x in &a.antennas {
        let snr_ref_x = x.ref_snr(n0);
        if snr_ref_x <= 0.0 {
            continue;
        }
        let snr_thresh_x = (1.0 + snr_ref_x).powf(1.0 / n) - 1.0;
        if snr_thresh_x <= 0.0 {
            continue;
        }
        for y in &b.antennas {
            let r2 = x.tx_power * x.gain * y.gain / (n0 * snr_thresh_x);
            if r2 > max_r2 {
                max_r2 = r2;
            }
        }
    }
    for y in &b.antennas {
        let snr_ref_y = y.ref_snr(n0);
        if snr_ref_y <= 0.0 {
            continue;
        }
        let snr_thresh_y = (1.0 + snr_ref_y).powf(1.0 / n) - 1.0;
        if snr_thresh_y <= 0.0 {
            continue;
        }
        for x in &a.antennas {
            let r2 = y.tx_power * y.gain * x.gain / (n0 * snr_thresh_y);
            if r2 > max_r2 {
                max_r2 = r2;
            }
        }
    }
    max_r2.sqrt()
}

/// Advance each active job's byte counters by `allocated_rate · dt`.
/// Packet flows cap at `total_bytes` and flip to `Completed` when
/// drained; Broadcast/Receive accumulate their cumulative byte tallies.
fn integrate(jobs: &mut [Job], dt: f64) {
    for job in jobs {
        if job.status() != JobStatus::Active {
            continue;
        }
        let rate = job.allocated_rate_bps();
        if rate <= 0.0 {
            continue;
        }
        let bytes = rate * dt;
        match job {
            Job::Packet {
                delivered_bytes,
                total_bytes,
                allocated_rate_bps,
                status,
                ..
            } => {
                let remaining = total_bytes.saturating_sub(*delivered_bytes) as f64;
                let add = bytes.min(remaining) as u64;
                *delivered_bytes = delivered_bytes.saturating_add(add);
                if *delivered_bytes >= *total_bytes {
                    *delivered_bytes = *total_bytes;
                    *status = JobStatus::Completed;
                    *allocated_rate_bps = 0.0;
                }
            }
            Job::Broadcast { bytes_sent, .. } => {
                *bytes_sent = bytes_sent.saturating_add(bytes as u64);
            }
            Job::Receive { bytes_received, .. } => {
                *bytes_received = bytes_received.saturating_add(bytes as u64);
            }
        }
    }
}

/// Allocate Packet flows + Broadcast/Receive flows over the current
/// graph via max-rate paths + water-fill. Direct port of
/// `CommunicationsNetwork.cs:540-625`.
fn allocate_jobs(jobs: &mut [Job], graph: &mut GraphSnapshot) {
    // Reset link utilisation and per-job rates for active jobs.
    for link in graph.links.iter_mut() {
        link.used_bps = 0.0;
    }
    for j in jobs.iter_mut() {
        if j.status() == JobStatus::Active {
            j.reset_allocated_rate();
        }
    }

    let mut alloc_edges: Vec<AllocEdge> = Vec::new();
    let mut alloc_flows: Vec<AllocFlow> = Vec::new();
    let mut link_to_edge: HashMap<usize, usize> = HashMap::new();
    let mut broadcast_to_edge: HashMap<usize, usize> = HashMap::new();

    // Packet flows.
    for (ji, job) in jobs.iter().enumerate() {
        let (source, dest) = match job {
            Job::Packet { source, dest, .. } if job.status() == JobStatus::Active => {
                (*source, *dest)
            }
            _ => continue,
        };
        let path = match max_rate_path::find(graph, source, dest) {
            Some(p) => p,
            None => continue,
        };
        let mut edge_indices = Vec::with_capacity(path.len());
        for li in path {
            let ei = link_to_edge_idx(li, graph, &mut link_to_edge, &mut alloc_edges);
            edge_indices.push(ei);
        }
        let flow_idx = alloc_flows.len();
        for &ei in &edge_indices {
            alloc_edges[ei].flow_indices.push(flow_idx);
        }
        alloc_flows.push(AllocFlow {
            job_index: Some(ji),
            broadcast_index: None,
            receive_index: None,
            ceiling: f64::INFINITY,
            edge_indices,
            rate: 0.0,
            saturated: false,
        });
    }

    // Broadcast → Receive flows: one per matching (broadcast, receive)
    // pair, keyed by `topic`. Each broadcast contributes a single
    // shared budget edge so the source-side ceiling is enforced.
    let active_broadcasts: Vec<usize> = jobs
        .iter()
        .enumerate()
        .filter_map(|(i, j)| match j {
            Job::Broadcast { .. } if j.status() == JobStatus::Active => Some(i),
            _ => None,
        })
        .collect();
    let active_receives: Vec<usize> = jobs
        .iter()
        .enumerate()
        .filter_map(|(i, j)| match j {
            Job::Receive { .. } if j.status() == JobStatus::Active => Some(i),
            _ => None,
        })
        .collect();

    for &bi in &active_broadcasts {
        for &ri in &active_receives {
            let (b_source, b_topic, b_target) = match &jobs[bi] {
                Job::Broadcast { source, topic, target_rate_bps, .. } => {
                    (*source, *topic, *target_rate_bps)
                }
                _ => unreachable!(),
            };
            let (r_receiver, r_topic, r_max) = match &jobs[ri] {
                Job::Receive { receiver, topic, max_rate_bps, .. } => {
                    (*receiver, *topic, *max_rate_bps)
                }
                _ => unreachable!(),
            };
            if b_topic != r_topic {
                continue;
            }
            let path = match max_rate_path::find(graph, b_source, r_receiver) {
                Some(p) => p,
                None => continue,
            };

            // Virtual broadcast-budget edge: one per Broadcast,
            // capacity = `target_rate_bps`. Shared by every flow this
            // broadcast feeds.
            let bcast_edge_idx = match broadcast_to_edge.get(&bi) {
                Some(&idx) => idx,
                None => {
                    let new_idx = alloc_edges.len();
                    alloc_edges.push(AllocEdge {
                        capacity: b_target,
                        used: 0.0,
                        backing_link: None,
                        flow_indices: Vec::new(),
                        saturated: false,
                    });
                    broadcast_to_edge.insert(bi, new_idx);
                    new_idx
                }
            };

            let mut edge_indices = Vec::with_capacity(path.len() + 1);
            edge_indices.push(bcast_edge_idx);
            for li in path {
                let ei = link_to_edge_idx(li, graph, &mut link_to_edge, &mut alloc_edges);
                edge_indices.push(ei);
            }
            let flow_idx = alloc_flows.len();
            for &ei in &edge_indices {
                alloc_edges[ei].flow_indices.push(flow_idx);
            }
            alloc_flows.push(AllocFlow {
                job_index: None,
                broadcast_index: Some(bi),
                receive_index: Some(ri),
                ceiling: r_max,
                edge_indices,
                rate: 0.0,
                saturated: false,
            });
        }
    }

    if alloc_flows.is_empty() {
        return;
    }

    allocate(&mut alloc_flows, &mut alloc_edges);

    // Map flow rates back to jobs. Packets get their flow rate
    // directly; Broadcasts and Receives sum across all flows they
    // participate in.
    for flow in &alloc_flows {
        if let Some(ji) = flow.job_index {
            if let Job::Packet { allocated_rate_bps, .. } = &mut jobs[ji] {
                *allocated_rate_bps = flow.rate;
            }
        }
    }
    for &bi in &active_broadcasts {
        let total: f64 = alloc_flows
            .iter()
            .filter(|f| f.broadcast_index == Some(bi))
            .map(|f| f.rate)
            .sum();
        if let Job::Broadcast { allocated_rate_bps, .. } = &mut jobs[bi] {
            *allocated_rate_bps = total;
        }
    }
    for &ri in &active_receives {
        let total: f64 = alloc_flows
            .iter()
            .filter(|f| f.receive_index == Some(ri))
            .map(|f| f.rate)
            .sum();
        if let Job::Receive { allocated_rate_bps, .. } = &mut jobs[ri] {
            *allocated_rate_bps = total;
        }
    }

    // Mirror edge `used` back to the backing link's `used_bps` (real
    // edges only — virtual broadcast edges have `backing_link = None`).
    for edge in &alloc_edges {
        if let Some(li) = edge.backing_link {
            graph.links[li].used_bps = edge.used;
        }
    }
}

fn link_to_edge_idx(
    link_idx: usize,
    graph: &GraphSnapshot,
    link_to_edge: &mut HashMap<usize, usize>,
    alloc_edges: &mut Vec<AllocEdge>,
) -> usize {
    if let Some(&idx) = link_to_edge.get(&link_idx) {
        return idx;
    }
    let new_idx = alloc_edges.len();
    alloc_edges.push(AllocEdge {
        capacity: graph.links[link_idx].rate_bps,
        used: 0.0,
        backing_link: Some(link_idx),
        flow_indices: Vec::new(),
        saturated: false,
    });
    link_to_edge.insert(link_idx, new_idx);
    new_idx
}

/// Coarse distance sweep: returns true iff the pair's distance exceeds
/// `pair_r_max` at `ut` AND every sampled future UT in the search
/// window — i.e. bucket 0 across the window (modulo brief encounters
/// shorter than the sample spacing).
fn prescreen_always_out_of_range(
    a: &Endpoint,
    b: &Endpoint,
    world: &World,
    ut: f64,
    pair_r_max: f64,
) -> bool {
    if pair_r_max <= 0.0 {
        return true;
    }
    let pa0 = a.position_at(world, ut);
    let pb0 = b.position_at(world, ut);
    if (pb0 - pa0).norm() <= pair_r_max {
        return false;
    }
    let horizon = MAX_HORIZON_SECONDS;
    let samples = PRESCREEN_SAMPLES;
    let step = horizon / f64::from(samples);
    for k in 1..=samples {
        let t = ut + f64::from(k) * step;
        let pa = a.position_at(world, t);
        let pb = b.position_at(world, t);
        if (pb - pa).norm() <= pair_r_max {
            return false;
        }
    }
    true
}

/// Pick the `(tx, rx)` antenna pair maximising achievable rate, using
/// the directional formula:
///   rate = `min(max_rate_tx, max_rate_rx) · min(1, log(1+SNR) / log(1+SNR_ref(tx)))`.
/// Returns the SNR *of the winning pair* alongside its rate (not the
/// global max-SNR, which can correspond to a different pair).
pub(crate) fn best_pair(from: &Endpoint, to: &Endpoint, distance_m: f64) -> (f64, f64) {
    let mut best_rate = 0.0;
    let mut best_snr = 0.0;
    let n0 = NOISE_FLOOR;
    let r2_n0 = distance_m * distance_m * n0;

    for tx in &from.antennas {
        let ref_snr = tx.ref_snr(n0);
        if ref_snr <= 0.0 {
            continue;
        }
        let log_ref = (1.0 + ref_snr).ln();
        for rx in &to.antennas {
            let snr = if r2_n0 > 0.0 {
                tx.tx_power * tx.gain * rx.gain / r2_n0
            } else {
                f64::INFINITY
            };
            let ceiling = tx.max_rate.min(rx.max_rate);
            let ratio = (1.0 + snr).ln() / log_ref;
            let rate = ceiling * ratio.min(1.0);
            if rate > best_rate {
                best_rate = rate;
                best_snr = snr;
            }
        }
    }
    (best_snr, best_rate)
}

/// Largest possible hardware ceiling for any antenna pair on this
/// directed edge. Used as the bucketing denominator so quantised rate
/// is comparable across pair switches and distance variation.
pub(crate) fn link_max_ceiling(from: &Endpoint, to: &Endpoint) -> f64 {
    let mut max = 0.0_f64;
    for tx in &from.antennas {
        for rx in &to.antennas {
            let c = tx.max_rate.min(rx.max_rate);
            if c > max {
                max = c;
            }
        }
    }
    max
}

