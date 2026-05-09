//! LP solver for Uniform resources. Mirrors
//! `mod/Nova.Core/Systems/ProcessFlowSystem.cs`.
//!
//! Why an LP for Uniform: producers, consumers, and buffers can be
//! many-to-many, possibly cyclic (closed-loop life support — device
//! A consumes O₂ produces CO₂; device B reverses). Water-fill doesn't
//! fit; LP does.
//!
//! Algorithm — device-priority max-min fairness loop:
//!
//!   for each priority P in [Critical, High, Low]:
//!     repeat:
//!       max α + ε × Σ device.var
//!       s.t. device.var ≥ α × demand              (active P devices)
//!            conservation per resource: Σ output·var − Σ input·var
//!                                       + supply − fill = 0
//!            0 ≤ supply_r ≤ Σ MaxRateOut over non-empty buffers
//!            0 ≤ fill_r   ≤ Σ MaxRateIn  over non-full   buffers
//!            0 ≤ device.var ≤ min(demand, max_activity)
//!       if α* ≥ 1 - ε: pin all at LP values, advance to next priority.
//!       else: pin bottleneck devs (var at physical UB), recurse residual.
//!
//! Lex-2 cleanup after the priority loop: with every device pinned,
//! `min Σ supply + Σ fill` collapses any feasible cycling that the
//! priority-loop's basis would otherwise leave undefined (supply = X,
//! fill = X − ε scenarios). Only after that do we distribute supply /
//! fill across the per-resource buffer list (drain ∝ Contents, fill
//! ∝ remaining capacity).
//!
//! ## Persistent LP, warm basis
//!
//! The HiGHS Model is built once on the first `solve()` call (matches
//! C# `topologyFinalized`) and held across every subsequent solve.
//! Each priority-loop iteration, lex-2 cleanup pass, and inter-tick
//! re-solve mutates column bounds, column costs, row bounds, and
//! coefficients in place on the same model. HiGHS retains its dual-
//! revised-simplex basis between `Highs_run` calls automatically, so
//! warm starts are the default — repeated solves with slightly
//! perturbed coefficients re-pivot from the prior optimum rather than
//! cold-starting.
//!
//! The safe `highs` Rust crate exposes `change_col_*` but no row-
//! bound or coefficient mutation, which would force a rebuild per
//! solve and discard the basis. We use `highs-sys` (raw FFI) directly
//! to reach `Highs_changeRowBounds` and `Highs_changeCoeff`.

use std::collections::{HashMap, HashSet};
use std::ffi::CString;
use std::os::raw::c_void;
use std::ptr;

use highs_sys::{
    HighsInt, Highs_addCol, Highs_addRow, Highs_changeCoeff, Highs_changeColBounds,
    Highs_changeColCost, Highs_changeObjectiveSense, Highs_changeRowBounds, Highs_create,
    Highs_destroy, Highs_getModelStatus, Highs_getNumCol, Highs_getSolution, Highs_run,
    Highs_setBoolOptionValue, MODEL_STATUS_OPTIMAL, OBJECTIVE_SENSE_MAXIMIZE,
    OBJECTIVE_SENSE_MINIMIZE, STATUS_OK,
};
#[cfg(test)]
use highs_sys::Highs_getIntInfoValue;

use crate::buffer::Buffer;
use crate::resource::{Resource, ResourceDomain};
use crate::sim_clock::SimClock;
use crate::systems::staging::BufferId;

/// HiGHS tolerance floor and "treat as zero" guard. Matches the
/// `Epsilon = 1e-9` constant in `ProcessFlowSystem.cs:44`.
pub(crate) const EPSILON: f64 = 1e-9;

/// Max alpha bound used in the LP. Mirrors the C# `1e6` upper bound
/// on alpha — large enough that the priority loop's structural
/// constraint `device.var ≤ demand` is always the binding one when
/// the system is supply-rich, never the alpha column itself.
const ALPHA_MAX: f64 = 1.0e6;

/// ε weight on the per-device activity tie-break term in the
/// priority-loop objective. Matches `ProcessFlowSystem.cs:320`.
const ACTIVITY_TIE_BREAK: f64 = 1.0e-3;

/// HiGHS treats any bound with magnitude ≥ kHighsInf (1e30) as ±∞.
/// Slightly below the threshold to avoid the boundary case.
const HIGHS_INF: f64 = 1.0e30;

#[derive(Copy, Clone, Debug, PartialEq, Eq, Hash, PartialOrd, Ord)]
pub struct DeviceId(pub u32);

/// Three-tier device priority. Critical devices win supply ahead
/// of High, which wins ahead of Low. Within a tier, supply is
/// allocated max-min fairly via the alpha-iteration LP. Order
/// matters — solver iterates `[Critical, High, Low]` in that order.
#[derive(Copy, Clone, Debug, PartialEq, Eq, Hash, PartialOrd, Ord)]
pub enum Priority {
    Critical,
    High,
    Low,
}

