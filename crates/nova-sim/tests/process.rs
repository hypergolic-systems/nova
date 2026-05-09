//! End-to-end ProcessFlowSystem scenarios. Mirrors
//! `mod/Nova.Tests/Systems/ProcessFlowSystemTests.cs` — the C# is the
//! spec, this file ports its scenarios verbatim.
//!
//! Buffer-driven cases (drain/fill/MaxTickDt) land alongside M5.6 +
//! M5.8; for now we cover the algebra of the priority loop: producer
//! / consumer matching, demand throttling, priorities, fairness.

use approx::assert_relative_eq;
use nova_sim::buffer::Buffer;
use nova_sim::resource::Resource;
use nova_sim::sim_clock::SimClock;
use nova_sim::systems::process::{Priority, ProcessFlowSystem};

/// Mirror of the C# `Battery(...)` test fixture in
/// `mod/Nova.Tests/Systems/ProcessFlowSystemTests.cs:12-21`.
fn battery(capacity: f64, contents: f64, max_rate_in: f64, max_rate_out: f64) -> Buffer {
    let mut b = Buffer::new(Resource::ElectricCharge, capacity, None);
    b.set_contents(contents);
    b.flow_limits(max_rate_in, max_rate_out);
    b
}

// ── Sanity ───────────────────────────────────────────────────────────

#[test]
fn producer_supplies_consumer_direct_match() {
    let mut sys = ProcessFlowSystem::new(SimClock::new(0.0));
    let producer = sys.add_device(Priority::Low);
    sys.device_mut(producer).add_output(Resource::ElectricCharge, 10.0);
    sys.device_mut(producer).demand = 1.0;

    let consumer = sys.add_device(Priority::Low);
    sys.device_mut(consumer).add_input(Resource::ElectricCharge, 10.0);
    sys.device_mut(consumer).demand = 1.0;

    sys.solve();

    assert_relative_eq!(sys.device(producer).activity, 1.0, max_relative = 1e-6);
    assert_relative_eq!(sys.device(consumer).activity, 1.0, max_relative = 1e-6);
}

#[test]
fn producer_excess_limited_to_consumer_demand() {
    // Solar-style: 100 EC available, only 10 EC demanded by consumer
    // and no buffer. Producer activity should drop to match (no waste).
    let mut sys = ProcessFlowSystem::new(SimClock::new(0.0));
    let producer = sys.add_device(Priority::Low);
    sys.device_mut(producer).add_output(Resource::ElectricCharge, 100.0);
    sys.device_mut(producer).demand = 1.0;

    let consumer = sys.add_device(Priority::Low);
    sys.device_mut(consumer).add_input(Resource::ElectricCharge, 10.0);
    sys.device_mut(consumer).demand = 1.0;

    sys.solve();

    assert_relative_eq!(sys.device(consumer).activity, 1.0, max_relative = 1e-6);
    assert_relative_eq!(sys.device(producer).activity, 0.1, max_relative = 1e-6);
}

#[test]
fn no_producer_no_buffer_consumer_starves() {
    let mut sys = ProcessFlowSystem::new(SimClock::new(0.0));
    let consumer = sys.add_device(Priority::Low);
    sys.device_mut(consumer).add_input(Resource::ElectricCharge, 10.0);
    sys.device_mut(consumer).demand = 1.0;

    sys.solve();

    assert_relative_eq!(sys.device(consumer).activity, 0.0, epsilon = 1e-6);
}

// ── Priority ────────────────────────────────────────────────────────

#[test]
fn device_priority_critical_satisfied_first() {
    // 5 EC supply available; Critical wants 5 EC, Low wants 5 EC.
    // Critical takes the entire supply; Low ends at 0.
    let mut sys = ProcessFlowSystem::new(SimClock::new(0.0));
    let producer = sys.add_device(Priority::Low);
    sys.device_mut(producer).add_output(Resource::ElectricCharge, 5.0);
    sys.device_mut(producer).demand = 1.0;

    let critical = sys.add_device(Priority::Critical);
    sys.device_mut(critical).add_input(Resource::ElectricCharge, 5.0);
    sys.device_mut(critical).demand = 1.0;

    let low = sys.add_device(Priority::Low);
    sys.device_mut(low).add_input(Resource::ElectricCharge, 5.0);
    sys.device_mut(low).demand = 1.0;

    sys.solve();

    assert_relative_eq!(sys.device(critical).activity, 1.0, max_relative = 1e-6);
    assert_relative_eq!(sys.device(low).activity, 0.0, epsilon = 1e-6);
    assert_relative_eq!(sys.device(producer).activity, 1.0, max_relative = 1e-6);
}

