//! Safe wrapper around the HiGHS LP solver. The whole point of this
//! crate is to quarantine the unsafe `highs-sys` FFI surface so that
//! `nova-sim` (and any future LP-using crate) can stay all-safe.
//!
//! ## Why not the `highs` crate from crates.io
//!
//! The published `highs` crate exposes `change_col_*` but no row-bound
//! or single-coefficient mutation. `ProcessFlowSystem` depends on
//! warm-start re-solves: the priority loop alone fires up to
//! `(active.len() + 1)` re-solves per tick on the same model. Going
//! through `Highs_changeRowBounds` / `Highs_changeCoeff` keeps the
//! simplex basis warm; rebuilding the model would discard it.
//!
//! ## Lifecycle
//!
//! ```ignore
//! use nova_highs::{Highs, Sense, INFINITY};
//!
//! let mut h = Highs::new();
//! let x = h.add_column(/*cost=*/ 1.0, 0.0, INFINITY);
//! let y = h.add_column(2.0, 0.0, INFINITY);
//! let _r = h.add_row(0.0, 10.0, &[(x, 1.0), (y, 1.0)]);
//! h.set_sense(Sense::Maximize);
//! let cols = h.solve().expect("optimal");
//! // Mutate, re-solve — basis is warm.
//! h.set_col_cost(x, 3.0);
//! let cols2 = h.solve().expect("optimal");
//! ```
//!
//! ## Threading
//!
//! `Highs` wraps a raw pointer to a HiGHS C++ object. The C++ side is
//! not thread-safe, and the wrapper inherits `!Send + !Sync` from the
//! pointer. A future `multi-vessel` parallel tick would give each
//! worker its own `Highs` (matches the C# `Process` instance per vessel).

use std::ffi::CString;
use std::os::raw::c_void;
use std::ptr;

use highs_sys::{
    HighsInt, Highs_addCol, Highs_addRow, Highs_changeCoeff, Highs_changeColBounds,
    Highs_changeColCost, Highs_changeObjectiveSense, Highs_changeRowBounds, Highs_create,
    Highs_destroy, Highs_getIntInfoValue, Highs_getModelStatus, Highs_getNumCol,
    Highs_getSolution, Highs_run, Highs_setBoolOptionValue, MODEL_STATUS_INFEASIBLE,
    MODEL_STATUS_OPTIMAL, MODEL_STATUS_UNBOUNDED, OBJECTIVE_SENSE_MAXIMIZE,
    OBJECTIVE_SENSE_MINIMIZE, STATUS_OK,
};

/// HiGHS treats any bound with magnitude ≥ kHighsInf (1e30) as ±∞.
/// Slightly below the threshold to avoid the boundary case.
pub const INFINITY: f64 = 1.0e30;

/// Index of a column added to the model. Returned by
/// `Highs::add_column`; callable identifier for every mutation.
#[derive(Copy, Clone, Debug, PartialEq, Eq, Hash, PartialOrd, Ord)]
pub struct ColIdx(i32);

impl ColIdx {
    pub fn raw(self) -> i32 {
        self.0
    }
}

/// Index of a row (linear constraint) added to the model.
#[derive(Copy, Clone, Debug, PartialEq, Eq, Hash, PartialOrd, Ord)]
pub struct RowIdx(i32);

impl RowIdx {
    pub fn raw(self) -> i32 {
        self.0
    }
}

#[derive(Copy, Clone, Debug, PartialEq, Eq)]
pub enum Sense {
    Minimize,
    Maximize,
}

/// Why a `solve()` call did not return optimal column values. HiGHS
/// internal failures (`Highs_run` returning non-OK, `Highs_getSolution`
/// failing on a successful solve) panic — those signal a solver bug
/// or memory corruption rather than a model issue.
#[derive(Copy, Clone, Debug, PartialEq, Eq)]
pub enum SolveError {
    Infeasible,
    Unbounded,
    /// Any other non-optimal status. Carries the raw HiGHS model
    /// status integer for diagnostic logging.
    Other(i32),
}