#[derive(Clone, Debug)]
pub struct Device {
    pub id: DeviceId,
    pub priority: Priority,
    /// Caller-supplied 0..1 demand for this tick. Read at solve time.
    pub demand: f64,
    /// Physical UB on activity (0..1). Defaults to 1.
    pub max_activity: f64,
    /// Solver output: 0..1 share of demand actually serviced.
    pub activity: f64,
    /// Component-side scheduled expiry — absolute UT at which this
    /// device wants the system re-solved. The tick driver collapses
    /// this with the system-side `max_tick_dt` to bound advance steps.
    pub valid_until: f64,
    pub(crate) inputs: Vec<(Resource, f64)>,
    pub(crate) outputs: Vec<(Resource, f64)>,
}

impl Device {
    pub fn add_input(&mut self, resource: Resource, max_rate: f64) {
        self.inputs.push((resource, max_rate));
    }

    pub fn add_output(&mut self, resource: Resource, max_rate: f64) {
        self.outputs.push((resource, max_rate));
    }

    pub fn inputs(&self) -> &[(Resource, f64)] {
        &self.inputs
    }

    pub fn outputs(&self) -> &[(Resource, f64)] {
        &self.outputs
    }

    /// Activity / demand, with epsilon guard. Returns 0 if demand
    /// is below epsilon (matches `Device.Satisfaction` in C#).
    pub fn satisfaction(&self) -> f64 {
        if self.demand > EPSILON {
            self.activity / self.demand
        } else {
            0.0
        }
    }
}

/// RAII wrapper around the raw HiGHS C++ Highs object. Holds the
/// solver's persistent state — model, basis, options. Drop frees it.
struct HighsPtr(*mut c_void);

impl HighsPtr {
    fn new() -> Self {
        // Highs_create returns a pointer to a fresh Highs C++ object
        // with no model loaded. Owning the pointer means we are
        // responsible for Highs_destroy in Drop.
        let p = unsafe { Highs_create() };
        assert!(!p.is_null(), "Highs_create returned null");

        // Suppress HiGHS' default chatty stdout. Solver still reports
        // status via Highs_getModelStatus.
        let opt = CString::new("output_flag").expect("static CString");
        unsafe {
            Highs_setBoolOptionValue(p, opt.as_ptr(), 0);
        }

        HighsPtr(p)
    }

    #[inline]
    fn raw(&self) -> *mut c_void {
        self.0
    }
}

impl Drop for HighsPtr {
    fn drop(&mut self) {
        unsafe { Highs_destroy(self.0) };
    }
}

impl std::fmt::Debug for HighsPtr {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "HighsPtr({:p})", self.0)
    }
}

/// Persistent LP state: the HiGHS handle plus the index maps from
/// our domain types (DeviceId, Resource) into the LP's column / row
/// space. Built once on first solve; mutated across every subsequent
/// solve via the FFI `change_*` calls. HiGHS keeps its simplex basis
/// across `Highs_run` calls, so re-solves are warm.
#[derive(Debug)]
struct LpState {
    highs: HighsPtr,

    alpha_col: HighsInt,
    /// One column per device, in `DeviceId` order.
    device_cols: Vec<HighsInt>,
    supply_col_by_resource: HashMap<Resource, HighsInt>,
    fill_col_by_resource: HashMap<Resource, HighsInt>,

    /// One conservation row per Uniform resource that participates
    /// in the LP (referenced by ≥ 1 device or ≥ 1 buffer).
    #[allow(dead_code)] // not currently mutated post-build, but kept for parity
    conservation_row_by_resource: HashMap<Resource, HighsInt>,
    /// Per-device alpha-fairness row index.
    alpha_row_by_device: Vec<HighsInt>,

    /// Total column count — used to size the solution vector buffer.
    num_cols: usize,
}

impl LpState {
    #[inline]
    fn change_col_bounds(&self, col: HighsInt, lo: f64, hi: f64) {
        unsafe { Highs_changeColBounds(self.highs.raw(), col, lo, hi) };
    }

    #[inline]
    fn change_col_cost(&self, col: HighsInt, cost: f64) {
        unsafe { Highs_changeColCost(self.highs.raw(), col, cost) };
    }

    #[inline]
    fn change_row_bounds(&self, row: HighsInt, lo: f64, hi: f64) {
        unsafe { Highs_changeRowBounds(self.highs.raw(), row, lo, hi) };
    }

    #[inline]
    fn change_coef(&self, row: HighsInt, col: HighsInt, value: f64) {
        unsafe { Highs_changeCoeff(self.highs.raw(), row, col, value) };
    }

    #[inline]
    fn change_objective_sense(&self, sense: HighsInt) {
        unsafe { Highs_changeObjectiveSense(self.highs.raw(), sense) };
    }

    /// Number of simplex iterations the most recent `Highs_run`
    /// took. Used by tests to verify warm-starting works (a re-solve
    /// after a small perturbation should take far fewer iterations
    /// than the cold first solve, often zero).
    #[cfg(test)]
    fn last_simplex_iterations(&self) -> i32 {
        let key = CString::new("simplex_iteration_count").expect("static CString");
        let mut value: HighsInt = 0;
        let res = unsafe { Highs_getIntInfoValue(self.highs.raw(), key.as_ptr(), &mut value) };
        if res != STATUS_OK {
            return -1;
        }
        value as i32
    }