#[test]
fn priority_higher_doesnt_steal_when_slack() {
    // 20 EC supply, Critical wants 5, Low wants 5: both satisfied.
    let mut sys = ProcessFlowSystem::new(SimClock::new(0.0));
    let producer = sys.add_device(Priority::Low);
    sys.device_mut(producer).add_output(Resource::ElectricCharge, 20.0);
    sys.device_mut(producer).demand = 1.0;

    let critical = sys.add_device(Priority::Critical);
    sys.device_mut(critical).add_input(Resource::ElectricCharge, 5.0);
    sys.device_mut(critical).demand = 1.0;

    let low = sys.add_device(Priority::Low);
    sys.device_mut(low).add_input(Resource::ElectricCharge, 5.0);
    sys.device_mut(low).demand = 1.0;

    sys.solve();

    assert_relative_eq!(sys.device(critical).activity, 1.0, max_relative = 1e-6);
    assert_relative_eq!(sys.device(low).activity, 1.0, max_relative = 1e-6);
}

#[test]
fn same_priority_fair_share_under_constraint() {
    // 10 EC supply, two consumers each want 8 EC.
    // Max-min fairness: both at 0.625 (5 EC each).
    let mut sys = ProcessFlowSystem::new(SimClock::new(0.0));
    let producer = sys.add_device(Priority::Low);
    sys.device_mut(producer).add_output(Resource::ElectricCharge, 10.0);
    sys.device_mut(producer).demand = 1.0;

    let c1 = sys.add_device(Priority::Low);
    sys.device_mut(c1).add_input(Resource::ElectricCharge, 8.0);
    sys.device_mut(c1).demand = 1.0;

    let c2 = sys.add_device(Priority::Low);
    sys.device_mut(c2).add_input(Resource::ElectricCharge, 8.0);
    sys.device_mut(c2).demand = 1.0;

    sys.solve();

    assert_relative_eq!(sys.device(c1).activity, 0.625, max_relative = 1e-6);
    assert_relative_eq!(sys.device(c2).activity, 0.625, max_relative = 1e-6);
    // Producer must run at full to provide the 10 EC.
    assert_relative_eq!(sys.device(producer).activity, 1.0, max_relative = 1e-6);
}

// ── Demand throttling ────────────────────────────────────────────────

// ── Buffer drain / fill ──────────────────────────────────────────────

#[test]
fn buffer_drains_when_producer_insufficient() {
    // Producer 5 EC, consumer 15 EC, battery 100/100. Battery
    // covers the 10 EC shortfall: rate = -10.
    let mut sys = ProcessFlowSystem::new(SimClock::new(0.0));
    let producer = sys.add_device(Priority::Low);
    sys.device_mut(producer).add_output(Resource::ElectricCharge, 5.0);
    sys.device_mut(producer).demand = 1.0;

    let bid = sys.add_buffer(battery(100.0, 100.0, 1000.0, 1000.0));

    let consumer = sys.add_device(Priority::Low);
    sys.device_mut(consumer).add_input(Resource::ElectricCharge, 15.0);
    sys.device_mut(consumer).demand = 1.0;

    sys.solve();

    assert_relative_eq!(sys.device(consumer).activity, 1.0, max_relative = 1e-6);
    assert_relative_eq!(sys.device(producer).activity, 1.0, max_relative = 1e-6);
    assert_relative_eq!(sys.buffer(bid).rate(), -10.0, max_relative = 1e-6);
}

#[test]
fn buffer_fills_when_producer_excess() {
    // Producer 20 EC, consumer 5 EC, battery 50/100. Excess fills
    // battery: rate = +15.
    let mut sys = ProcessFlowSystem::new(SimClock::new(0.0));
    let producer = sys.add_device(Priority::Low);
    sys.device_mut(producer).add_output(Resource::ElectricCharge, 20.0);
    sys.device_mut(producer).demand = 1.0;

    let bid = sys.add_buffer(battery(100.0, 50.0, 1000.0, 1000.0));

    let consumer = sys.add_device(Priority::Low);
    sys.device_mut(consumer).add_input(Resource::ElectricCharge, 5.0);
    sys.device_mut(consumer).demand = 1.0;

    sys.solve();

    assert_relative_eq!(sys.device(producer).activity, 1.0, max_relative = 1e-6);
    assert_relative_eq!(sys.device(consumer).activity, 1.0, max_relative = 1e-6);
    assert_relative_eq!(sys.buffer(bid).rate(), 15.0, max_relative = 1e-6);
}