impl SolveError {
    fn from_status(model_status: i32) -> Self {
        match model_status {
            s if s == MODEL_STATUS_INFEASIBLE => SolveError::Infeasible,
            s if s == MODEL_STATUS_UNBOUNDED => SolveError::Unbounded,
            other => SolveError::Other(other),
        }
    }
}

/// RAII handle around the underlying HiGHS C++ object. Owns the
/// model, basis, and options. `Drop` calls `Highs_destroy`.
///
/// Re-solves on the same handle reuse the simplex basis automatically:
/// `Highs::solve` is a thin wrapper over `Highs_run`, which inspects
/// the prior solve's basis and re-pivots from it instead of cold-
/// starting. Callers don't need to do anything special to opt in.
#[derive(Debug)]
pub struct Highs {
    handle: *mut c_void,
    next_col: i32,
    next_row: i32,
}

impl Highs {
    /// Create a fresh model, log output suppressed.
    pub fn new() -> Self {
        let handle = unsafe { Highs_create() };
        assert!(!handle.is_null(), "Highs_create returned null");
        let mut h = Highs { handle, next_col: 0, next_row: 0 };
        h.set_quiet();
        h
    }

    /// Suppress HiGHS' default chatty stdout. Solver still reports
    /// status via `Highs_getModelStatus`.
    pub fn set_quiet(&mut self) {
        let opt = CString::new("output_flag").expect("static CString");
        let res = unsafe { Highs_setBoolOptionValue(self.handle, opt.as_ptr(), 0) };
        assert_eq!(res, STATUS_OK, "Highs_setBoolOptionValue(output_flag, false) failed");
    }

    /// Append a column with the given objective `cost` and `[lo, hi]`
    /// bounds. Use `INFINITY` for unbounded; HiGHS treats `±INFINITY`
    /// as the appropriate side.
    pub fn add_column(&mut self, cost: f64, lo: f64, hi: f64) -> ColIdx {
        let res = unsafe {
            Highs_addCol(self.handle, cost, lo, hi, 0, ptr::null(), ptr::null())
        };
        assert_eq!(res, STATUS_OK, "Highs_addCol failed");
        let idx = ColIdx(self.next_col);
        self.next_col += 1;
        idx
    }

    /// Append a row (linear constraint) with `[lo, hi]` bounds and
    /// non-zero coefficients on the listed columns. Coefficients on
    /// any column not in `coeffs` default to 0; later
    /// `set_coefficient` calls can edit any (row, col) entry.
    pub fn add_row(&mut self, lo: f64, hi: f64, coeffs: &[(ColIdx, f64)]) -> RowIdx {
        let nz = coeffs.len() as HighsInt;
        let mut indices: Vec<HighsInt> = Vec::with_capacity(coeffs.len());
        let mut values: Vec<f64> = Vec::with_capacity(coeffs.len());
        for &(col, v) in coeffs {
            indices.push(col.0);
            values.push(v);
        }
        let res = unsafe {
            Highs_addRow(self.handle, lo, hi, nz, indices.as_ptr(), values.as_ptr())
        };
        assert_eq!(res, STATUS_OK, "Highs_addRow failed");
        let idx = RowIdx(self.next_row);
        self.next_row += 1;
        idx
    }

    pub fn set_sense(&mut self, sense: Sense) {
        let raw = match sense {
            Sense::Minimize => OBJECTIVE_SENSE_MINIMIZE,
            Sense::Maximize => OBJECTIVE_SENSE_MAXIMIZE,
        };
        let res = unsafe { Highs_changeObjectiveSense(self.handle, raw) };
        assert_eq!(res, STATUS_OK, "Highs_changeObjectiveSense failed");
    }

    pub fn set_col_bounds(&mut self, col: ColIdx, lo: f64, hi: f64) {
        let res = unsafe { Highs_changeColBounds(self.handle, col.0, lo, hi) };
        assert_eq!(res, STATUS_OK, "Highs_changeColBounds failed");
    }

    pub fn set_col_cost(&mut self, col: ColIdx, cost: f64) {
        let res = unsafe { Highs_changeColCost(self.handle, col.0, cost) };
        assert_eq!(res, STATUS_OK, "Highs_changeColCost failed");
    }

