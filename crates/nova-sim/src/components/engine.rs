//! Liquid-fuel rocket engine. Mirrors
//! `mod/Nova.Core/Components/Propulsion/Engine.cs`.
//!
//! The engine declares a coupled-input Consumer on the staging
//! system — one input per propellant — at `on_build_systems`. The
//! staging solver's water-fill + coupling pass then enforces the
//! "all propellants or none" semantic natively: starvation on any
//! one input drops `Activity` to 0 and the engine doesn't drain the
//! others either.
//!
//! `normalized_output` reflects the actual achieved throttle this
//! tick (= consumer.activity); `satisfaction` is the same thing
//! divided by the requested throttle.

use crate::resource::Resource;
use crate::systems::{ConsumerId, NodeId, VesselSystems};

const G0: f64 = 9.806_65;

#[derive(Debug, Clone)]
pub struct Propellant {
    pub resource: Resource,
    /// Volumetric mix ratio. The relative number is what matters
    /// (ratio 2 vs 3 = 2:3 mix); absolute scale is normalised when
    /// `max_flow` is computed at `initialize`.
    pub ratio: f64,
    /// Max volumetric flow at full throttle (L/s) — populated by
    /// `initialize` from thrust / Isp / density.
    pub max_flow: f64,
}

#[derive(Debug, Clone)]
pub struct Engine {
    pub thrust_kn: f64,
    pub isp_s: f64,
    /// Caller-set requested throttle, [0, 1]. Pushed into the
    /// solver each `on_pre_solve` as `consumer.demand`.
    pub throttle: f64,

    pub gimbal_range_rad: f64,
    pub gimbal_pitch_deflection: f64,
    pub gimbal_yaw_deflection: f64,

    pub ignited: bool,
    pub flameout: bool,

    pub propellants: Vec<Propellant>,

    /// Mass flow at full throttle (kg/s). Computed at `initialize`.
    mass_flow: f64,
    /// Mass per recipe batch — Σ(ratio × density) over propellants.
    batch_mass: f64,

    /// Set at `on_build_systems`; the staging-system Consumer
    /// representing this engine.
    consumer_id: Option<ConsumerId>,
    /// Set at `on_build_systems`; the staging Node we sit on.
    node_id: Option<NodeId>,
}

impl Engine {
    /// Construct a new engine and pre-compute the derived flow
    /// rates from thrust, Isp, and propellant ratios.
    pub fn new(
        thrust_kn: f64,
        isp_s: f64,
        propellants: Vec<(Resource, f64)>,
    ) -> Self {
        let mut e = Engine {
            thrust_kn,
            isp_s,
            throttle: 0.0,
            gimbal_range_rad: 0.0,
            gimbal_pitch_deflection: 0.0,
            gimbal_yaw_deflection: 0.0,
            ignited: false,
            flameout: false,
            propellants: propellants
                .into_iter()
                .map(|(resource, ratio)| Propellant {
                    resource,
                    ratio,
                    max_flow: 0.0,
                })
                .collect(),
            mass_flow: 0.0,
            batch_mass: 0.0,
            consumer_id: None,
            node_id: None,
        };
        e.compute_derived_fields();
        e
    }

    fn compute_derived_fields(&mut self) {
        // F = Isp · g0 · mdot  →  mdot = F / (Isp · g0)
        // thrust_kn × 1000 converts to N.
        if self.isp_s > 0.0 {
            self.mass_flow = self.thrust_kn * 1000.0 / (self.isp_s * G0);
        } else {
            self.mass_flow = 0.0;
        }
        self.batch_mass = self
            .propellants
            .iter()
            .map(|p| p.ratio * p.resource.density())
            .sum();
        if self.batch_mass > 0.0 {
            let max_batch_rate = self.mass_flow / self.batch_mass;
            for p in &mut self.propellants {
                p.max_flow = max_batch_rate * p.ratio;
            }
        }
    }

    pub fn mass_flow(&self) -> f64 { self.mass_flow }
    pub fn batch_mass(&self) -> f64 { self.batch_mass }
    pub fn consumer_id(&self) -> Option<ConsumerId> { self.consumer_id }
    pub fn node_id(&self) -> Option<NodeId> { self.node_id }