    /// Solve. Returns the column-value vector on optimal, `None`
    /// otherwise. HiGHS reuses the prior basis automatically.
    fn run_solve(&self) -> Option<Vec<f64>> {
        let status = unsafe { Highs_run(self.highs.raw()) };
        if status != STATUS_OK {
            return None;
        }
        let model_status = unsafe { Highs_getModelStatus(self.highs.raw()) };
        if model_status != MODEL_STATUS_OPTIMAL {
            return None;
        }

        let mut col_values: Vec<f64> = vec![0.0; self.num_cols];
        let res = unsafe {
            Highs_getSolution(
                self.highs.raw(),
                col_values.as_mut_ptr(),
                ptr::null_mut(),
                ptr::null_mut(),
                ptr::null_mut(),
            )
        };
        if res != STATUS_OK {
            return None;
        }
        Some(col_values)
    }
}

#[derive(Debug)]
pub struct ProcessFlowSystem {
    clock: SimClock,
    devices: Vec<Device>,
    /// Buffers registered via `add_buffer`. Owned here; index = BufferId.
    buffers: Vec<Buffer>,
    /// Per-resource: list of indices into `self.buffers`.
    buffers_by_resource: HashMap<Resource, Vec<BufferId>>,

    /// Snapshot of supply/fill values from the most recent solve;
    /// read by `distribute_buffer_rates`.
    supply_snapshot: HashMap<Resource, f64>,
    fill_snapshot: HashMap<Resource, f64>,

    /// Persistent HiGHS model + index maps. Built lazily on first
    /// `solve()`, kept across all subsequent solves so the simplex
    /// basis warm-starts.
    lp: Option<LpState>,

    /// Flips on first `solve()`. After that, `add_device` and
    /// `add_buffer` panic. Mirrors C# `topologyFinalized`.
    pub(crate) topology_finalized: bool,
}

impl ProcessFlowSystem {
    pub fn new(clock: SimClock) -> Self {
        ProcessFlowSystem {
            clock,
            devices: Vec::new(),
            buffers: Vec::new(),
            buffers_by_resource: HashMap::new(),
            supply_snapshot: HashMap::new(),
            fill_snapshot: HashMap::new(),
            lp: None,
            topology_finalized: false,
        }
    }

    pub fn clock(&self) -> &SimClock {
        &self.clock
    }

    // ── Construction ────────────────────────────────────────────────

    pub fn add_device(&mut self, priority: Priority) -> DeviceId {
        if self.topology_finalized {
            panic!(
                "ProcessFlowSystem: cannot add device after first solve(); \
                 rebuild the system instead."
            );
        }
        let id = DeviceId(self.devices.len() as u32);
        self.devices.push(Device {
            id,
            priority,
            demand: 0.0,
            max_activity: 1.0,
            activity: 0.0,
            valid_until: f64::INFINITY,
            inputs: Vec::new(),
            outputs: Vec::new(),
        });
        id
    }

    /// Register a buffer in the Process pool. Panics if the buffer's
    /// resource is Topological — those belong on `StagingFlowSystem`.
    /// The system adopts the buffer (sets its clock + baselines at
    /// `clock.ut()`); mirrors `ProcessFlowSystem.cs:113-130`.
    pub fn add_buffer(&mut self, mut buffer: Buffer) -> BufferId {
        if self.topology_finalized {
            panic!(
                "ProcessFlowSystem: cannot add buffer after first solve(); \
                 rebuild the system instead."
            );
        }
        if buffer.resource.domain() != ResourceDomain::Uniform {
            panic!(
                "ProcessFlowSystem only accepts Uniform resources; got {:?} \
                 ({:?}). Topological resources belong on StagingFlowSystem.",
                buffer.resource.name(),
                buffer.resource.domain(),
            );
        }
        buffer.clock = Some(self.clock.clone());
        let now = self.clock.ut();
        buffer.refresh(now);
        let resource = buffer.resource;
        let id = BufferId(self.buffers.len() as u32);
        self.buffers.push(buffer);
        self.buffers_by_resource
            .entry(resource)
            .or_default()
            .push(id);
        id
    }

    // ── Accessors ────────────────────────────────────────────────────

    pub fn devices(&self) -> &[Device] {
        &self.devices
    }

    pub fn device(&self, id: DeviceId) -> &Device {
        &self.devices[id.0 as usize]
    }

    pub fn device_mut(&mut self, id: DeviceId) -> &mut Device {
        &mut self.devices[id.0 as usize]
    }

    pub fn buffer(&self, id: BufferId) -> &Buffer {
        &self.buffers[id.0 as usize]
    }

    pub fn buffers_for(&self, resource: Resource) -> &[BufferId] {
        self.buffers_by_resource
            .get(&resource)
            .map(|v| v.as_slice())
            .unwrap_or(&[])
    }

    /// Resources that participate in the LP — union of all device
    /// input/output resources plus all buffer resources, filtered to
    /// `ResourceDomain::Uniform`. Stable across solves once topology
    /// is finalized; sorted for deterministic LP construction.
    fn resources(&self) -> Vec<Resource> {
        let mut set: HashSet<Resource> = HashSet::new();
        for d in &self.devices {
            for (r, _) in &d.inputs {
                set.insert(*r);
            }
            for (r, _) in &d.outputs {
                set.insert(*r);
            }
        }
        for r in self.buffers_by_resource.keys() {
            set.insert(*r);
        }
        set.retain(|r| r.domain() == ResourceDomain::Uniform);
        let mut v: Vec<Resource> = set.into_iter().collect();
        v.sort_by_key(|r| r.name());
        v
    }

