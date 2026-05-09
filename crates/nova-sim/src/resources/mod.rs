//! Cross-cutting resource oracles. These are pure-ish helpers that
//! consult `Ephemeris` to answer "when does X happen for a vessel in
//! this orbit at this time" questions. Components don't own them; the
//! per-tick driver feeds the answers into the LP via the
//! `WorldContext` it threads through `Vessel::tick`.

pub mod solar_forecaster;

pub use solar_forecaster::{Orbit, PanelGeometry, SolarEvent, SolarForecaster};