#[test]
fn buffer_full_no_fill_producer_throttles() {
    // Producer 20 EC, consumer 5 EC, battery 100/100 (full → no fill
    // capacity). Producer should throttle to match consumer load
    // (activity = 5/20 = 0.25); battery rate stays at 0.
    let mut sys = ProcessFlowSystem::new(SimClock::new(0.0));
    let producer = sys.add_device(Priority::Low);
    sys.device_mut(producer).add_output(Resource::ElectricCharge, 20.0);
    sys.device_mut(producer).demand = 1.0;

    let bid = sys.add_buffer(battery(100.0, 100.0, 1000.0, 1000.0));

    let consumer = sys.add_device(Priority::Low);
    sys.device_mut(consumer).add_input(Resource::ElectricCharge, 5.0);
    sys.device_mut(consumer).demand = 1.0;

    sys.solve();

    assert_relative_eq!(sys.device(producer).activity, 0.25, max_relative = 1e-6);
    assert_relative_eq!(sys.device(consumer).activity, 1.0, max_relative = 1e-6);
    assert_relative_eq!(sys.buffer(bid).rate(), 0.0, epsilon = 1e-6);
}

#[test]
fn two_batteries_drain_proportional_to_contents() {
    // Two batteries (100/100 + 100/100) draining at total -10 EC/s
    // would split 50/50. Make one have 80, the other 20: 80/100 ratio
    // → battery A drains at -8, battery B at -2.
    let mut sys = ProcessFlowSystem::new(SimClock::new(0.0));
    let a = sys.add_buffer(battery(100.0, 80.0, 1000.0, 1000.0));
    let b = sys.add_buffer(battery(100.0, 20.0, 1000.0, 1000.0));

    let consumer = sys.add_device(Priority::Low);
    sys.device_mut(consumer).add_input(Resource::ElectricCharge, 10.0);
    sys.device_mut(consumer).demand = 1.0;

    sys.solve();

    assert_relative_eq!(sys.buffer(a).rate(), -8.0, max_relative = 1e-6);
    assert_relative_eq!(sys.buffer(b).rate(), -2.0, max_relative = 1e-6);
}

#[test]
fn two_batteries_fill_proportional_to_remaining_capacity() {
    // Two batteries (100/100 each), one at 20 (80 free) and the other
    // at 80 (20 free). Fill 10 EC/s should split: A gets 8 (80/100 of
    // total free space), B gets 2.
    let mut sys = ProcessFlowSystem::new(SimClock::new(0.0));
    let a = sys.add_buffer(battery(100.0, 20.0, 1000.0, 1000.0));
    let b = sys.add_buffer(battery(100.0, 80.0, 1000.0, 1000.0));

    let producer = sys.add_device(Priority::Low);
    sys.device_mut(producer).add_output(Resource::ElectricCharge, 10.0);
    sys.device_mut(producer).demand = 1.0;

    sys.solve();

    assert_relative_eq!(sys.buffer(a).rate(), 8.0, max_relative = 1e-6);
    assert_relative_eq!(sys.buffer(b).rate(), 2.0, max_relative = 1e-6);
}

// ── max_tick_dt ──────────────────────────────────────────────────────

#[test]
fn max_tick_dt_battery_empties_at_drain_rate() {
    // 100 EC, 10 EC/s drain → empty in 10 s.
    let mut sys = ProcessFlowSystem::new(SimClock::new(0.0));
    sys.add_buffer(battery(100.0, 100.0, 1000.0, 1000.0));

    let consumer = sys.add_device(Priority::Low);
    sys.device_mut(consumer).add_input(Resource::ElectricCharge, 10.0);
    sys.device_mut(consumer).demand = 1.0;

    sys.solve();

    assert_relative_eq!(sys.max_tick_dt(), 10.0, max_relative = 1e-6);
}

#[test]
fn max_tick_dt_battery_fills_at_fill_rate() {
    // 50/100 EC, +5 EC/s fill → full in 10 s.
    let mut sys = ProcessFlowSystem::new(SimClock::new(0.0));
    sys.add_buffer(battery(100.0, 50.0, 1000.0, 1000.0));

    let producer = sys.add_device(Priority::Low);
    sys.device_mut(producer).add_output(Resource::ElectricCharge, 5.0);
    sys.device_mut(producer).demand = 1.0;

    sys.solve();

    assert_relative_eq!(sys.max_tick_dt(), 10.0, max_relative = 1e-6);
}

