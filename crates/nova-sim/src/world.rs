use crate::components::{Component, Part};
use crate::comms::{Antenna, CommsSystem, EndpointId};
use crate::ephem::{Body, BodyId, Ephemeris};
use crate::math::Vec3d;
use crate::orbit::OrbitalElements;
use crate::resource::Resource;
use crate::resources::{Orbit, PanelGeometry, SolarEvent, SolarForecaster};
use crate::sim_clock::SimClock;
use crate::situation::Situation;
use crate::systems::{DeviceHandle, NodeId, Priority, VesselSystems};
use crate::world_context::WorldContext;

use std::collections::HashMap;

/// Per-vessel solar wiring. `None` for vessels with no SolarPanel
/// components. Owned by `Vessel` and rebuilt on `initialize_solver`.
#[derive(Clone, Debug)]
pub(crate) struct VesselSolar {
    /// Aggregate Process producer (output: ElectricCharge,
    /// total_charge_rate). One device per vessel, regardless of panel
    /// count — matches C# `VirtualVessel.solarDevice`.
    pub device: DeviceHandle,
    /// Σ over all panels' rated charge_rate.
    pub total_charge_rate: f64,
    /// Best-orientation total power, from
    /// `SolarForecaster::optimal_rate`. Recomputed on deploy-state
    /// change (deferred — not exposed in this milestone).
    pub cached_optimal_rate: f64,
}

/// Stable identifier for a vessel within a World.
#[derive(Copy, Clone, Debug, PartialEq, Eq, Hash, PartialOrd, Ord)]
pub struct VesselId(pub u32);

/// A simulated vessel. Carries a [`Situation`] (where it is — Keplerian
/// orbit, abstract editor preview, …), a part list, and an optional
/// `VesselSystems` populated by `initialize_solver`.
#[derive(Clone, Debug)]
pub struct Vessel {
    pub id: VesselId,
    pub name: String,
    pub situation: Situation,
    pub parts: Vec<Part>,
    pub systems: Option<VesselSystems>,
    pub(crate) solar: Option<VesselSolar>,
}

impl Vessel {
    pub fn new(id: VesselId, name: impl Into<String>, situation: Situation) -> Self {
        Vessel {
            id,
            name: name.into(),
            situation,
            parts: Vec::new(),
            systems: None,
            solar: None,
        }
    }

    /// Convenience constructor for `Situation::Orbit`. Most callers want
    /// this — `Abstract` is reserved for editor / pre-launch states.
    pub fn in_orbit(
        id: VesselId,
        name: impl Into<String>,
        parent: BodyId,
        orbit: OrbitalElements,
    ) -> Self {
        Vessel::new(id, name, Situation::orbit(parent, orbit))
    }

    /// Update the vessel's situation in-place. Used by FFI hosts that
    /// register a vessel as `Abstract` and later transition it to
    /// `Orbit` when the host's orbit data is ready.
    pub fn set_situation(&mut self, situation: Situation) {
        self.situation = situation;
    }