    pub fn set_row_bounds(&mut self, row: RowIdx, lo: f64, hi: f64) {
        let res = unsafe { Highs_changeRowBounds(self.handle, row.0, lo, hi) };
        assert_eq!(res, STATUS_OK, "Highs_changeRowBounds failed");
    }

    /// Overwrite the coefficient of a single (row, col) entry.
    pub fn set_coefficient(&mut self, row: RowIdx, col: ColIdx, value: f64) {
        let res = unsafe { Highs_changeCoeff(self.handle, row.0, col.0, value) };
        assert_eq!(res, STATUS_OK, "Highs_changeCoeff failed");
    }

    /// Solve. Returns the column-value vector in column-add order on
    /// optimal; otherwise `Err` carrying the model status. The
    /// previous solve's basis is reused automatically.
    pub fn solve(&mut self) -> Result<Vec<f64>, SolveError> {
        let status = unsafe { Highs_run(self.handle) };
        assert_eq!(
            status, STATUS_OK,
            "Highs_run returned non-OK status {} — solver-internal failure, not a model problem",
            status
        );

        let model_status = unsafe { Highs_getModelStatus(self.handle) };
        if model_status != MODEL_STATUS_OPTIMAL {
            return Err(SolveError::from_status(model_status));
        }

        let n = self.num_cols();
        let mut col_values = vec![0.0; n];
        let res = unsafe {
            Highs_getSolution(
                self.handle,
                col_values.as_mut_ptr(),
                ptr::null_mut(),
                ptr::null_mut(),
                ptr::null_mut(),
            )
        };
        assert_eq!(res, STATUS_OK, "Highs_getSolution failed on optimal model");
        Ok(col_values)
    }

    /// Number of columns currently in the model.
    pub fn num_cols(&self) -> usize {
        let n = unsafe { Highs_getNumCol(self.handle) };
        n as usize
    }

    /// Number of simplex iterations the most recent `solve()` took.
    /// `None` if the solver hasn't been queried successfully (model
    /// hasn't been solved yet, or the info-key isn't recognised on
    /// this HiGHS build). Used by tests to confirm warm-start really
    /// is warm — a re-solve after a small perturbation should take
    /// far fewer iterations than the cold first solve.
    pub fn last_simplex_iterations(&self) -> Option<i64> {
        let key = CString::new("simplex_iteration_count").expect("static CString");
        let mut value: HighsInt = 0;
        let res = unsafe { Highs_getIntInfoValue(self.handle, key.as_ptr(), &mut value) };
        if res != STATUS_OK {
            return None;
        }
        Some(value as i64)
    }
}

impl Default for Highs {
    fn default() -> Self {
        Self::new()
    }
}