#[test]
fn max_tick_dt_collapses_with_device_valid_until() {
    // Battery would empty in 10 s (100 EC / 10 EC/s drain), but a
    // device declares it expects re-solve at UT=4 (i.e. dt=4 from
    // clock.ut()=0). max_tick_dt picks the device horizon.
    let mut sys = ProcessFlowSystem::new(SimClock::new(0.0));
    sys.add_buffer(battery(100.0, 100.0, 1000.0, 1000.0));

    let consumer = sys.add_device(Priority::Low);
    sys.device_mut(consumer).add_input(Resource::ElectricCharge, 10.0);
    sys.device_mut(consumer).demand = 1.0;
    sys.device_mut(consumer).valid_until = 4.0;

    sys.solve();

    assert_relative_eq!(sys.max_tick_dt(), 4.0, max_relative = 1e-6);
}

#[test]
fn max_tick_dt_infinite_when_no_events() {
    // Idle vessel: nothing draining, nothing filling, no device
    // forecasts. max_tick_dt should be +∞.
    let mut sys = ProcessFlowSystem::new(SimClock::new(0.0));
    sys.solve();
    assert!(sys.max_tick_dt().is_infinite());
}

// ── Lerp under clock advance ─────────────────────────────────────────

#[test]
fn clock_advance_lerps_contents() {
    // Solve once → battery rate set by LP. Advancing the clock without
    // re-solving should still report the correct lerped contents
    // (Buffer's lerp handles it; the system just sets the rate).
    let clock = SimClock::new(0.0);
    let mut sys = ProcessFlowSystem::new(clock.clone());
    let bid = sys.add_buffer(battery(100.0, 100.0, 1000.0, 1000.0));

    let consumer = sys.add_device(Priority::Low);
    sys.device_mut(consumer).add_input(Resource::ElectricCharge, 10.0);
    sys.device_mut(consumer).demand = 1.0;

    sys.solve();

    // Advance 2 s; contents should drift to 80 (rate -10, dt 2).
    clock.advance(2.0);
    assert_relative_eq!(sys.buffer(bid).contents(), 80.0, max_relative = 1e-6);
}

// ── LP hygiene / numerical envelope ──────────────────────────────────

#[test]
fn handles_within_row_coefficient_ratio_at_envelope_upper() {
    // Worst within-row spread allowed by `docs/lp_hygiene.md`: 10⁵
    // (smallest scaled coefficient ≈ 10⁻⁵, one order above HiGHS's
    // tolerance floor). Producer 10000 + consumer 0.1 → producer
    // throttles to consumer/100000 = 1e-5 to satisfy conservation.
    // This is the regime where GLOP starts emitting MPSOLVER_ABNORMAL;
    // confirming HiGHS handles it cleanly is the smoke test.
    let mut sys = ProcessFlowSystem::new(SimClock::new(0.0));
    let producer = sys.add_device(Priority::Low);
    sys.device_mut(producer).add_output(Resource::ElectricCharge, 10_000.0);
    sys.device_mut(producer).demand = 1.0;

    let consumer = sys.add_device(Priority::Low);
    sys.device_mut(consumer).add_input(Resource::ElectricCharge, 0.1);
    sys.device_mut(consumer).demand = 1.0;

    sys.solve();

    assert_relative_eq!(sys.device(consumer).activity, 1.0, max_relative = 1e-5);
    assert_relative_eq!(sys.device(producer).activity, 1.0e-5, max_relative = 1e-3);
}

// ── Demand throttling ────────────────────────────────────────────────

#[test]
fn demand_throttling_caps_activity() {
    // Plenty of supply but demand=0.5 → activity capped at 0.5.
    let mut sys = ProcessFlowSystem::new(SimClock::new(0.0));
    let producer = sys.add_device(Priority::Low);
    sys.device_mut(producer).add_output(Resource::ElectricCharge, 100.0);
    sys.device_mut(producer).demand = 1.0;

    let consumer = sys.add_device(Priority::Low);
    sys.device_mut(consumer).add_input(Resource::ElectricCharge, 10.0);
    sys.device_mut(consumer).demand = 0.5;

    sys.solve();

    assert_relative_eq!(sys.device(consumer).activity, 0.5, max_relative = 1e-6);
}
