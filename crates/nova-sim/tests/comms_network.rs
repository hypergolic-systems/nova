//! End-to-end tests for `CommsSystem`. Mirror of
//! `mod/Nova.Tests/Communications/CommunicationsNetworkTests.cs` —
//! grown step-by-step alongside the M6 build order. This file starts
//! at the BuildGraph slice and accretes routing, allocation, and
//! horizon-driven scenarios as later steps land.

use approx::assert_relative_eq;
use nova_sim::comms::{ksc, Antenna, CommsSystem, EndpointId, GraphSnapshot};
use nova_sim::components::{Comms, Component};
use nova_sim::ephem::BodyId;
use nova_sim::fixtures::{ids, kerbol_bodies};
use nova_sim::orbit::OrbitalElements;
use nova_sim::world::{Vessel, VesselId, World};

/// Antenna whose Shannon knee sits at exactly `ref_distance` — at
/// closer separations the link reports full hardware ceiling.
fn knee(max_rate: f64, ref_distance: f64) -> Antenna {
    Antenna { tx_power: 1.0, gain: 1.0, max_rate, ref_distance }
}

/// Wide-aperture flat antenna; identical to the C# `Flat` fixture.
fn flat(max_rate: f64) -> Antenna {
    Antenna { tx_power: 1.0, gain: 1.0, max_rate, ref_distance: 1.0e6 }
}

fn vessel_with_antenna(
    id: u32,
    name: &str,
    parent: BodyId,
    orbit: OrbitalElements,
    antenna: Antenna,
) -> Vessel {
    let mut v = Vessel::new(VesselId(id), name, parent, orbit);
    v.add_part(1, "core", 100.0, vec![Component::Comms(Comms::new(antenna))]);
    v
}

/// Find the first link in `graph` matching `from → to`, panicking with
/// useful context if missing.
fn link<'a>(
    graph: &'a GraphSnapshot,
    from: EndpointId,
    to: EndpointId,
) -> &'a nova_sim::comms::Link {
    graph
        .links
        .iter()
        .find(|l| l.from == from && l.to == to)
        .unwrap_or_else(|| {
            panic!(
                "missing link {:?} → {:?}; have {} links",
                from,
                to,
                graph.links.len()
            )
        })
}

#[test]
fn build_graph_two_vessels_yields_both_directions() {
    // Two vessels in 700 km Kerbin orbit (one with mean anomaly 0,
    // one with π — i.e. opposite sides of Kerbin), each carrying a
    // wide-aperture antenna. With Kerbin between them the link is
    // blocked; without occluders it would carry a quantised rate.
    let mut orbit_b = OrbitalElements::circular(600_000.0 + 700_000.0);
    orbit_b.mean_anomaly_at_epoch = std::f64::consts::PI;
    let world = World::builder()
        .bodies(kerbol_bodies())
        .vessel(vessel_with_antenna(
            1,
            "Sat-A",
            ids::KERBIN,
            OrbitalElements::circular(600_000.0 + 700_000.0),
            flat(1000.0),
        ))
        .vessel(vessel_with_antenna(2, "Sat-B", ids::KERBIN, orbit_b, flat(1000.0)))
        .build();

    let mut comms = CommsSystem::new();
    comms.solve(&world, 0.0);

    // Two vessels = 2 directed edges (A→B, B→A). Each blocked.
    let g = comms.graph();
    assert_eq!(g.links.len(), 2);
    let a = EndpointId::Vessel(VesselId(1));
    let b = EndpointId::Vessel(VesselId(2));
    let ab = link(g, a, b);
    let ba = link(g, b, a);
    assert!(ab.blocked, "Kerbin should occlude the inter-satellite chord");
    assert!(ba.blocked);
    assert_relative_eq!(ab.rate_bps, 0.0);
    assert_relative_eq!(ba.rate_bps, 0.0);
}