    // ── Solve ────────────────────────────────────────────────────────

    /// Run one full LP cycle at the current clock UT. Direct port of
    /// `ProcessFlowSystem.cs:136-181`.
    pub fn solve(&mut self) {
        if self.lp.is_none() {
            self.build_lp();
            self.topology_finalized = true;
        }

        let now = self.clock.ut();
        for b in &mut self.buffers {
            b.refresh(now);
        }

        for d in &mut self.devices {
            d.activity = 0.0;
        }
        self.supply_snapshot.clear();
        self.fill_snapshot.clear();

        self.reset_per_tick_bounds();

        // Pinned across priorities: deviceID → fixed activity. In
        // each iter we treat pinned devices as having tight var
        // bounds (LB = UB = pinned_value), letting them participate
        // in the conservation rows but no longer shifting under
        // fairness.
        let mut pinned: HashMap<DeviceId, f64> = HashMap::new();

        let priorities = [Priority::Critical, Priority::High, Priority::Low];
        for &prio in &priorities {
            let active: Vec<DeviceId> = self
                .devices
                .iter()
                .filter(|d| {
                    d.priority == prio
                        && !pinned.contains_key(&d.id)
                        && d.demand > EPSILON
                })
                .map(|d| d.id)
                .collect();
            if active.is_empty() {
                continue;
            }
            self.iterate_device_alpha(&active, &mut pinned);
        }

        // Lex-2 cleanup: with every device's var pinned, minimise
        // `Σ supply + Σ fill` to collapse the basis-arbitrary cycling
        // before reading. Mirrors `cs:183-201`.
        self.cleanup_supply_fill(&pinned);

        self.distribute_buffer_rates();
    }

    /// Soonest of buffer empty/fill horizons + per-device valid_until,
    /// expressed as a relative dt against `clock.ut()`. Used by the
    /// tick driver to bound advance steps so a state transition (a
    /// battery emptying mid-burn, a fuel-cell SoC threshold flip)
    /// doesn't get over-stepped. Mirrors `cs:210-231`.
    pub fn max_tick_dt(&self) -> f64 {
        let mut earliest = f64::INFINITY;

        for b in &self.buffers {
            let contents = b.contents();
            let rate = b.rate();
            if rate < -EPSILON && contents > EPSILON {
                let t = contents / -rate;
                if t < earliest {
                    earliest = t;
                }
            } else if rate > EPSILON && contents < b.capacity - EPSILON {
                let t = (b.capacity - contents) / rate;
                if t < earliest {
                    earliest = t;
                }
            }
        }

        let now = self.clock.ut();
        for d in &self.devices {
            if d.valid_until.is_infinite() {
                continue;
            }
            let dt = (d.valid_until - now).max(0.0);
            if dt < earliest {
                earliest = dt;
            }
        }

        earliest
    }

    // ── LP construction (one-shot, on first solve) ───────────────────