    /// Effective throttle achieved this tick — equal to
    /// `consumer.activity`. When all propellants are fully supplied,
    /// `normalized_output == throttle`. When any propellant is
    /// starved, the staging coupling pass scales it down (zero in
    /// the fully-starved case).
    pub fn normalized_output(&self, sys: &VesselSystems) -> f64 {
        match self.consumer_id {
            Some(id) => sys.staging.consumer(id).activity,
            None => 0.0,
        }
    }

    /// Fraction of requested throttle actually achieved (1.0 = fully
    /// supplied, 0 = fully starved). Independent of the throttle
    /// setting — useful for telemetry that wants raw satisfaction.
    pub fn satisfaction(&self, sys: &VesselSystems) -> f64 {
        if self.throttle <= 1.0e-12 { return 0.0; }
        self.normalized_output(sys) / self.throttle
    }

    pub(crate) fn on_build_systems(&mut self, sys: &mut VesselSystems, node: NodeId) {
        self.node_id = Some(node);
        let inputs: Vec<(Resource, f64)> = self
            .propellants
            .iter()
            .map(|p| (p.resource, p.max_flow))
            .collect();
        self.consumer_id = Some(sys.add_device(node, inputs));
    }

    pub(crate) fn on_pre_solve(&mut self, sys: &mut VesselSystems) {
        if let Some(id) = self.consumer_id {
            sys.staging.consumer_mut(id).demand = self.throttle;
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use approx::assert_relative_eq;

    #[test]
    fn mass_flow_matches_isp_g0_thrust_relation() {
        let e = Engine::new(1.0, 220.0, vec![(Resource::Hydrazine, 1.0)]);
        // 1 kN @ 220 s: mdot = 1000 / (220 × 9.80665) ≈ 0.4634 kg/s.
        assert_relative_eq!(e.mass_flow(), 1000.0 / (220.0 * G0), max_relative = 1e-12);
    }

    #[test]
    fn single_propellant_max_flow_equals_volumetric_rate_at_full_throttle() {
        let e = Engine::new(1.0, 220.0, vec![(Resource::Hydrazine, 1.0)]);
        // mdot / density = volumetric flow = ~0.4634 L/s (hydrazine ρ = 1.0).
        let expected = (1000.0 / (220.0 * G0)) / Resource::Hydrazine.density();
        assert_relative_eq!(e.propellants[0].max_flow, expected, max_relative = 1e-12);
    }

    #[test]
    fn kerolox_max_flows_in_2_to_3_ratio() {
        // RP-1 : LOX volume ratio 2 : 3.
        let e = Engine::new(
            240.0,
            310.0,
            vec![
                (Resource::Rp1, 2.0),
                (Resource::LiquidOxygen, 3.0),
            ],
        );
        let rp1 = e.propellants[0].max_flow;
        let lox = e.propellants[1].max_flow;
        assert!(rp1 > 0.0 && lox > 0.0);
        assert_relative_eq!(rp1 / lox, 2.0 / 3.0, max_relative = 1e-12);
    }

    #[test]
    fn zero_isp_yields_zero_mass_flow() {
        let e = Engine::new(1.0, 0.0, vec![(Resource::Hydrazine, 1.0)]);
        assert_relative_eq!(e.mass_flow(), 0.0);
    }

    #[test]
    fn empty_propellants_yields_zero_batch_mass() {
        let e = Engine::new(1.0, 220.0, vec![]);
        assert_relative_eq!(e.batch_mass(), 0.0);
    }

    #[test]
    fn normalized_output_is_zero_before_build_systems() {
        let sys = VesselSystems::new(crate::sim_clock::SimClock::new(0.0));
        let e = Engine::new(1.0, 220.0, vec![(Resource::Hydrazine, 1.0)]);
        assert_relative_eq!(e.normalized_output(&sys), 0.0);
        assert_relative_eq!(e.satisfaction(&sys), 0.0);
    }
}
