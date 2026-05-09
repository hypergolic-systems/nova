//! Where a vessel is.
//!
//! Most vessels orbit a body — that's `Situation::Orbit`. But the world
//! also needs to model vessels that exist without a physical location:
//!
//!   - **Editor preview**: a craft in the VAB/SPH being designed. It
//!     has parts, components, resources, can be ticked for d/v +
//!     resource-flow checks, but it isn't anywhere in space.
//!   - **Just-created**: in the brief window between the FFI host
//!     spawning a vessel and committing its initial orbit, the vessel
//!     is real but unplaced.
//!
//! Both cases collapse to `Situation::Abstract`. Solar forecasting,
//! ephemeris position queries, and comms motion models early-out
//! against this variant — there's no orbit to integrate, no shadow to
//! check, no distance to neighbour.
//!
//! Future variants (`Landed`, etc.) plug in here. The enum is the
//! single chokepoint everything else dispatches against.

use crate::ephem::BodyId;
use crate::orbit::OrbitalElements;

#[derive(Clone, Copy, Debug, PartialEq)]
pub enum Situation {
    /// Exists in the world but has no physical location. Solar/comms/
    /// ephem queries skip these vessels; they still tick resource flow.
    Abstract,
    /// Keplerian orbit around `parent`.
    Orbit {
        parent: BodyId,
        orbit: OrbitalElements,
    },
}

impl Situation {
    pub fn orbit(parent: BodyId, orbit: OrbitalElements) -> Self {
        Situation::Orbit { parent, orbit }
    }

    /// `Some((parent, orbit))` for `Orbit`, `None` for `Abstract`.
    pub fn as_orbit(&self) -> Option<(BodyId, OrbitalElements)> {
        match *self {
            Situation::Orbit { parent, orbit } => Some((parent, orbit)),
            Situation::Abstract => None,
        }
    }

    pub fn parent_body(&self) -> Option<BodyId> {
        self.as_orbit().map(|(p, _)| p)
    }

    pub fn orbital_elements(&self) -> Option<OrbitalElements> {
        self.as_orbit().map(|(_, o)| o)
    }

    pub fn is_abstract(&self) -> bool {
        matches!(self, Situation::Abstract)
    }
}
