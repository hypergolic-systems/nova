//! Tick-driver scenarios — exercise `Vessel::tick(target_ut)` over
//! time, validating event-driven re-solves at buffer-empty boundaries.
//!
//! These are the "the simulator is actually simulating" tests: you
//! point it at a future UT and it integrates the burn, flameout, and
//! coast phases naturally without per-frame stepping.

use approx::assert_relative_eq;
use nova_sim::components::{Component, Engine, TankVolume};
use nova_sim::fixtures::ids;
use nova_sim::orbit::OrbitalElements;
use nova_sim::resource::Resource;
use nova_sim::{Vessel, VesselId};

const G0: f64 = 9.806_65;

fn pod_with_parts(extras: Vec<(u32, &str, f64, Vec<Component>)>) -> Vessel {
    let mut vessel = Vessel::new(
        VesselId(1),
        "TestVessel",
        ids::KERBIN,
        OrbitalElements::circular(700_000.0 + 600_000.0),
    );
    vessel.add_part(1, "pod", 100.0, vec![]);
    for (id, name, dry, components) in extras {
        vessel.add_part(id, name, dry, components);
        vessel.set_parent(id, 1);
    }
    vessel
}

/// Single-prop hydrazine engine + matching tank, throttle 1.
fn burn_vessel(thrust_kn: f64, isp_s: f64, capacity: f64) -> Vessel {
    let tank = TankVolume::new(capacity, 1.0e9).add_tank(Resource::Hydrazine, capacity);
    let mut engine = Engine::new(thrust_kn, isp_s, vec![(Resource::Hydrazine, 1.0)]);
    engine.throttle = 1.0;
    pod_with_parts(vec![
        (2, "tank", 0.0, vec![Component::TankVolume(tank)]),
        (3, "engine", 0.0, vec![Component::Engine(engine)]),
    ])
}

fn buffer_id(v: &Vessel, part: u32) -> nova_sim::BufferId {
    match &v.part(part).components[0] {
        Component::TankVolume(t) => t.buffer_ids()[0],
        _ => unreachable!(),
    }
}

fn engine_activity(v: &Vessel, part: u32) -> f64 {
    match &v.part(part).components[0] {
        Component::Engine(e) => e.normalized_output(v.systems()),
        _ => unreachable!(),
    }
}

// ── Basic event-driven advance ────────────────────────────────────────

#[test]
fn tick_to_current_time_solves_without_advancing() {
    let mut v = burn_vessel(1.0, 220.0, 100.0);
    v.initialize_solver(0.0);
    v.tick(0.0);
    // No time passed; engine should be at full output, tank still full.
    assert_relative_eq!(engine_activity(&v, 3), 1.0);
    let bid = buffer_id(&v, 2);
    assert_relative_eq!(v.systems().staging.buffer(bid).contents(), 100.0);
}

#[test]
fn inert_vessel_tick_just_advances_clock() {
    // Engine throttle 0 → no rate, no events. Clock should jump
    // straight to target_ut.
    let mut v = burn_vessel(1.0, 220.0, 100.0);
    if let Component::Engine(e) = &mut v.part_mut(3).components[0] {
        e.throttle = 0.0;
    }
    v.initialize_solver(0.0);
    v.tick(50.0);
    assert_relative_eq!(v.systems().clock.ut(), 50.0);
    let bid = buffer_id(&v, 2);
    assert_relative_eq!(v.systems().staging.buffer(bid).contents(), 100.0);
}

// ── Burn dynamics ─────────────────────────────────────────────────────

#[test]
fn engine_burn_drains_tank_to_empty_at_predicted_ut() {
    let capacity = 100.0;
    let mut v = burn_vessel(1.0, 220.0, capacity);
    v.initialize_solver(0.0);
    let drain_rate = 1000.0 / (220.0 * G0) / Resource::Hydrazine.density();
    let burn_time = capacity / drain_rate;

    // Tick exactly to burn-out — at the boundary, contents == 0 and
    // the engine has just flamed out.
    v.tick(burn_time);
    let bid = buffer_id(&v, 2);
    assert_relative_eq!(v.systems().staging.buffer(bid).contents(), 0.0, epsilon = 1e-9);
    assert_relative_eq!(engine_activity(&v, 3), 0.0);
}