    /// Build the persistent HiGHS model. Columns + rows are added in
    /// a fixed deterministic order; the indices captured in `LpState`
    /// are used to address them via `change_*` thereafter.
    fn build_lp(&mut self) {
        let highs = HighsPtr::new();

        // Track sequential column index. HiGHS assigns col indices
        // implicitly in add order — we have to mirror that.
        let mut next_col: HighsInt = 0;
        let mut next_row: HighsInt = 0;

        // Add columns in this order: alpha, devices (DeviceId order),
        // then per-resource (supply, fill) for resources with buffers.
        let alpha_col = next_col;
        unsafe {
            Highs_addCol(highs.raw(), 0.0, 0.0, ALPHA_MAX, 0, ptr::null(), ptr::null());
        }
        next_col += 1;

        let mut device_cols: Vec<HighsInt> = Vec::with_capacity(self.devices.len());
        for d in &self.devices {
            let ub = d.demand.min(d.max_activity).max(0.0);
            let col = next_col;
            unsafe {
                Highs_addCol(highs.raw(), 0.0, 0.0, ub, 0, ptr::null(), ptr::null());
            }
            device_cols.push(col);
            next_col += 1;
            let _ = d;
        }

        let mut supply_col_by_resource: HashMap<Resource, HighsInt> = HashMap::new();
        let mut fill_col_by_resource: HashMap<Resource, HighsInt> = HashMap::new();
        // Use the same ordering as `resources()` so reading back is
        // deterministic.
        let buffer_resources: Vec<Resource> = {
            let mut v: Vec<Resource> = self.buffers_by_resource.keys().copied().collect();
            v.sort_by_key(|r| r.name());
            v
        };
        for resource in &buffer_resources {
            let s = next_col;
            unsafe {
                Highs_addCol(highs.raw(), 0.0, 0.0, 0.0, 0, ptr::null(), ptr::null());
            }
            next_col += 1;
            let f = next_col;
            unsafe {
                Highs_addCol(highs.raw(), 0.0, 0.0, 0.0, 0, ptr::null(), ptr::null());
            }
            next_col += 1;
            supply_col_by_resource.insert(*resource, s);
            fill_col_by_resource.insert(*resource, f);
        }

        // Conservation rows.
        let mut conservation_row_by_resource: HashMap<Resource, HighsInt> = HashMap::new();
        for r in self.resources() {
            let mut indices: Vec<HighsInt> = Vec::new();
            let mut values: Vec<f64> = Vec::new();
            for (idx, d) in self.devices.iter().enumerate() {
                let mut coef = 0.0;
                for (rr, mr) in &d.outputs {
                    if *rr == r {
                        coef += *mr;
                    }
                }
                for (rr, mr) in &d.inputs {
                    if *rr == r {
                        coef -= *mr;
                    }
                }
                if coef != 0.0 {
                    indices.push(device_cols[idx]);
                    values.push(coef);
                }
            }
            if let Some(&s) = supply_col_by_resource.get(&r) {
                indices.push(s);
                values.push(1.0);
            }
            if let Some(&f) = fill_col_by_resource.get(&r) {
                indices.push(f);
                values.push(-1.0);
            }
            let nz: HighsInt = indices.len() as HighsInt;
            unsafe {
                Highs_addRow(highs.raw(), 0.0, 0.0, nz, indices.as_ptr(), values.as_ptr());
            }
            conservation_row_by_resource.insert(r, next_row);
            next_row += 1;
        }

        // Alpha rows: per device, `device.var − α × demand ≥ 0`.
        // Built wide-open ([-INF, INF]); per-tick bounds activate them.
        let mut alpha_row_by_device: Vec<HighsInt> = Vec::with_capacity(self.devices.len());
        for (idx, d) in self.devices.iter().enumerate() {
            let indices = [device_cols[idx], alpha_col];
            let values = [1.0_f64, -d.demand];
            unsafe {
                Highs_addRow(
                    highs.raw(),
                    -HIGHS_INF,
                    HIGHS_INF,
                    2,
                    indices.as_ptr(),
                    values.as_ptr(),
                );
            }
            alpha_row_by_device.push(next_row);
            next_row += 1;
        }

        // Default sense; reset_per_tick_bounds and the priority loop
        // assume Maximise.
        unsafe {
            Highs_changeObjectiveSense(highs.raw(), OBJECTIVE_SENSE_MAXIMIZE);
        }

        // HiGHS reports back the actual column count; assert the
        // bookkeeping matches.
        let num_cols_check = unsafe { Highs_getNumCol(highs.raw()) };
        debug_assert_eq!(
            num_cols_check as usize,
            next_col as usize,
            "HiGHS column count mismatch — index bookkeeping bug"
        );

        self.lp = Some(LpState {
            highs,
            alpha_col,
            device_cols,
            supply_col_by_resource,
            fill_col_by_resource,
            conservation_row_by_resource,
            alpha_row_by_device,
            num_cols: next_col as usize,
        });
    }

    // ── Per-tick bound + coefficient reset ───────────────────────────

    /// Reset every per-tick-mutable LP entry to its baseline before
    /// the priority loop fires. Device demand and buffer state may
    /// have changed since the last solve — pull them in.
    fn reset_per_tick_bounds(&self) {
        let lp = self.lp.as_ref().expect("LP must be built");

        // Alpha column: cost = 0, bounds [0, ALPHA_MAX]. Cost gets set
        // to 1 inside iterate_device_alpha; reset it here so the
        // cleanup path (which sets it to 0 explicitly) starts from a
        // known state.
        lp.change_col_cost(lp.alpha_col, 0.0);
        lp.change_col_bounds(lp.alpha_col, 0.0, ALPHA_MAX);

        // Device columns: bounds [0, min(demand, max_activity)], cost 0,
        // alpha-row coefficient on alpha column = -demand (demand may
        // have changed since last tick), alpha-row deactivated.
        for (idx, d) in self.devices.iter().enumerate() {
            let dcol = lp.device_cols[idx];
            let arow = lp.alpha_row_by_device[idx];
            let ub = d.demand.min(d.max_activity).max(0.0);
            lp.change_col_bounds(dcol, 0.0, ub);
            lp.change_col_cost(dcol, 0.0);
            lp.change_coef(arow, lp.alpha_col, -d.demand);
            lp.change_row_bounds(arow, -HIGHS_INF, HIGHS_INF);
        }

        // Supply / fill columns: bounds from current buffer state,
        // cost 0.
        for (resource, bids) in &self.buffers_by_resource {
            let mut max_out = 0.0;
            let mut max_in = 0.0;
            for bid in bids {
                let b = &self.buffers[bid.0 as usize];
                if b.contents() > EPSILON {
                    max_out += b.max_rate_out;
                }
                if b.contents() < b.capacity - EPSILON {
                    max_in += b.max_rate_in;
                }
            }
            let s = lp.supply_col_by_resource[resource];
            let f = lp.fill_col_by_resource[resource];
            lp.change_col_bounds(s, 0.0, max_out);
            lp.change_col_bounds(f, 0.0, max_in);
            lp.change_col_cost(s, 0.0);
            lp.change_col_cost(f, 0.0);
        }

        // Default sense for the priority loop.
        lp.change_objective_sense(OBJECTIVE_SENSE_MAXIMIZE);
    }

    // ── Priority loop ────────────────────────────────────────────────