    /// Vessel orbit descriptor for `SolarForecaster::forecast`.
    /// `None` when the vessel has no physical location (e.g. editor
    /// preview); callers should skip orbit-dependent work.
    pub fn orbit_descriptor(&self) -> Option<Orbit> {
        self.situation
            .as_orbit()
            .map(|(parent, orbit)| Orbit::new(orbit, parent))
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

    /// Walk every part's `Component::Comms` and return the antenna
    /// specs. Used by `CommsSystem` to synthesise the vessel's
    /// endpoint at solve time.
    pub fn comms_antennas(&self) -> Vec<Antenna> {
        let mut out = Vec::new();
        for part in &self.parts {
            for c in &part.components {
                if let Component::Comms(comms) = c {
                    out.push(comms.antenna);
                }
            }
        }
        out
    }

    /// Build the staging-system topology from `parts` and run each
    /// component's `on_build_systems` registration. Idempotent —
    /// subsequent calls reset the solver and rebuild from scratch.
    ///
    /// Takes `&WorldContext` so the aggregate solar Device's optimal
    /// rate can be cached at build time. Vessels with no SolarPanel
    /// components don't touch the context.
    pub fn initialize_solver(&mut self, ctx: &WorldContext, ut: f64) {
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

        // 4. Build the aggregate solar device. Mirrors C#
        //    `VirtualVessel.BuildSolarDevice` (line 107-120) +
        //    `ComputeSolarRates` (line 182-212). Sums charge_rate
        //    across panels, registers one Process producer at the
        //    root node, runs the Fibonacci-sphere optimiser to cache
        //    the best-orientation total, and pro-rates per-panel
        //    `effective_rate`.
        self.solar = self.build_solar(&mut systems);

        // 5. Seed per-panel telemetry from a single forecast call so
        //    `is_sunlit` / `shadow_transition_ut` reflect reality
        //    before the first tick lands. Cheap (one ephem walk).
        if self.solar.is_some() {
            // Abstract vessels (editor preview, pre-launch) have no
            // orbit to forecast against — leave panel telemetry at the
            // default (is_sunlit=false, transition_ut=0). The next
            // `update_solar_pre_solve` will pick up real values once
            // the host transitions the situation to `Orbit`.
            if let Some(desc) = self.orbit_descriptor() {
                let event = ctx.solar.forecast(&desc, ut);
                let (sunlit, transition_ut) = match event {
                    SolarEvent::Sun(dt) => (true, abs_ut(ut, dt)),
                    SolarEvent::Shade(dt) => (false, abs_ut(ut, dt)),
                };
                for part in &mut self.parts {
                    for c in &mut part.components {
                        if let Component::SolarPanel(p) = c {
                            p.is_sunlit = sunlit;
                            p.shadow_transition_ut = transition_ut;
                        }
                    }
                }
            }
        }

        self.systems = Some(systems);
    }

    fn build_solar(&mut self, systems: &mut VesselSystems) -> Option<VesselSolar> {
        // Determine which staging node hosts the aggregate device.
        // Use the root part — same convention the C# port follows
        // (RootStagingNode in BuildSolarDevice).
        let root_node = self
            .parts
            .iter()
            .find(|p| p.parent.is_none())
            .and_then(|p| p.node_id)?;

        // Sum rated charge across panels, and gather deployed-panel
        // geometry for the optimiser.
        let mut total_charge_rate = 0.0;
        let mut deployed: Vec<PanelGeometry> = Vec::new();
        let mut deployed_charge = 0.0;
        for part in &self.parts {
            for c in &part.components {
                if let Component::SolarPanel(p) = c {
                    total_charge_rate += p.charge_rate;
                    if p.is_deployed {
                        deployed.push(PanelGeometry {
                            direction: p.panel_direction,
                            charge_rate: p.charge_rate,
                            is_tracking: p.is_tracking,
                        });
                        deployed_charge += p.charge_rate;
                    }
                }
            }
        }

        if total_charge_rate <= 0.0 {
            return None;
        }

        let device = systems.add_device(
            root_node,
            &[],
            &[(Resource::ElectricCharge, total_charge_rate)],
            Priority::Low,
        );
        systems.set_device_demand(device, 0.0);

        let cached_optimal_rate = SolarForecaster::optimal_rate(&deployed);

        // Pro-rate effective_rate per panel (deployed only). Mirrors
        // `VirtualVessel.ComputeSolarRates` line 208-211.
        if deployed_charge > 0.0 && cached_optimal_rate > 0.0 {
            for part in &mut self.parts {
                for c in &mut part.components {
                    if let Component::SolarPanel(p) = c {
                        p.effective_rate = if p.is_deployed {
                            (p.charge_rate / deployed_charge) * cached_optimal_rate
                        } else {
                            0.0
                        };
                    }
                }
            }
        }

        Some(VesselSolar {
            device,
            total_charge_rate,
            cached_optimal_rate,
        })
    }

    /// Mark the systems dirty so the next `tick(...)` runs
    /// pre/solve/post. Required after any external mutation that
    /// changes rates (`engine.throttle = ...`, `consumer.demand = ...`,
    /// `buffer.set_contents(...)`). No-op if `initialize_solver` hasn't
    /// been called yet.
    pub fn invalidate(&mut self) {
        if let Some(sys) = self.systems.as_mut() {
            sys.invalidate();
        }
    }

    /// Run one solve at the current clock time: pre-solve hooks,
    /// staging solve, post-solve hooks. Useful for tests that want
    /// the instantaneous rate without advancing time. Production
    /// code should generally use `tick(target_ut)` instead. Always
    /// runs unconditionally — bypasses `needs_solve`.
    pub fn solve(&mut self, ctx: &WorldContext) {
        let mut systems = self.systems.take()
            .expect("solve() called before initialize_solver()");
        self.do_solve_with(ctx, &mut systems);
        self.systems = Some(systems);
    }

    /// Advance the simulation clock to `target_ut`, re-solving at
    /// every event boundary (buffer empty/fill, component-scheduled
    /// expiry). Mirrors `VirtualVessel.Tick` in C# — including the
    /// `needsSolve` invalidation pattern: solves only fire when the
    /// LP/water-fill state is actually dirty (initial build,
    /// forecasted event firing, external mutation flagged via
    /// `invalidate()`).
    ///
    /// Within one tick, advance steps are bounded by the soonest
    /// expiry — guarantees we don't over-step a state transition
    /// (a tank emptying mid-burn, a panel slipping into shadow).
    pub fn tick(&mut self, ctx: &WorldContext, target_ut: f64) {
        let mut systems = self.systems.take()
            .expect("tick() called before initialize_solver()");

        for part in &mut self.parts {
            for c in &mut part.components {
                c.on_tick_begin();
            }
        }

        for _ in 0..MAX_TICK_ITERATIONS {
            // Skip redundant solves: rates from the prior solve are
            // still valid as long as nothing has changed. Lerp keeps
            // integrating buffer contents during clock advance with
            // those rates; only state changes (events firing,
            // external mutations) need a re-solve.
            if systems.needs_solve() {
                self.do_solve_with(ctx, &mut systems);
            }

            let now = systems.clock.ut();
            if now >= target_ut { break; }

            // Soonest event is the min of each system's buffer-event
            // forecast and any component-scheduled expiry. MIN_TICK_STEP
            // floor prevents fp residuals near a transition from
            // stalling the loop.
            let dt_staging = systems.staging.max_tick_dt();
            let dt_process = systems.process.max_tick_dt();
            let dt_components = self.min_component_dt(now);
            let dt_event = dt_staging.min(dt_process).min(dt_components);

            let dt = dt_event
                .min(target_ut - now)
                .max(MIN_TICK_STEP);
            systems.clock.advance(dt);

            // Crossing a forecasted event invalidates the solve —
            // the new state (tank empty, accumulator at threshold,
            // etc.) needs fresh rates. The 1e-12 tolerance covers fp
            // residuals from `MIN_TICK_STEP` flooring.
            if dt_event.is_finite() && systems.clock.ut() + 1e-12 >= now + dt_event {
                systems.invalidate();
            }
        }

        // If the final advance crossed an event, run one closing
        // solve so post-tick reads (engine activity, fuel-cell
        // current_output, etc.) reflect the state at target_ut, not
        // the rates from the previous-window solve.
        if systems.needs_solve() {
            self.do_solve_with(ctx, &mut systems);
        }

        self.systems = Some(systems);
    }

    pub fn systems(&self) -> &VesselSystems {
        self.systems.as_ref().expect("systems not initialized — call initialize_solver()")
    }

    fn do_solve_with(&mut self, ctx: &WorldContext, systems: &mut VesselSystems) {
        // Solar pre-solve: forecast next shadow boundary and gate the
        // aggregate solar device's demand. Mirrors C#
        // `VirtualVessel.UpdateShadowState` + `UpdateSolarDeviceDemand`
        // (line 218-226). The Process system folds the device's
        // valid_until into its `max_tick_dt`, so the inner tick loop
        // re-solves at every shadow transition for free.
        self.update_solar_pre_solve(ctx, systems);

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

        // Solar post-solve: distribute LP-solved aggregate output to
        // per-panel `current_rate`. Mirrors
        // `VirtualVessel.DistributeSolarPanelCurrentRates` (line 266-276).
        self.distribute_solar_current_rates(systems);

        systems.note_solved();
    }

    fn update_solar_pre_solve(&mut self, ctx: &WorldContext, systems: &mut VesselSystems) {
        let solar = match self.solar.as_ref() {
            Some(s) => s,
            None => return,
        };
        let desc = match self.orbit_descriptor() {
            Some(d) => d,
            None => return, // abstract vessel — no orbit to forecast against
        };

        let now = systems.clock.ut();
        let event = ctx.solar.forecast(&desc, now);
        let (sunlit, transition_ut) = match event {
            SolarEvent::Sun(dt) => (true, abs_ut(now, dt)),
            SolarEvent::Shade(dt) => (false, abs_ut(now, dt)),
        };

        // Demand 0..1 — Process LP variable's effective cap is
        // `total_charge_rate × demand`, so we want `optimal/total`
        // when sunlit (saturates at the optimiser-found best rate),
        // 0 otherwise.
        let demand = if sunlit && solar.total_charge_rate > 0.0 {
            (solar.cached_optimal_rate / solar.total_charge_rate).min(1.0)
        } else {
            0.0
        };
        systems.set_device_demand(solar.device, demand);
        systems.set_device_valid_until(solar.device, transition_ut);

        // Mirror state to per-panel telemetry.
        for part in &mut self.parts {
            for c in &mut part.components {
                if let Component::SolarPanel(p) = c {
                    p.is_sunlit = sunlit;
                    p.shadow_transition_ut = transition_ut;
                }
            }
        }
    }

    fn distribute_solar_current_rates(&mut self, systems: &VesselSystems) {
        let solar = match self.solar.as_ref() {
            Some(s) => s,
            None => return,
        };

        if solar.cached_optimal_rate <= 0.0 || solar.total_charge_rate <= 0.0 {
            for part in &mut self.parts {
                for c in &mut part.components {
                    if let Component::SolarPanel(p) = c {
                        p.current_rate = 0.0;
                    }
                }
            }
            return;
        }

        let activity = systems.device_activity(solar.device);
        let actual_output = activity * solar.total_charge_rate;
        let scale = actual_output / solar.cached_optimal_rate;
        for part in &mut self.parts {
            for c in &mut part.components {
                if let Component::SolarPanel(p) = c {
                    p.current_rate = p.effective_rate * scale;
                }
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

/// Convert a relative dt (from `SolarEvent`) to an absolute UT,
/// preserving `+∞` rather than producing NaN at `now + ∞`.
fn abs_ut(now: f64, dt: f64) -> f64 {
    if dt.is_infinite() {
        f64::INFINITY
    } else {
        now + dt
    }
}

/// Safety cap on tick iterations. At worst, one iter per scheduled
/// event in the tick window; 256 covers reasonable burn scenarios
/// without runaway loops. C# uses 100 with the same intent.
const MAX_TICK_ITERATIONS: usize = 256;

#[derive(Clone, Debug)]
pub struct World {
    pub ephemeris: Ephemeris,
    pub vessels: Vec<Vessel>,
    pub comms: CommsSystem,
}

impl World {
    pub fn builder() -> WorldBuilder { WorldBuilder::default() }

    pub fn vessel(&self, id: VesselId) -> &Vessel {
        self.vessels
            .iter()
            .find(|v| v.id == id)
            .unwrap_or_else(|| panic!("unknown VesselId {}", id.0))
    }

    /// Advance every vessel and the world-level comms graph to
    /// `target_ut`, interleaved at comms event boundaries. Per-vessel
    /// `Vessel::tick` stays unaware of comms; the loop here clamps
    /// each step to `comms.max_tick_dt()` so packet completions and
    /// link bucket/occlusion crossings land on a re-solve boundary.
    pub fn tick(&mut self, target_ut: f64) {
        // Move comms out so we can pass `&self` to its methods without
        // borrow-checker pain. Same `mem::take` pattern as
        // `Vessel::tick` uses for its `VesselSystems`.
        let mut comms = std::mem::take(&mut self.comms);

        // First-call bootstrap: if comms has never solved, prime it
        // at UT=0 so subsequent loop iterations have a graph + a
        // simulation_time reference.
        let mut now = match comms.simulation_time() {
            Some(t) => t,
            None => {
                comms.solve(self, 0.0);
                0.0
            }
        };

        let mut iter = 0;
        while now < target_ut {
            iter += 1;
            if iter > MAX_TICK_ITERATIONS {
                break;
            }

            // Reactive bucket-watch for off-rails endpoints. M6
            // doesn't surface "off-rails" yet — every vessel is
            // motion-predictable — so this is a no-op for current
            // fixtures. The wiring is in place for when on/off-rails
            // transitions land via the FFI bridge.
            for v in &self.vessels {
                if comms.any_link_bucket_difference(self, EndpointId::Vessel(v.id), now) {
                    comms.invalidate();
                    break;
                }
            }

            let dt = comms
                .max_tick_dt()
                .min(target_ut - now)
                .max(MIN_TICK_STEP);
            let next = now + dt;

            // Build the per-tick context once. Borrows `&self.ephemeris`
            // — disjoint field from `&mut self.vessels`, so the inner
            // loop compiles cleanly.
            let ctx = WorldContext::new(&self.ephemeris);
            for v in &mut self.vessels {
                if v.systems.is_some() {
                    v.tick(&ctx, next);
                }
            }
            comms.solve(self, next);
            now = next;
        }

        self.comms = comms;
    }

    /// Vessel position in the parent body's inertial frame at `ut`.
    /// `None` for abstract vessels (no physical location).
    pub fn vessel_position_relative(&self, id: VesselId, ut: f64) -> Option<Vec3d> {
        let v = self.vessel(id);
        let (parent, orbit) = v.situation.as_orbit()?;
        let mu = self.ephemeris.body(parent).mu;
        Some(orbit.position_at(mu, ut))
    }

    /// Vessel position in the root inertial frame at `ut`. `None` for
    /// abstract vessels.
    pub fn vessel_position_absolute(&self, id: VesselId, ut: f64) -> Option<Vec3d> {
        let v = self.vessel(id);
        let parent = v.situation.parent_body()?;
        let parent_pos = self.ephemeris.body_position_absolute(parent, ut);
        Some(parent_pos + self.vessel_position_relative(id, ut)?)
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
            comms: CommsSystem::default(),
        }
    }
}