#[test]
fn tick_past_empty_yields_zero_activity_for_remaining_time() {
    let mut v = burn_vessel(1.0, 220.0, 100.0);
    v.initialize_solver(0.0);
    let drain_rate = 1000.0 / (220.0 * G0) / Resource::Hydrazine.density();
    let burn_time = 100.0 / drain_rate;

    // Tick well past flameout. Tank stays empty, engine stays off,
    // clock lands at the requested target_ut.
    v.tick(burn_time + 100.0);
    assert_relative_eq!(v.systems().clock.ut(), burn_time + 100.0);
    let bid = buffer_id(&v, 2);
    assert_relative_eq!(v.systems().staging.buffer(bid).contents(), 0.0, epsilon = 1e-9);
    assert_relative_eq!(engine_activity(&v, 3), 0.0);
}

#[test]
fn partial_burn_drains_tank_proportionally() {
    let capacity = 100.0;
    let mut v = burn_vessel(1.0, 220.0, capacity);
    v.initialize_solver(0.0);
    let drain_rate = 1000.0 / (220.0 * G0) / Resource::Hydrazine.density();
    let burn_time = capacity / drain_rate;

    // Burn for half the empty time → contents at half capacity.
    v.tick(burn_time * 0.5);
    let bid = buffer_id(&v, 2);
    assert_relative_eq!(
        v.systems().staging.buffer(bid).contents(),
        capacity * 0.5,
        max_relative = 1e-9,
    );
    // Still firing.
    assert_relative_eq!(engine_activity(&v, 3), 1.0);
}

#[test]
fn consecutive_ticks_continue_burn_continuously() {
    let capacity = 100.0;
    let mut v = burn_vessel(1.0, 220.0, capacity);
    v.initialize_solver(0.0);
    let drain_rate = 1000.0 / (220.0 * G0) / Resource::Hydrazine.density();
    let bid = buffer_id(&v, 2);

    v.tick(10.0);
    let after_10 = v.systems().staging.buffer(bid).contents();
    assert_relative_eq!(after_10, capacity - 10.0 * drain_rate, max_relative = 1e-9);

    v.tick(20.0);
    let after_20 = v.systems().staging.buffer(bid).contents();
    assert_relative_eq!(after_20, capacity - 20.0 * drain_rate, max_relative = 1e-9);

    v.tick(30.0);
    let after_30 = v.systems().staging.buffer(bid).contents();
    assert_relative_eq!(after_30, capacity - 30.0 * drain_rate, max_relative = 1e-9);
}

#[test]
fn throttle_change_between_ticks_is_picked_up_at_next_solve() {
    let capacity = 100.0;
    let mut v = burn_vessel(1.0, 220.0, capacity);
    v.initialize_solver(0.0);
    let drain_rate = 1000.0 / (220.0 * G0) / Resource::Hydrazine.density();
    let bid = buffer_id(&v, 2);

    // Burn at full throttle for 10 s.
    v.tick(10.0);
    let after_full = v.systems().staging.buffer(bid).contents();
    assert_relative_eq!(after_full, capacity - 10.0 * drain_rate, max_relative = 1e-9);

    // Halve the throttle, burn another 10 s. External mutations
    // require an explicit `invalidate()` — solves don't auto-fire
    // every tick under the event-driven scheduler; the throttle
    // change wouldn't propagate to the staging consumer otherwise.
    if let Component::Engine(e) = &mut v.part_mut(3).components[0] {
        e.throttle = 0.5;
    }
    v.invalidate();
    v.tick(20.0);
    // Drain over the second 10s should be half-rate.
    let expected = after_full - 10.0 * drain_rate * 0.5;
    let after_half = v.systems().staging.buffer(bid).contents();
    assert_relative_eq!(after_half, expected, max_relative = 1e-9);
}

// ── Event-driven re-solve scheduling ──────────────────────────────────

#[test]
fn quiet_ticks_skip_solve_after_initial() {
    // Inert vessel: throttle 0 → no rates, no forecasted events.
    // The first tick solves once (initial state); subsequent ticks
    // advance the clock without re-solving since nothing changed.
    let mut v = burn_vessel(1.0, 220.0, 100.0);
    if let Component::Engine(e) = &mut v.part_mut(3).components[0] {
        e.throttle = 0.0;
    }
    v.initialize_solver(0.0);
    assert_eq!(v.systems().solve_count(), 0);

    v.tick(10.0);
    assert_eq!(v.systems().solve_count(), 1, "first tick should solve once");

    // Five more quiet ticks — clock advances but nothing's changing.
    for t in [20.0, 30.0, 40.0, 50.0, 100.0] {
        v.tick(t);
    }
    assert_eq!(
        v.systems().solve_count(),
        1,
        "rates stable across long horizons → no re-solves",
    );
}