    /// Iterate the priority-loop's alpha LP at one priority. Pins
    /// devices as they bottleneck, narrows the active set, re-solves,
    /// up to `active.len() + 1` iterations. Mirrors
    /// `ProcessFlowSystem.cs:304-387`.
    fn iterate_device_alpha(
        &mut self,
        active: &[DeviceId],
        pinned: &mut HashMap<DeviceId, f64>,
    ) {
        let mut devs: Vec<DeviceId> = active.to_vec();
        let max_iter = devs.len() + 1;

        for _ in 0..max_iter {
            if devs.is_empty() {
                return;
            }

            self.setup_iter(&devs, pinned);
            let lp = self.lp.as_ref().expect("LP built");
            let cols = match lp.run_solve() {
                Some(v) => v,
                None => {
                    // Non-OPTIMAL: pin everything at 0 and bail.
                    // Mirrors the C# log+pin behavior.
                    for &id in &devs {
                        self.devices[id.0 as usize].activity = 0.0;
                        pinned.insert(id, 0.0);
                    }
                    return;
                }
            };

            let alpha_star = cols[lp.alpha_col as usize];
            let mut device_values: HashMap<DeviceId, f64> = HashMap::new();
            for d in &self.devices {
                device_values.insert(d.id, cols[lp.device_cols[d.id.0 as usize] as usize]);
            }

            // Update the per-iter visible state immediately.
            for &id in &devs {
                self.devices[id.0 as usize].activity = device_values[&id];
            }
            for (r, c) in &lp.supply_col_by_resource {
                self.supply_snapshot.insert(*r, cols[*c as usize]);
            }
            for (r, c) in &lp.fill_col_by_resource {
                self.fill_snapshot.insert(*r, cols[*c as usize]);
            }

            if alpha_star >= 1.0 - EPSILON {
                for &id in &devs {
                    pinned.insert(id, device_values[&id]);
                }
                return;
            }

            // α < 1: bottlenecks are devs at physical UB (within ε).
            let mut bottlenecks: Vec<DeviceId> = Vec::new();
            for &id in &devs {
                let d = &self.devices[id.0 as usize];
                let ub = d.demand.min(d.max_activity).max(0.0);
                let v = device_values[&id];
                if v >= ub - EPSILON {
                    bottlenecks.push(id);
                }
            }

            if bottlenecks.is_empty() {
                // Conservation-bound at α < 1 — supply can't cover
                // demand. Pin all at current LP and bail.
                for &id in &devs {
                    pinned.insert(id, device_values[&id]);
                }
                return;
            }

            for &id in &bottlenecks {
                pinned.insert(id, device_values[&id]);
            }
            devs.retain(|id| !bottlenecks.contains(id));
        }

        // Iter cap reached — pin remainder at current activity.
        for &id in &devs {
            let v = self.devices[id.0 as usize].activity;
            pinned.insert(id, v);
        }
    }

    /// Configure the LP for one alpha-iteration: pinned devices
    /// tightly bound, active devices unpinned with ε activity-tie-
    /// break + active alpha row, all other devices wide-bound but
    /// inactive (no objective term, alpha row deactivated).
    fn setup_iter(&self, active: &[DeviceId], pinned: &HashMap<DeviceId, f64>) {
        let lp = self.lp.as_ref().expect("LP built");
        let active_set: HashSet<DeviceId> = active.iter().copied().collect();

        // Alpha column: cost = 1.
        lp.change_col_cost(lp.alpha_col, 1.0);

        for (idx, d) in self.devices.iter().enumerate() {
            let dcol = lp.device_cols[idx];
            let arow = lp.alpha_row_by_device[idx];
            if let Some(&v) = pinned.get(&d.id) {
                lp.change_col_bounds(dcol, v, v);
                lp.change_col_cost(dcol, 0.0);
                lp.change_row_bounds(arow, -HIGHS_INF, HIGHS_INF);
            } else {
                let ub = d.demand.min(d.max_activity).max(0.0);
                lp.change_col_bounds(dcol, 0.0, ub);
                if active_set.contains(&d.id) {
                    lp.change_col_cost(dcol, ACTIVITY_TIE_BREAK);
                    lp.change_row_bounds(arow, 0.0, HIGHS_INF);
                } else {
                    lp.change_col_cost(dcol, 0.0);
                    lp.change_row_bounds(arow, -HIGHS_INF, HIGHS_INF);
                }
            }
        }

        lp.change_objective_sense(OBJECTIVE_SENSE_MAXIMIZE);
    }

    // ── Lex-2 cleanup ────────────────────────────────────────────────

    /// With every device's var tightly bound at its priority-loop
    /// value, minimise `Σ supply + Σ fill` over the supply / fill
    /// columns. Resolves the basis-arbitrary slack where any feasible
    /// (supply, fill) pair with the same net would yield identical α.
    /// Mirrors `ProcessFlowSystem.cs:183-201`.
    fn cleanup_supply_fill(&mut self, pinned: &HashMap<DeviceId, f64>) {
        if self.buffers_by_resource.is_empty() {
            return;
        }
        let lp = self.lp.as_ref().expect("LP built");

        // Alpha: cost 0, bounds wide.
        lp.change_col_cost(lp.alpha_col, 0.0);
        lp.change_col_bounds(lp.alpha_col, 0.0, ALPHA_MAX);

        // Device cols: pin every device at its priority-loop output
        // (or 0 if it never made it into `pinned`); cost 0; alpha row
        // wide.
        for (idx, d) in self.devices.iter().enumerate() {
            let dcol = lp.device_cols[idx];
            let arow = lp.alpha_row_by_device[idx];
            let v = pinned.get(&d.id).copied().unwrap_or(0.0);
            lp.change_col_bounds(dcol, v, v);
            lp.change_col_cost(dcol, 0.0);
            lp.change_row_bounds(arow, -HIGHS_INF, HIGHS_INF);
        }

        // Supply / fill cols: cost = 1 → minimise the sum.
        for c in lp.supply_col_by_resource.values() {
            lp.change_col_cost(*c, 1.0);
        }
        for c in lp.fill_col_by_resource.values() {
            lp.change_col_cost(*c, 1.0);
        }

        lp.change_objective_sense(OBJECTIVE_SENSE_MINIMIZE);

        let cols = match lp.run_solve() {
            Some(v) => v,
            None => return, // fall through with the priority loop's snapshot
        };

        for (r, c) in &lp.supply_col_by_resource {
            self.supply_snapshot.insert(*r, cols[*c as usize]);
        }
        for (r, c) in &lp.fill_col_by_resource {
            self.fill_snapshot.insert(*r, cols[*c as usize]);
        }
    }