#[test]
fn build_graph_ksc_and_one_vessel_link_at_full_ceiling() {
    // KSC + one Kerbin-orbit vessel. The vessel sits at periapsis on
    // +X (mean_anomaly=0, argp=0, lan=0), while KSC is at lon ≈ -75°
    // — at a 700 km parking orbit Kerbin would occlude the chord. To
    // isolate the rate-quantisation path here, put the vessel at a
    // much higher altitude (5 Mm) so the chord clears Kerbin.
    let world = World::builder()
        .bodies(kerbol_bodies())
        .vessel(vessel_with_antenna(
            1,
            "Sat",
            ids::KERBIN,
            OrbitalElements::circular(600_000.0 + 5_000_000.0),
            knee(1000.0, 1.0e10),
        ))
        .build();

    let mut comms = CommsSystem::new();
    let ksc_id = comms.add_ground_station(ksc(ids::KERBIN));
    comms.solve(&world, 0.0);

    let sat = EndpointId::Vessel(VesselId(1));
    let g = comms.graph();
    assert_eq!(g.links.len(), 2, "expected KSC↔Sat both directions");

    // Distance ≈ 5 Mm. With ref_distance 1e10, distance ≪ ref_distance
    // → SNR ≫ SNR_ref → log-ratio ≫ 1, clamped to 1 → rate = ceiling
    // (above-knee bucket reports full hardware ceiling).
    let kc = link(g, ksc_id, sat);
    let sk = link(g, sat, ksc_id);
    assert!(!kc.blocked, "chord at altitude 5 Mm should clear Kerbin");
    assert!(!sk.blocked);
    // KSC → Sat: ceiling = min(KSC.max_rate=1e9, Sat.max_rate=1000) = 1000.
    assert_relative_eq!(kc.rate_bps, 1000.0, max_relative = 1e-9);
    assert_relative_eq!(sk.rate_bps, 1000.0, max_relative = 1e-9);
}

#[test]
fn build_graph_vessels_without_antennas_are_skipped() {
    let world = World::builder()
        .bodies(kerbol_bodies())
        .vessel(Vessel::new(
            VesselId(1),
            "NoAntenna",
            ids::KERBIN,
            OrbitalElements::circular(1_000_000.0),
        ))
        .vessel(vessel_with_antenna(
            2,
            "Sat",
            ids::KERBIN,
            OrbitalElements::circular(1_000_000.0),
            flat(1000.0),
        ))
        .build();

    let mut comms = CommsSystem::new();
    comms.solve(&world, 0.0);

    // Only the antenna-carrying vessel makes it onto the graph as
    // an endpoint, but with no peer there can be no edges.
    assert!(comms.graph().links.is_empty());
}

#[test]
fn solve_with_no_endpoints_yields_empty_graph() {
    let world = World::builder().bodies(kerbol_bodies()).build();
    let mut comms = CommsSystem::new();
    comms.solve(&world, 0.0);
    assert!(comms.graph().links.is_empty());
    assert_eq!(comms.graph().solved_ut, 0.0);
}

#[test]
fn add_ground_station_returns_distinct_ids() {
    let mut comms = CommsSystem::new();
    let a = comms.add_ground_station(ksc(ids::KERBIN));
    let b = comms.add_ground_station(ksc(ids::KERBIN));
    assert_ne!(a, b);
}

// ─── PathSummary / RefreshHomePathSummaries ────────────────────────

#[test]
fn path_summary_for_unblocked_vessel_to_home_has_path() {
    // KSC + one high-altitude vessel where the chord clears Kerbin.
    // After solve, the vessel's path_to_home should reflect a single-hop
    // path to KSC with bottleneck = direct rate.
    let world = World::builder()
        .bodies(kerbol_bodies())
        .vessel(vessel_with_antenna(
            1,
            "Sat",
            ids::KERBIN,
            OrbitalElements::circular(600_000.0 + 5_000_000.0),
            knee(1000.0, 1.0e10),
        ))
        .build();

    let mut comms = CommsSystem::new();
    let home = comms.add_ground_station(ksc(ids::KERBIN));
    comms.set_home(home);
    comms.solve(&world, 0.0);

    let sat_id = EndpointId::Vessel(VesselId(1));
    let sat = comms.endpoint(sat_id).expect("sat endpoint should exist");
    assert!(sat.path_to_home.has_path);
    assert!(sat.path_to_home.bottleneck_bps > 0.0);
    assert_relative_eq!(
        sat.path_to_home.bottleneck_bps,
        sat.path_to_home.direct_rate_bps,
        max_relative = 1e-9,
    );

    // KSC's own path_to_home stays at default (no self-link).
    let home_ep = comms.endpoint(home).unwrap();
    assert!(!home_ep.path_to_home.has_path);
    assert_relative_eq!(home_ep.path_to_home.bottleneck_bps, 0.0);
    assert_relative_eq!(home_ep.path_to_home.direct_rate_bps, 0.0);
}