#[test]
fn forecasted_event_invalidates_at_crossing() {
    // Engine burning at known rate empties the tank at burn_time.
    // The first tick solves once; the closing solve at the burn-out
    // boundary makes a second one fire so post-tick activity reads
    // 0 (engine flamed out) rather than 1 (last in-burn rate).
    let capacity = 100.0;
    let mut v = burn_vessel(1.0, 220.0, capacity);
    v.initialize_solver(0.0);
    let drain_rate = 1000.0 / (220.0 * G0) / Resource::Hydrazine.density();
    let burn_time = capacity / drain_rate;

    v.tick(burn_time);

    // Two solves: the initial pre-burn solve, and a closing solve
    // because the tank-empty event was reached exactly at target_ut.
    assert_eq!(v.systems().solve_count(), 2);
    assert_relative_eq!(engine_activity(&v, 3), 0.0);
}

#[test]
fn external_invalidate_forces_resolve_on_next_tick() {
    // After a quiet tick, an external mutation isn't auto-detected
    // (rates living on staging consumers don't observe writes to
    // engine.throttle directly). `invalidate()` is the explicit
    // signal that "rates are stale, re-solve next time".
    let mut v = burn_vessel(1.0, 220.0, 100.0);
    if let Component::Engine(e) = &mut v.part_mut(3).components[0] {
        e.throttle = 0.0;
    }
    v.initialize_solver(0.0);
    v.tick(10.0);
    assert_eq!(v.systems().solve_count(), 1);

    // Mutation alone doesn't trigger a re-solve.
    v.tick(20.0);
    assert_eq!(v.systems().solve_count(), 1);

    // Explicit invalidate → next tick re-solves.
    v.invalidate();
    v.tick(30.0);
    assert_eq!(v.systems().solve_count(), 2);
}

// ── Multi-resource / coupled-input timing ─────────────────────────────

#[test]
fn kerolox_burn_flameouts_when_lox_runs_out_first() {
    // RP-1 ratio 2 : LOX ratio 3 — LOX drains faster volumetrically.
    // Stock the LOX tank below the 2:3 needed → LOX empties first.
    let tank = TankVolume::new(500.0, 1.0e9)
        .add_tank(Resource::Rp1, 200.0)
        .add_tank_with_contents(Resource::LiquidOxygen, 300.0, 90.0); // limited LOX
    let mut engine = Engine::new(
        240.0,
        310.0,
        vec![(Resource::Rp1, 2.0), (Resource::LiquidOxygen, 3.0)],
    );
    engine.throttle = 1.0;

    let mut v = pod_with_parts(vec![
        (2, "tank", 500.0, vec![Component::TankVolume(tank)]),
        (3, "engine", 1500.0, vec![Component::Engine(engine)]),
    ]);
    v.initialize_solver(0.0);

    // mdot = 240_000 / (310 × 9.80665) ≈ 78.96 kg/s.
    // batch_mass = 2 × 0.8 + 3 × 1.2 = 5.2 kg per batch.
    // batch rate = mdot / batch_mass.
    // LOX volumetric rate = batch_rate × 3 = 78.96 / 5.2 × 3 ≈ 45.554 L/s.
    // 90 L LOX → empties in ~1.976 s.
    let mdot = 240.0 * 1000.0 / (310.0 * G0);
    let batch_rate = mdot / (2.0 * 0.8 + 3.0 * 1.2);
    let lox_burn_time = 90.0 / (batch_rate * 3.0);

    v.tick(lox_burn_time + 5.0);

    let tank_ref = match &v.part(2).components[0] {
        Component::TankVolume(t) => t,
        _ => unreachable!(),
    };
    let b_rp1 = tank_ref.buffer_ids()[0];
    let b_lox = tank_ref.buffer_ids()[1];

    // LOX is out; engine flamed out; RP-1 left whatever it had at flameout.
    assert_relative_eq!(v.systems().staging.buffer(b_lox).contents(), 0.0, epsilon = 1e-9);
    assert_relative_eq!(engine_activity(&v, 3), 0.0);
    // RP-1 burned at ratio-proportional rate during the burn:
    // rp1 rate = batch_rate × 2 ≈ 30.37 L/s; over ~1.976 s that's ~60 L drained,
    // leaving ~140 L of the original 200 L.
    let rp1_drained = batch_rate * 2.0 * lox_burn_time;
    assert_relative_eq!(
        v.systems().staging.buffer(b_rp1).contents(),
        200.0 - rp1_drained,
        max_relative = 1e-6,
    );
}
