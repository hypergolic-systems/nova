use crate::components::{Component, Part};
use crate::ephem::{Body, BodyId, Ephemeris};
use crate::math::Vec3d;
use crate::orbit::OrbitalElements;
use crate::sim_clock::SimClock;
use crate::systems::{NodeId, VesselSystems};

use std::collections::HashMap;

/// Stable identifier for a vessel within a World.
#[derive(Copy, Clone, Debug, PartialEq, Eq, Hash)]
pub struct VesselId(pub u32);

/// A simulated vessel. Carries orbital elements (for ephem queries),
/// a part list (for component-laden tests), and an optional
/// `VesselSystems` populated by `initialize_solver`.
#[derive(Clone, Debug)]
pub struct Vessel {
    pub id: VesselId,
    pub name: String,
    pub parent: BodyId,
    pub orbit: OrbitalElements,
    pub parts: Vec<Part>,
    pub systems: Option<VesselSystems>,
}

impl Vessel {
    pub fn new(id: VesselId, name: impl Into<String>, parent: BodyId, orbit: OrbitalElements) -> Self {
        Vessel {
            id,
            name: name.into(),
            parent,
            orbit,
            parts: Vec::new(),
            systems: None,
        }
    }

    /// Append a part. Builder-style — returns `&mut Self`.
    pub fn add_part(
        &mut self,
        id: u32,
        name: impl Into<String>,
        dry_mass_kg: f64,
        components: Vec<Component>,
    ) -> &mut Self {
        let part = Part::new(id, name, dry_mass_kg).with_components(components);
        self.parts.push(part);
        self
    }

    /// Set parent in the part-tree topology. Both parts must already
    /// exist in `self.parts`.
    pub fn set_parent(&mut self, child: u32, parent: u32) -> &mut Self {
        let p_idx = self.parts.iter().position(|p| p.id == child)
            .unwrap_or_else(|| panic!("set_parent: unknown child id {}", child));
        self.parts[p_idx].parent = Some(parent);
        self
    }

    pub fn part(&self, id: u32) -> &Part {
        self.parts.iter().find(|p| p.id == id)
            .unwrap_or_else(|| panic!("unknown part id {}", id))
    }

    pub fn part_mut(&mut self, id: u32) -> &mut Part {
        self.parts.iter_mut().find(|p| p.id == id)
            .unwrap_or_else(|| panic!("unknown part id {}", id))
    }

    /// Build the staging-system topology from `parts` and run each
    /// component's `on_build_systems` registration. Idempotent —
    /// subsequent calls reset the solver and rebuild from scratch.
    pub fn initialize_solver(&mut self, ut: f64) {
        let clock = SimClock::new(ut);
        let mut systems = VesselSystems::new(clock);

        // 1. One solver Node per part. Build id → NodeId map first
        //    so set_parent can wire children before any component
        //    callbacks reference NodeIds.
        let mut node_for: HashMap<u32, NodeId> = HashMap::new();
        for part in &mut self.parts {
            let nid = systems.staging.add_node(part.dry_mass_kg);
            node_for.insert(part.id, nid);
            part.node_id = Some(nid);
        }

        // 2. Wire parent → child links so the staging system knows
        //    the topology and the connectivity graph follows the tree
        //    by default (no explicit edges needed in M3).
        for part in &self.parts {
            if let Some(parent_id) = part.parent {
                let parent_nid = node_for[&parent_id];
                let child_nid = node_for[&part.id];
                systems.staging.set_parent(parent_nid, child_nid);
            }
        }

        // 3. Run each component's on_build_systems. We pull the
        //    components out, run the callback, then put them back —
        //    avoids overlapping borrows with `systems`.
        for part_idx in 0..self.parts.len() {
            let nid = self.parts[part_idx].node_id.unwrap();
            let mut components = std::mem::take(&mut self.parts[part_idx].components);
            for c in &mut components {
                c.on_build_systems(&mut systems, nid);
            }
            self.parts[part_idx].components = components;
        }

        self.systems = Some(systems);
    }

    /// Run one solve at the current clock time: pre-solve hooks,
    /// staging solve, post-solve hooks. Useful for tests that want
    /// the instantaneous rate without advancing time. Production
    /// code should generally use `tick(target_ut)` instead.
    pub fn solve(&mut self) {
        let mut systems = self.systems.take()
            .expect("solve() called before initialize_solver()");
        self.do_solve_with(&mut systems);
        self.systems = Some(systems);
    }