#[test]
fn path_summary_blocked_vessel_to_home_has_no_path() {
    // Vessel at low (700 km) Kerbin orbit on +X — KSC at lon ≈ -75°
    // sits on the opposite side; Kerbin occludes the direct chord.
    // Without a relay, has_path = false.
    let world = World::builder()
        .bodies(kerbol_bodies())
        .vessel(vessel_with_antenna(
            1,
            "Sat",
            ids::KERBIN,
            OrbitalElements::circular(600_000.0 + 700_000.0),
            knee(1000.0, 1.0e10),
        ))
        .build();

    let mut comms = CommsSystem::new();
    let home = comms.add_ground_station(ksc(ids::KERBIN));
    comms.set_home(home);
    comms.solve(&world, 0.0);

    let sat = comms
        .endpoint(EndpointId::Vessel(VesselId(1)))
        .expect("sat endpoint should exist");
    assert!(!sat.path_to_home.has_path);
    assert_relative_eq!(sat.path_to_home.bottleneck_bps, 0.0);
    // Direct edge in the graph still reports its (blocked, rate=0)
    // attributes — verify direct_rate_bps == 0 because of blocking.
    assert_relative_eq!(sat.path_to_home.direct_rate_bps, 0.0);
}

// ─── Horizons ───────────────────────────────────────────────────────

#[test]
fn stationary_pair_next_event_ut_lands_at_horizon_cap() {
    // Two ground stations on a stationary body — distance constant,
    // so neither bucket nor occlusion changes within the search
    // window. next_event_ut should fall back to ut + MAX_HORIZON_SECONDS.
    use nova_sim::comms::MAX_HORIZON_SECONDS;
    use nova_sim::ephem::{Body, BodyId, BodyRotation};

    // Single root body at origin, no rotation (sidereal infinite via 0?
    // actually surface_offset divides by period — use a long but
    // finite period so omega is tiny). Both stations at the same
    // body, antipodal lat/lon — chord crosses through the body
    // (occluded) but bucket index stays constant at 0 since blocked.
    // Use lat=0, lon=±5 so chord stays clear.
    let bodies = vec![Body {
        id: BodyId(0),
        name: "Test".into(),
        parent: None,
        mu: 1.0,
        radius: 100.0,
        soi_radius: f64::INFINITY,
        atmosphere: None,
        rotation: BodyRotation {
            rotates: true,
            period_seconds: 1.0e12, // effectively stationary
            initial_rotation_rad: 0.0,
            tidally_locked: false,
        },
        orbit: None,
    }];
    let world = World::builder().bodies(bodies).build();

    let mut comms = CommsSystem::new();
    let _g1 = comms.add_ground_station(nova_sim::comms::GroundStationSpec {
        name: "G1".into(),
        primary: BodyId(0),
        latitude_deg: 0.0,
        longitude_deg: -5.0,
        altitude_m: 1000.0,
        antennas: vec![flat(1000.0)],
    });
    let _g2 = comms.add_ground_station(nova_sim::comms::GroundStationSpec {
        name: "G2".into(),
        primary: BodyId(0),
        latitude_deg: 0.0,
        longitude_deg: 5.0,
        altitude_m: 1000.0,
        antennas: vec![flat(1000.0)],
    });
    comms.solve(&world, 0.0);

    for link in &comms.graph().links {
        assert_relative_eq!(link.next_event_ut, MAX_HORIZON_SECONDS, max_relative = 1e-9);
    }
}

