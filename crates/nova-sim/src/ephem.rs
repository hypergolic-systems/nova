use crate::atmosphere::Atmosphere;
use crate::math::Vec3d;
use crate::orbit::OrbitalElements;

/// Stable identifier for a celestial body within a single Ephemeris.
/// Mirrors `FlightGlobals.Bodies` indexing on the KSP side.
#[derive(Copy, Clone, Debug, PartialEq, Eq, Hash, PartialOrd, Ord)]
pub struct BodyId(pub u32);

#[derive(Clone, Debug)]
pub struct Body {
    pub id: BodyId,
    pub name: String,
    /// `None` for the root star; `Some(parent)` otherwise.
    pub parent: Option<BodyId>,
    /// Gravitational parameter, m³/s².
    pub mu: f64,
    /// Equatorial radius, m.
    pub radius: f64,
    /// Sphere-of-influence radius, m. `f64::INFINITY` for the root.
    pub soi_radius: f64,
    pub atmosphere: Option<Atmosphere>,
    pub rotation: BodyRotation,
    /// Orbit relative to `parent`. `None` for the root.
    pub orbit: Option<OrbitalElements>,
}

#[derive(Copy, Clone, Debug, Default)]
pub struct BodyRotation {
    pub rotates: bool,
    pub period_seconds: f64,
    pub initial_rotation_rad: f64,
    pub tidally_locked: bool,
}

/// Read-only celestial-body database for a given save / scenario.
/// Static once built — no setters; bodies don't dock or stage.
#[derive(Clone, Debug)]
pub struct Ephemeris {
    bodies: Vec<Body>,
    by_id: Vec<usize>, // BodyId.0 → index into `bodies`
    /// Direct children per body, keyed by `BodyId.0`. Precomputed at
    /// construction so `OccluderSet` can walk down the SOI tree
    /// without rebuilding an index per query.
    children_by_id: Vec<Vec<BodyId>>,
}

impl Ephemeris {
    pub fn new(bodies: Vec<Body>) -> Self {
        let max_id = bodies.iter().map(|b| b.id.0).max().unwrap_or(0) as usize;
        let mut by_id = vec![usize::MAX; max_id + 1];
        for (i, b) in bodies.iter().enumerate() {
            let slot = b.id.0 as usize;
            assert!(by_id[slot] == usize::MAX, "duplicate BodyId {}", b.id.0);
            by_id[slot] = i;
        }
        let mut children_by_id: Vec<Vec<BodyId>> = vec![Vec::new(); max_id + 1];
        for b in &bodies {
            if let Some(parent) = b.parent {
                children_by_id[parent.0 as usize].push(b.id);
            }
        }
        Ephemeris { bodies, by_id, children_by_id }
    }

    pub fn bodies(&self) -> &[Body] { &self.bodies }

    pub fn body(&self, id: BodyId) -> &Body {
        let i = self
            .by_id
            .get(id.0 as usize)
            .copied()
            .filter(|&i| i != usize::MAX)
            .unwrap_or_else(|| panic!("unknown BodyId {}", id.0));
        &self.bodies[i]
    }

    /// Walk up the parent chain until a body with no parent is found.
    pub fn root(&self, mut id: BodyId) -> BodyId {
        loop {
            match self.body(id).parent {
                Some(p) => id = p,
                None => return id,
            }
        }
    }

    /// Direct children of `id` in the SOI tree. Empty for leaf bodies.
    pub fn children(&self, id: BodyId) -> &[BodyId] {
        self.children_by_id
            .get(id.0 as usize)
            .map(|v| v.as_slice())
            .unwrap_or(&[])
    }

    /// Append `id` plus every descendant in `id`'s subtree to `out`.
    /// Order: pre-order DFS. Used by `OccluderSet` to enumerate the
    /// subtree of a penultimate body.
    pub fn descendants(&self, id: BodyId, out: &mut Vec<BodyId>) {
        out.push(id);
        for &child in self.children(id) {
            self.descendants(child, out);
        }
    }

    /// Position of `id` in its parent's inertial frame at `ut`.
    /// Returns `Vec3d::ZERO` for the root body.
    pub fn body_position_relative(&self, id: BodyId, ut: f64) -> Vec3d {
        let body = self.body(id);
        match (body.orbit.as_ref(), body.parent) {
            (Some(orbit), Some(parent)) => {
                let mu = self.body(parent).mu;
                orbit.position_at(mu, ut)
            }
            _ => Vec3d::ZERO,
        }
    }

    /// Position of `id` in the root inertial frame at `ut`.
    /// Recursively composes parent positions.
    pub fn body_position_absolute(&self, id: BodyId, ut: f64) -> Vec3d {
        let mut accum = Vec3d::ZERO;
        let mut cur = id;
        loop {
            let body = self.body(cur);
            accum += self.body_position_relative(cur, ut);
            match body.parent {
                Some(p) => cur = p,
                None => return accum,
            }
        }
    }

    /// Pressure (atm) at the given altitude above the body's surface.
    /// Returns 0 for bodies with no atmosphere, or above the top.
    pub fn pressure_atm(&self, body: BodyId, altitude_m: f64) -> f64 {
        match &self.body(body).atmosphere {
            Some(atm) => atm.pressure_atm(altitude_m),
            None => 0.0,
        }
    }

    /// Sidereal orbital period of `body` around its parent (s).
    /// `f64::INFINITY` for the root.
    pub fn orbital_period(&self, body: BodyId) -> f64 {
        let b = self.body(body);
        match (b.orbit.as_ref(), b.parent) {
            (Some(orbit), Some(parent)) => orbit.period(self.body(parent).mu),
            _ => f64::INFINITY,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::fixtures::{ids, kerbol_bodies};

    #[test]
    fn children_index_links_kerbol_tree() {
        let ephem = Ephemeris::new(kerbol_bodies());

        let sun_children = ephem.children(ids::SUN);
        assert!(sun_children.contains(&ids::KERBIN));
        assert!(sun_children.contains(&ids::DUNA));
        assert!(!sun_children.contains(&ids::MUN));

        let kerbin_children = ephem.children(ids::KERBIN);
        assert!(kerbin_children.contains(&ids::MUN));
        assert!(kerbin_children.contains(&ids::MINMUS));

        assert!(ephem.children(ids::MUN).is_empty());
    }

    #[test]
    fn descendants_kerbin_subtree_includes_mun_and_minmus() {
        let ephem = Ephemeris::new(kerbol_bodies());
        let mut out = Vec::new();
        ephem.descendants(ids::KERBIN, &mut out);
        assert!(out.contains(&ids::KERBIN));
        assert!(out.contains(&ids::MUN));
        assert!(out.contains(&ids::MINMUS));
        assert!(!out.contains(&ids::DUNA));
    }

    #[test]
    fn descendants_sun_includes_full_tree() {
        let ephem = Ephemeris::new(kerbol_bodies());
        let mut out = Vec::new();
        ephem.descendants(ids::SUN, &mut out);
        assert_eq!(out.len(), ephem.bodies().len());
    }
}
