//! Sanity checks on `Endpoint::position_at` dispatch — one for vessel
//! kind, one for ground kind. The richer position-evaluation tests
//! live in `comms_motion.rs` and `comms_ground_station.rs`.

use approx::assert_relative_eq;
use nova_sim::comms::{Antenna, Endpoint, EndpointId, EndpointKind};
use nova_sim::ephem::BodyId;
use nova_sim::fixtures::{ids, kerbol_bodies};
use nova_sim::orbit::OrbitalElements;
use nova_sim::world::{Vessel, VesselId, World};

fn antenna() -> Antenna {
    Antenna { tx_power: 1.0, gain: 1.0, max_rate: 1000.0, ref_distance: 1.0e6 }
}

fn world_with_vessel() -> World {
    let v = Vessel::in_orbit(
        VesselId(1),
        "Sat",
        ids::KERBIN,
        OrbitalElements::circular(600_000.0 + 700_000.0),
    );
    World::builder().bodies(kerbol_bodies()).vessel(v).build()
}

#[test]
fn vessel_kind_endpoint_matches_world_position() {
    let world = world_with_vessel();
    let ep = Endpoint {
        id: EndpointId::Vessel(VesselId(1)),
        name: "Sat".into(),
        kind: EndpointKind::Vessel(VesselId(1)),
        motion: None,
        primary_body: Some(ids::KERBIN),
        antennas: vec![antenna()],
        is_predictable: true,
        path_to_home: Default::default(),
    };
    let ut = 100.0;
    let p = ep.position_at(&world, ut);
    let expected = world.vessel_position_absolute(VesselId(1), ut).unwrap();
    assert_relative_eq!(p.x, expected.x, max_relative = 1e-12);
    assert_relative_eq!(p.y, expected.y, max_relative = 1e-12);
    assert_relative_eq!(p.z, expected.z, max_relative = 1e-12);
}

#[test]
fn ground_kind_endpoint_at_lon_zero_aligns_with_x_offset_from_kerbin_centre() {
    // Ground station at lat=0, lon=0, alt=0 on Kerbin: at ut=0 the
    // body-frame offset is (radius, 0, 0). World position = Kerbin
    // centre + that offset.
    let world = world_with_vessel();
    let ep = Endpoint {
        id: EndpointId::Ground(0),
        name: "TestGround".into(),
        kind: EndpointKind::Ground {
            primary: ids::KERBIN,
            latitude_deg: 0.0,
            longitude_deg: 0.0,
            altitude_m: 0.0,
        },
        motion: None,
        primary_body: Some(ids::KERBIN),
        antennas: vec![antenna()],
        is_predictable: true,
        path_to_home: Default::default(),
    };
    let p = ep.position_at(&world, 0.0);
    let kerbin_centre = world.ephemeris.body_position_absolute(ids::KERBIN, 0.0);
    let local = p - kerbin_centre;
    assert_relative_eq!(local.x, 600_000.0, max_relative = 1e-9);
    assert_relative_eq!(local.y, 0.0, epsilon = 1e-6);
    assert_relative_eq!(local.z, 0.0, epsilon = 1e-6);

    // Touch BodyId import to silence dead-code on the import.
    let _ = BodyId(0);
}