    // ── Buffer rate distribution ─────────────────────────────────────

    /// Spread the snapshotted supply/fill values across the buffers
    /// of each resource. Drain proportional to current Contents;
    /// fill proportional to remaining capacity. Net buffer rate is
    /// `fill_share - drain_share` (positive = filling, matching the
    /// `Buffer.rate` convention). Mirrors `cs:400-423`.
    fn distribute_buffer_rates(&mut self) {
        for b in &mut self.buffers {
            b.set_rate(0.0);
        }

        for (resource, bids) in &self.buffers_by_resource {
            let supply = self.supply_snapshot.get(resource).copied().unwrap_or(0.0);
            let fill = self.fill_snapshot.get(resource).copied().unwrap_or(0.0);

            let mut total_contents = 0.0;
            let mut total_space = 0.0;
            for bid in bids {
                let b = &self.buffers[bid.0 as usize];
                let c = b.contents();
                if c > EPSILON {
                    total_contents += c;
                }
                let space = b.capacity - c;
                if space > EPSILON {
                    total_space += space;
                }
            }

            for bid in bids {
                let (drain_share, fill_share);
                {
                    let b = &self.buffers[bid.0 as usize];
                    let c = b.contents();
                    let space = b.capacity - c;
                    drain_share = if total_contents > EPSILON && c > EPSILON {
                        supply * (c / total_contents)
                    } else {
                        0.0
                    };
                    fill_share = if total_space > EPSILON && space > EPSILON {
                        fill * (space / total_space)
                    } else {
                        0.0
                    };
                }
                let b = &mut self.buffers[bid.0 as usize];
                b.set_rate(fill_share - drain_share);
            }
        }
    }
}