    /// Advance the simulation clock to `target_ut`, re-solving at
    /// every event boundary (buffer empty/fill, component-scheduled
    /// expiry). Mirrors `VirtualVessel.Tick` in C#.
    ///
    /// Within one tick, advance steps are bounded by the soonest
    /// expiry — guarantees we don't over-step a state transition
    /// (a tank emptying mid-burn, a panel slipping into shadow). The
    /// loop solves first, then advances; final iter solves again so
    /// post-tick rates reflect the state at `target_ut`.
    pub fn tick(&mut self, target_ut: f64) {
        let mut systems = self.systems.take()
            .expect("tick() called before initialize_solver()");

        for part in &mut self.parts {
            for c in &mut part.components {
                c.on_tick_begin();
            }
        }

        for _ in 0..MAX_TICK_ITERATIONS {
            // Solve at the start of every iter: captures the effect
            // of the previous clock advance and any external state
            // changes (throttle, etc).
            self.do_solve_with(&mut systems);

            let now = systems.clock.ut();
            if now >= target_ut { break; }

            // Soonest event is the min of each system's buffer-event
            // forecast and any component-scheduled expiry, clamped to
            // the remaining tick window. The MIN_TICK_STEP floor
            // prevents fp residuals near a transition from stalling
            // the loop.
            let dt_staging = systems.staging.max_tick_dt();
            let dt_process = systems.process.max_tick_dt();
            let dt_components = self.min_component_dt(now);
            let dt = dt_staging
                .min(dt_process)
                .min(dt_components)
                .min(target_ut - now)
                .max(MIN_TICK_STEP);

            systems.clock.advance(dt);
        }

        self.systems = Some(systems);
    }

    pub fn systems(&self) -> &VesselSystems {
        self.systems.as_ref().expect("systems not initialized — call initialize_solver()")
    }

    fn do_solve_with(&mut self, systems: &mut VesselSystems) {
        for part in &mut self.parts {
            for c in &mut part.components {
                c.on_pre_solve(systems);
            }
        }
        systems.staging.solve();
        systems.process.solve();
        for part in &mut self.parts {
            for c in &mut part.components {
                c.on_post_solve(systems);
            }
        }
    }

    /// Smallest time delta to a component-side scheduled expiry,
    /// across all components on the vessel. +∞ when nothing is
    /// scheduled (M4 components — Engine and TankVolume — never
    /// schedule events; Accumulator and friends will).
    fn min_component_dt(&self, now: f64) -> f64 {
        let mut earliest = f64::INFINITY;
        for part in &self.parts {
            for c in &part.components {
                let valid_until = c.valid_until();
                if valid_until.is_infinite() { continue; }
                let dt = valid_until - now;
                if dt > 0.0 && dt < earliest { earliest = dt; }
            }
        }
        earliest
    }
}

/// Floor on per-iter advance — prevents fp residuals near a
/// transition from stalling the tick loop.
const MIN_TICK_STEP: f64 = 1.0e-6;

/// Safety cap on tick iterations. At worst, one iter per scheduled
/// event in the tick window; 256 covers reasonable burn scenarios
/// without runaway loops. C# uses 100 with the same intent.
const MAX_TICK_ITERATIONS: usize = 256;

#[derive(Clone, Debug)]
pub struct World {
    pub ephemeris: Ephemeris,
    pub vessels: Vec<Vessel>,
}

impl World {
    pub fn builder() -> WorldBuilder { WorldBuilder::default() }

    pub fn vessel(&self, id: VesselId) -> &Vessel {
        self.vessels
            .iter()
            .find(|v| v.id == id)
            .unwrap_or_else(|| panic!("unknown VesselId {}", id.0))
    }

    /// Vessel position in the parent body's inertial frame at `ut`.
    pub fn vessel_position_relative(&self, id: VesselId, ut: f64) -> Vec3d {
        let v = self.vessel(id);
        let mu = self.ephemeris.body(v.parent).mu;
        v.orbit.position_at(mu, ut)
    }

    /// Vessel position in the root inertial frame at `ut`.
    pub fn vessel_position_absolute(&self, id: VesselId, ut: f64) -> Vec3d {
        let v = self.vessel(id);
        let parent_pos = self.ephemeris.body_position_absolute(v.parent, ut);
        parent_pos + self.vessel_position_relative(id, ut)
    }

    /// Direction from the given absolute position toward the root
    /// star, normalised. The root body is treated as the light source.
    pub fn sun_direction(&self, from_absolute: Vec3d, _ut: f64) -> Vec3d {
        // Root is at origin in the absolute frame by construction.
        (Vec3d::ZERO - from_absolute).normalize()
    }
}

#[derive(Default)]
pub struct WorldBuilder {
    bodies: Vec<Body>,
    vessels: Vec<Vessel>,
}

impl WorldBuilder {
    pub fn body(mut self, body: Body) -> Self {
        self.bodies.push(body);
        self
    }

    pub fn bodies<I: IntoIterator<Item = Body>>(mut self, iter: I) -> Self {
        self.bodies.extend(iter);
        self
    }

    pub fn vessel(mut self, v: Vessel) -> Self {
        assert!(
            !self.vessels.iter().any(|x| x.id == v.id),
            "duplicate VesselId {}", v.id.0,
        );
        self.vessels.push(v);
        self
    }

    pub fn build(self) -> World {
        World {
            ephemeris: Ephemeris::new(self.bodies),
            vessels: self.vessels,
        }
    }
}
