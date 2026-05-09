//! Shared mutable simulation time. Buffers and solvers hold a clone
//! of the same `SimClock`; advancing it through one handle is visible
//! through every other handle. Single-threaded by design — `Rc<Cell<f64>>`
//! is enough; multi-vessel parallelism (rayon scope inside `advance`)
//! gives each worker its own clock view.
//!
//! Tests can leave a Buffer's clock as `None`; the lerp then collapses
//! to the static baseline value.

use std::cell::Cell;
use std::rc::Rc;

#[derive(Clone, Debug)]
pub struct SimClock(Rc<Cell<f64>>);

impl SimClock {
    pub fn new(ut: f64) -> Self {
        SimClock(Rc::new(Cell::new(ut)))
    }

    pub fn ut(&self) -> f64 {
        self.0.get()
    }

    pub fn set(&self, ut: f64) {
        self.0.set(ut);
    }

    pub fn advance(&self, dt: f64) {
        self.0.set(self.0.get() + dt);
    }
}

impl Default for SimClock {
    fn default() -> Self {
        SimClock::new(0.0)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn new_starts_at_given_ut() {
        let c = SimClock::new(100.0);
        assert_eq!(c.ut(), 100.0);
    }

    #[test]
    fn set_replaces_ut() {
        let c = SimClock::new(0.0);
        c.set(42.5);
        assert_eq!(c.ut(), 42.5);
    }

    #[test]
    fn advance_is_additive() {
        let c = SimClock::new(10.0);
        c.advance(2.5);
        c.advance(0.5);
        assert_eq!(c.ut(), 13.0);
    }

    #[test]
    fn clones_share_state() {
        let a = SimClock::new(0.0);
        let b = a.clone();
        a.set(5.0);
        assert_eq!(b.ut(), 5.0);
        b.advance(3.0);
        assert_eq!(a.ut(), 8.0);
    }
}