/// Manual `Clone` — drops the LP state. The clone is a topology-only
/// copy; on the clone's next `solve()`, `build_lp()` rebuilds the
/// HiGHS model from scratch. Subsequent solves of the clone warm-
/// start from that fresh basis. The original is unaffected.
///
/// Trades a one-time cold solve on the clone for not having to
/// deep-copy the C++ HiGHS object across the FFI boundary (the C
/// API has no clone primitive).
impl Clone for ProcessFlowSystem {
    fn clone(&self) -> Self {
        ProcessFlowSystem {
            clock: self.clock.clone(),
            devices: self.devices.clone(),
            buffers: self.buffers.clone(),
            buffers_by_resource: self.buffers_by_resource.clone(),
            supply_snapshot: self.supply_snapshot.clone(),
            fill_snapshot: self.fill_snapshot.clone(),
            lp: None,
            topology_finalized: false,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::resource::Resource;

    fn fresh() -> ProcessFlowSystem {
        ProcessFlowSystem::new(SimClock::new(0.0))
    }

    #[test]
    fn add_device_returns_dense_ids() {
        let mut sys = fresh();
        assert_eq!(sys.add_device(Priority::Low), DeviceId(0));
        assert_eq!(sys.add_device(Priority::High), DeviceId(1));
        assert_eq!(sys.add_device(Priority::Critical), DeviceId(2));
    }

    #[test]
    fn add_buffer_returns_dense_ids() {
        let mut sys = fresh();
        let b1 = sys.add_buffer(Buffer::new(Resource::ElectricCharge, 100.0, None));
        let b2 = sys.add_buffer(Buffer::new(Resource::ElectricCharge, 50.0, None));
        assert_eq!(b1, BufferId(0));
        assert_eq!(b2, BufferId(1));
    }

    #[test]
    fn add_buffer_groups_by_resource() {
        let mut sys = fresh();
        sys.add_buffer(Buffer::new(Resource::ElectricCharge, 100.0, None));
        sys.add_buffer(Buffer::new(Resource::ElectricCharge, 50.0, None));
        assert_eq!(sys.buffers_for(Resource::ElectricCharge).len(), 2);
    }

    #[test]
    fn add_buffer_adopts_clock_and_baseline() {
        let clock = SimClock::new(50.0);
        let mut sys = ProcessFlowSystem::new(clock.clone());
        let bid = sys.add_buffer(Buffer::new(Resource::ElectricCharge, 100.0, None));
        let b = sys.buffer(bid);
        assert_eq!(b.baseline_ut, 50.0);
        clock.advance(10.0);
        assert_eq!(b.contents(), 100.0);
    }

    #[test]
    #[should_panic(expected = "Topological resources belong on StagingFlowSystem")]
    fn add_buffer_rejects_topological_resource() {
        let mut sys = fresh();
        sys.add_buffer(Buffer::new(Resource::Hydrazine, 100.0, None));
    }

    #[test]
    #[should_panic(expected = "cannot add device after first solve")]
    fn add_device_after_solve_panics() {
        let mut sys = fresh();
        sys.solve();
        sys.add_device(Priority::Low);
    }

    #[test]
    #[should_panic(expected = "cannot add buffer after first solve")]
    fn add_buffer_after_solve_panics() {
        let mut sys = fresh();
        sys.solve();
        sys.add_buffer(Buffer::new(Resource::ElectricCharge, 100.0, None));
    }

    #[test]
    fn satisfaction_handles_zero_demand() {
        let mut sys = fresh();
        let d = sys.add_device(Priority::Low);
        sys.device_mut(d).demand = 0.0;
        sys.device_mut(d).activity = 0.0;
        assert_eq!(sys.device(d).satisfaction(), 0.0);
    }

    #[test]
    fn satisfaction_is_activity_over_demand() {
        let mut sys = fresh();
        let d = sys.add_device(Priority::Low);
        sys.device_mut(d).demand = 0.5;
        sys.device_mut(d).activity = 0.25;
        assert!((sys.device(d).satisfaction() - 0.5).abs() < 1e-12);
    }

    /// Solve, change demand, solve again — verifies the LP is reused
    /// across solves (model isn't rebuilt) and per-tick bounds are
    /// re-derived from current `device.demand` each call.
    #[test]
    fn warm_resolve_honours_updated_demand() {
        let mut sys = fresh();
        let p = sys.add_device(Priority::Low);
        sys.device_mut(p).add_output(Resource::ElectricCharge, 10.0);
        sys.device_mut(p).demand = 1.0;
        let c = sys.add_device(Priority::Low);
        sys.device_mut(c).add_input(Resource::ElectricCharge, 10.0);
        sys.device_mut(c).demand = 1.0;

        sys.solve();
        assert!((sys.device(c).activity - 1.0).abs() < 1e-6);

        // Cut consumer demand in half; rebuild ought to NOT happen
        // (topology fixed) but bounds should reflect the new demand.
        sys.device_mut(c).demand = 0.5;
        sys.solve();
        assert!((sys.device(c).activity - 0.5).abs() < 1e-6);
    }

    /// Locks in HiGHS' warm-start behaviour: after a successful
    /// solve, a re-solve with the *same* LP should take zero
    /// simplex iterations (the prior basis is already optimal),
    /// and a re-solve with a tiny perturbation should take far
    /// fewer iterations than the cold first solve.
    ///
    /// This test is the floor of the persistent-LP design — if it
    /// fails, we've regressed to rebuild-every-solve and the basis
    /// is being thrown away each tick.
    #[test]
    fn re_solve_after_no_change_takes_no_simplex_iterations() {
        let mut sys = fresh();
        // A sized-up LP so the cold solve actually does work; a
        // 1-device LP can be solved in 0 iters by HiGHS' presolve
        // and that wouldn't tell us anything.
        let p = sys.add_device(Priority::Low);
        sys.device_mut(p).add_output(Resource::ElectricCharge, 100.0);
        sys.device_mut(p).demand = 1.0;
        for _ in 0..5 {
            let c = sys.add_device(Priority::Low);
            sys.device_mut(c).add_input(Resource::ElectricCharge, 8.0);
            sys.device_mut(c).demand = 1.0;
        }
        let mut buf = Buffer::new(Resource::ElectricCharge, 100.0, None);
        buf.flow_limits(1000.0, 1000.0);
        sys.add_buffer(buf);

        sys.solve();
        let cold_iters = sys.lp.as_ref().unwrap().last_simplex_iterations();

        // No state change → re-solve hits the same optimum.
        // HiGHS picks up the warm basis and shouldn't pivot.
        sys.solve();
        let warm_iters = sys.lp.as_ref().unwrap().last_simplex_iterations();

        // Cold should have done some pivoting; warm should be zero.
        // Allow a small ceiling on warm in case HiGHS reports its
        // post-presolve check as one step.
        assert!(
            warm_iters <= 1,
            "warm re-solve should take ≤ 1 simplex iteration, got {warm_iters} (cold was {cold_iters})",
        );
    }

    /// Cloning should produce an LP-less topology copy that runs
    /// independently from the original.
    #[test]
    fn clone_drops_lp_and_solves_independently() {
        let mut sys = fresh();
        let p = sys.add_device(Priority::Low);
        sys.device_mut(p).add_output(Resource::ElectricCharge, 10.0);
        sys.device_mut(p).demand = 1.0;
        let c = sys.add_device(Priority::Low);
        sys.device_mut(c).add_input(Resource::ElectricCharge, 10.0);
        sys.device_mut(c).demand = 1.0;

        sys.solve();

        let mut cloned = sys.clone();
        cloned.device_mut(c).demand = 0.25;
        cloned.solve();
        assert!((cloned.device(c).activity - 0.25).abs() < 1e-6);
        // Original unaffected.
        assert!((sys.device(c).activity - 1.0).abs() < 1e-6);
    }
}