#[test]
fn pair_out_of_range_pinned_at_horizon_cap_after_prescreen() {
    // Two vessels on Kerbin orbits separated by enormous distance
    // (Mun and Minmus orbits). Antennas with tight ref_distance make
    // the link stay below the bucket-1 threshold over the whole search
    // window — pre-screen pins the horizon at the cap.
    use nova_sim::comms::MAX_HORIZON_SECONDS;

    // Tiny ref_distance → tiny pair_r_max. Vessel-to-vessel distance
    // (Mun orbit vs minmus orbit ~50 Mm) ≫ that, so pre-screen kicks in.
    let tiny = Antenna {
        tx_power: 1.0,
        gain: 1.0,
        max_rate: 1000.0,
        ref_distance: 1.0,
    };
    // Place vessels in orbits where they stay distant. Mun orbit
    // (12 Mm radius) and a different inclined Minmus orbit (47 Mm).
    let mut o1 = OrbitalElements::circular(12_000_000.0);
    o1.mean_anomaly_at_epoch = 0.0;
    let mut o2 = OrbitalElements::circular(47_000_000.0);
    o2.mean_anomaly_at_epoch = std::f64::consts::PI;
    o2.inclination = 6.0_f64.to_radians();

    let world = World::builder()
        .bodies(kerbol_bodies())
        .vessel(vessel_with_antenna(1, "FarA", ids::KERBIN, o1, tiny))
        .vessel(vessel_with_antenna(2, "FarB", ids::KERBIN, o2, tiny))
        .build();

    let mut comms = CommsSystem::new();
    comms.solve(&world, 0.0);

    for link in &comms.graph().links {
        // Link is rate 0 (out of range) — and horizon pinned at cap
        // because pre-screen detects the pair stays out of range.
        approx::assert_relative_eq!(link.rate_bps, 0.0);
        approx::assert_relative_eq!(
            link.next_event_ut,
            MAX_HORIZON_SECONDS,
            max_relative = 1e-9
        );
    }
}

#[test]
fn invalidate_clears_horizon_cache_so_re_solve_recomputes() {
    // Two stationary ground stations: solve once, fingerprint
    // graph.links[0].next_event_ut, invalidate, re-solve, get the
    // same value (cache cleared but determinism preserved). Smoke
    // test that invalidate doesn't break re-solve.
    use nova_sim::comms::GroundStationSpec;
    use nova_sim::ephem::{Body, BodyId, BodyRotation};

    let bodies = vec![Body {
        id: BodyId(0),
        name: "Test".into(),
        parent: None,
        mu: 1.0,
        radius: 100.0,
        soi_radius: f64::INFINITY,
        atmosphere: None,
        rotation: BodyRotation { rotates: true, period_seconds: 1.0e12, ..Default::default() },
        orbit: None,
    }];
    let world = World::builder().bodies(bodies).build();

    let mut comms = CommsSystem::new();
    comms.add_ground_station(GroundStationSpec {
        name: "G1".into(),
        primary: BodyId(0),
        latitude_deg: 0.0,
        longitude_deg: -5.0,
        altitude_m: 1000.0,
        antennas: vec![flat(1000.0)],
    });
    comms.add_ground_station(GroundStationSpec {
        name: "G2".into(),
        primary: BodyId(0),
        latitude_deg: 0.0,
        longitude_deg: 5.0,
        altitude_m: 1000.0,
        antennas: vec![flat(1000.0)],
    });
    comms.solve(&world, 0.0);
    let baseline = comms.graph().links[0].next_event_ut;

    comms.invalidate();
    comms.solve(&world, 0.0);
    let recomputed = comms.graph().links[0].next_event_ut;

    approx::assert_relative_eq!(baseline, recomputed, max_relative = 1e-9);
}

#[test]
fn path_summary_without_home_set_leaves_summaries_default() {
    // No set_home call → refresh skipped → all path_to_home stay default.
    let world = World::builder()
        .bodies(kerbol_bodies())
        .vessel(vessel_with_antenna(
            1,
            "Sat",
            ids::KERBIN,
            OrbitalElements::circular(600_000.0 + 5_000_000.0),
            knee(1000.0, 1.0e10),
        ))
        .build();

    let mut comms = CommsSystem::new();
    comms.add_ground_station(ksc(ids::KERBIN));
    // No set_home.
    comms.solve(&world, 0.0);

    for ep in comms.endpoints() {
        assert!(!ep.path_to_home.has_path);
        assert_relative_eq!(ep.path_to_home.bottleneck_bps, 0.0);
    }
}
