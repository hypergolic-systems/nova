//! End-to-end SolarPanel + Vessel integration. Verifies the
//! aggregate solar Device wires through `Vessel::initialize_solver`,
//! shadow forecasting drives demand + valid_until on every solve, and
//! per-panel `current_rate` reflects LP activity.
//!
//! Vessel + Battery are placed in low Kerbin orbit using the shared
//! `nova_sim::fixtures` helpers, so the geometry exercises the same
//! Ephemeris path the world tick driver does.

use approx::assert_relative_eq;
use nova_sim::components::{Battery, Component, SolarPanel};
use nova_sim::fixtures::{ids, kerbol_ctx};
use nova_sim::math::Vec3d;
use nova_sim::orbit::OrbitalElements;
use nova_sim::{Vessel, VesselId};

/// Stock-Kerbol low orbit at 700 km altitude. With default elements,
/// the vessel sits at +X relative to Kerbin and Kerbin sits at +X
/// relative to the sun, putting the vessel on the anti-sun side at
/// UT=0 — i.e. starts in shadow.
fn build_lko_vessel(components: Vec<Component>) -> Vessel {
    let mut v = Vessel::in_orbit(
        VesselId(1),
        "Sat",
        ids::KERBIN,
        OrbitalElements::circular(700_000.0 + 600_000.0),
    );
    v.add_part(1, "core", 100.0, components);
    v.initialize_solver(&kerbol_ctx(), 0.0);
    v
}

fn solar_panel_of(v: &Vessel) -> &SolarPanel {
    for c in &v.part(1).components {
        if let Component::SolarPanel(p) = c {
            return p;
        }
    }
    panic!("no SolarPanel on part 1");
}

fn battery_buffer_id(v: &Vessel) -> nova_sim::BufferId {
    for c in &v.part(1).components {
        if let Component::Battery(b) = c {
            return b.buffer_id().expect("battery not yet built");
        }
    }
    panic!("no Battery on part 1");
}

// ── Wiring ──────────────────────────────────────────────────────────

#[test]
fn no_panels_no_solar_state() {
    // Vessel without panels — nothing should be wired up. Existing
    // tests already covered this implicitly via fuel_cell etc.; this
    // is the explicit check that the solar field stays None.
    let v = build_lko_vessel(vec![Component::Battery(
        Battery::new(100.0).with_flow_limits(1000.0, 1000.0),
    )]);
    // The internal `solar` field is crate-private; we instead check
    // that a panel-less vessel doesn't introduce extra Process
    // devices. With one battery, only the battery's buffer should be
    // present — no aggregate solar producer.
    let process = &v.systems().process;
    assert_eq!(process.devices().len(), 0,
        "no panel components should mean no aggregate solar device");
    let _ = process; // silence unused if devices() not exposed
}

#[test]
fn deployed_panel_seeds_effective_rate() {
    let panel = SolarPanel::new(50.0, Vec3d::new(0.0, 1.0, 0.0));
    let v = build_lko_vessel(vec![
        Component::SolarPanel(panel),
        Component::Battery(Battery::new(100.0).with_flow_limits(1000.0, 1000.0)),
    ]);
    // Single panel → effective_rate equals the optimizer's output
    // (one direction perfectly aligned with sun gives full rate).
    let p = solar_panel_of(&v);
    assert_relative_eq!(p.effective_rate, 50.0, epsilon = 0.5);
}

#[test]
fn undeployed_panel_has_zero_effective_rate() {
    let panel = SolarPanel::new(50.0, Vec3d::new(0.0, 1.0, 0.0)).with_deployed(false);
    let v = build_lko_vessel(vec![
        Component::SolarPanel(panel),
        Component::Battery(Battery::new(100.0).with_flow_limits(1000.0, 1000.0)),
    ]);
    let p = solar_panel_of(&v);
    assert_eq!(p.effective_rate, 0.0);
}

// ── Shadow seeding ──────────────────────────────────────────────────

#[test]
fn lko_vessel_starts_in_shadow_at_ut_zero() {
    // Default LKO geometry puts the vessel anti-solar at UT=0 (see
    // module doc).
    let panel = SolarPanel::new(50.0, Vec3d::new(0.0, 1.0, 0.0));
    let v = build_lko_vessel(vec![
        Component::SolarPanel(panel),
        Component::Battery(Battery::new(100.0).with_flow_limits(1000.0, 1000.0)),
    ]);
    let p = solar_panel_of(&v);
    assert!(!p.is_sunlit, "default LKO at UT=0 should be in shadow");
    assert!(p.shadow_transition_ut.is_finite() && p.shadow_transition_ut > 0.0,
        "expected a finite future shadow transition, got {}", p.shadow_transition_ut);
}