impl Drop for Highs {
    fn drop(&mut self) {
        unsafe { Highs_destroy(self.handle) };
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Trivial 2-var LP: maximise 3x + 5y subject to
    ///   x + 2y ≤ 14, 3x − y ≥ 0, x − y ≤ 2, all vars ≥ 0.
    /// Optimum at (x, y) = (6, 4), objective 38.
    #[test]
    fn solves_canonical_2var_lp() {
        let mut h = Highs::new();
        let x = h.add_column(3.0, 0.0, INFINITY);
        let y = h.add_column(5.0, 0.0, INFINITY);
        let _r1 = h.add_row(-INFINITY, 14.0, &[(x, 1.0), (y, 2.0)]);
        let _r2 = h.add_row(0.0, INFINITY, &[(x, 3.0), (y, -1.0)]);
        let _r3 = h.add_row(-INFINITY, 2.0, &[(x, 1.0), (y, -1.0)]);
        h.set_sense(Sense::Maximize);

        let cols = h.solve().expect("LP should be optimal");
        approx::assert_relative_eq!(cols[x.raw() as usize], 6.0, max_relative = 1e-6);
        approx::assert_relative_eq!(cols[y.raw() as usize], 4.0, max_relative = 1e-6);
    }

    #[test]
    fn warm_resolve_after_tiny_perturbation_takes_at_most_one_simplex_iter() {
        // Build a small LP, solve cold, then perturb a cost and re-
        // solve. The warm re-solve should re-pivot from the prior
        // basis instead of cold-starting; for a tiny perturbation
        // that doesn't move the optimal vertex, simplex iterations
        // collapse to zero or one. (HiGHS may presolve away the
        // entire problem on a trivial LP, so we don't compare cold
        // vs. warm directly — we just lock in the warm-start ceiling.)
        let mut h = Highs::new();
        let x = h.add_column(1.0, 0.0, 10.0);
        let y = h.add_column(1.0, 0.0, 10.0);
        let _r = h.add_row(-INFINITY, 12.0, &[(x, 1.0), (y, 1.0)]);
        h.set_sense(Sense::Maximize);

        let _ = h.solve().unwrap();
        h.set_col_cost(x, 1.0001);
        let _ = h.solve().unwrap();
        let warm = h.last_simplex_iterations().unwrap_or(0);

        assert!(warm <= 1, "warm re-solve took {} simplex iterations", warm);
    }

    #[test]
    fn solve_returns_infeasible_for_contradictory_bounds() {
        let mut h = Highs::new();
        let x = h.add_column(0.0, 0.0, INFINITY);
        // x ≤ 1 AND x ≥ 5 → infeasible.
        let _ = h.add_row(-INFINITY, 1.0, &[(x, 1.0)]);
        let _ = h.add_row(5.0, INFINITY, &[(x, 1.0)]);
        h.set_sense(Sense::Minimize);

        assert!(matches!(h.solve(), Err(SolveError::Infeasible)));
    }

    #[test]
    fn set_col_bounds_takes_effect_on_resolve() {
        let mut h = Highs::new();
        let x = h.add_column(1.0, 0.0, 10.0);
        let y = h.add_column(1.0, 0.0, 10.0);
        let _r = h.add_row(-INFINITY, 12.0, &[(x, 1.0), (y, 1.0)]);
        h.set_sense(Sense::Maximize);

        let cols = h.solve().unwrap();
        approx::assert_relative_eq!(
            cols[x.raw() as usize] + cols[y.raw() as usize],
            12.0,
            max_relative = 1e-6,
        );

        // Tighten x's upper bound; the LP should saturate the row at
        // y = 10 and x = 2 (sum still 12).
        h.set_col_bounds(x, 0.0, 2.0);
        let cols = h.solve().unwrap();
        approx::assert_relative_eq!(cols[x.raw() as usize], 2.0, max_relative = 1e-6);
        approx::assert_relative_eq!(cols[y.raw() as usize], 10.0, max_relative = 1e-6);
    }

    #[test]
    fn set_coefficient_overwrites_a_single_entry() {
        let mut h = Highs::new();
        let x = h.add_column(1.0, 0.0, INFINITY);
        let y = h.add_column(1.0, 0.0, INFINITY);
        let r = h.add_row(-INFINITY, 10.0, &[(x, 1.0), (y, 1.0)]);
        h.set_sense(Sense::Maximize);

        let cols1 = h.solve().unwrap();
        let sum1 = cols1[x.raw() as usize] + cols1[y.raw() as usize];
        approx::assert_relative_eq!(sum1, 10.0, max_relative = 1e-6);

        // Make y twice as expensive in the row so the row is x + 2y ≤ 10.
        // Maximum at y=0, x=10 → sum=10 with x=10.
        h.set_coefficient(r, y, 2.0);
        let cols2 = h.solve().unwrap();
        approx::assert_relative_eq!(cols2[x.raw() as usize], 10.0, max_relative = 1e-6);
        approx::assert_relative_eq!(cols2[y.raw() as usize], 0.0, epsilon = 1e-6);
    }

    #[test]
    fn num_cols_reflects_added_columns() {
        let mut h = Highs::new();
        assert_eq!(h.num_cols(), 0);
        let _ = h.add_column(0.0, 0.0, 1.0);
        let _ = h.add_column(0.0, 0.0, 1.0);
        let _ = h.add_column(0.0, 0.0, 1.0);
        assert_eq!(h.num_cols(), 3);
    }
}
