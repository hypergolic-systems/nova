//! Per-tick oracle bundle threaded into `Vessel::tick`.
//!
//! `World::tick` builds one `WorldContext` from `&self.ephemeris` and
//! passes it down to each vessel. The context carries world-aware
//! oracles (currently just `SolarForecaster`; comms-route, gravity-loss,
//! etc. slot in later) so the per-vessel solver can answer "when's the
//! next shadow boundary" without holding `&World` itself — Vessel-side
//! code stays world-free except for this one borrow.

use crate::ephem::Ephemeris;
use crate::resources::SolarForecaster;

pub struct WorldContext<'a> {
    pub solar: SolarForecaster<'a>,
}

impl<'a> WorldContext<'a> {
    pub fn new(ephemeris: &'a Ephemeris) -> Self {
        WorldContext {
            solar: SolarForecaster::new(ephemeris),
        }
    }
}