// ── Per-tick behavior ───────────────────────────────────────────────

#[test]
fn shadow_panel_delivers_no_current_rate() {
    // In shadow the panel should produce nothing.
    let panel = SolarPanel::new(50.0, Vec3d::new(0.0, 1.0, 0.0));
    let mut v = build_lko_vessel(vec![
        Component::SolarPanel(panel),
        Component::Battery(Battery::new(100.0).with_contents(50.0).with_flow_limits(1000.0, 1000.0)),
    ]);
    v.tick(&kerbol_ctx(), 1.0);
    let p = solar_panel_of(&v);
    assert!(!p.is_sunlit);
    assert_eq!(p.current_rate, 0.0);
}

#[test]
fn sunlit_panel_charges_battery() {
    // Phase π → vessel on sun-side at UT=0. Run a brief tick; the
    // battery should gain charge.
    let panel = SolarPanel::new(50.0, Vec3d::new(0.0, 1.0, 0.0));
    let mut v = Vessel::in_orbit(
        VesselId(1),
        "Sat",
        ids::KERBIN,
        OrbitalElements {
            semi_major_axis: 700_000.0 + 600_000.0,
            eccentricity: 0.0,
            inclination: 0.0,
            lan: 0.0,
            arg_periapsis: std::f64::consts::PI, // start sun-side
            mean_anomaly_at_epoch: 0.0,
            epoch: 0.0,
        },
    );
    v.add_part(1, "core", 100.0, vec![
        Component::SolarPanel(panel),
        Component::Battery(Battery::new(1000.0).with_contents(500.0).with_flow_limits(1000.0, 1000.0)),
    ]);
    v.initialize_solver(&kerbol_ctx(), 0.0);

    // Verify the seeded sunlit state.
    assert!(solar_panel_of(&v).is_sunlit);

    let bid = battery_buffer_id(&v);
    let before = v.systems().process.buffer(bid).contents();
    v.tick(&kerbol_ctx(), 1.0);
    let after = v.systems().process.buffer(bid).contents();

    let p = solar_panel_of(&v);
    assert!(p.is_sunlit);
    assert!(p.current_rate > 0.0);
    assert!(after > before, "battery should gain charge while sunlit ({} → {})", before, after);
}

#[test]
fn tick_through_orbit_re_solves_at_shadow_boundary() {
    // Vessel starts on sun-side, ticks past shadow entry. The Process
    // system's `valid_until` plumbing should clamp dt so the inner
    // tick loop re-solves at the transition. We verify by checking
    // (a) the sunlit-state flips during the tick, and (b) more than
    // one solve fired.
    let panel = SolarPanel::new(50.0, Vec3d::new(0.0, 1.0, 0.0));
    let mut v = Vessel::in_orbit(
        VesselId(1),
        "Sat",
        ids::KERBIN,
        OrbitalElements {
            semi_major_axis: 700_000.0 + 600_000.0,
            eccentricity: 0.0,
            inclination: 0.0,
            lan: 0.0,
            arg_periapsis: std::f64::consts::PI,
            mean_anomaly_at_epoch: 0.0,
            epoch: 0.0,
        },
    );
    v.add_part(1, "core", 100.0, vec![
        Component::SolarPanel(panel),
        Component::Battery(Battery::new(10_000.0).with_contents(5_000.0).with_flow_limits(1000.0, 1000.0)),
    ]);
    v.initialize_solver(&kerbol_ctx(), 0.0);
    assert!(solar_panel_of(&v).is_sunlit);

    let solves_before = v.systems().solve_count();
    let transition_ut = solar_panel_of(&v).shadow_transition_ut;
    assert!(transition_ut.is_finite());

    // Tick well past the shadow entry — the loop must cross the
    // boundary and re-solve, flipping is_sunlit.
    v.tick(&kerbol_ctx(), transition_ut + 5.0);

    let solves_after = v.systems().solve_count();
    assert!(solves_after > solves_before,
        "expected re-solve at shadow boundary; solves {} → {}", solves_before, solves_after);
    assert!(!solar_panel_of(&v).is_sunlit, "should have flipped to shadow");
}
