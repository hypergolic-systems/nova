//! Ground-station endpoint factories. Mirrors
//! `mod/Nova.Core/Communications/GroundStation.cs`.
//!
//! The C# version takes a `bodyPositionAt: Func<UT, Vec3d>` closure
//! because endpoints owned a closure-shaped position evaluator. The
//! Rust port is data-driven — surface endpoints store
//! `EndpointKind::Ground { primary, lat, lon, alt }` and look up the
//! body's centre through `World::ephemeris` on each `position_at`
//! call. Constructors here produce a `GroundStationSpec` (raw data)
//! that `CommsSystem::add_ground_station` materialises into an
//! `Endpoint` once a fresh `Ground(n)` id has been minted.

use crate::ephem::BodyId;

use super::antenna::Antenna;
use super::endpoint::{Endpoint, EndpointId, EndpointKind, PathSummary};
use super::motion::MotionModel;

/// Static spec for a surface-mounted endpoint. Becomes an `Endpoint`
/// once `CommsSystem` mints a `Ground(n)` id for it.
#[derive(Clone, Debug)]
pub struct GroundStationSpec {
    pub name: String,
    pub primary: BodyId,
    pub latitude_deg: f64,
    pub longitude_deg: f64,
    pub altitude_m: f64,
    pub antennas: Vec<Antenna>,
}

impl GroundStationSpec {
    /// Materialise as a fully-populated `Endpoint` with the given
    /// ground-counter `id`. `motion` carries a `Surface` hint;
    /// `primary_body` is set to the parent body so the occluder set
    /// resolves correctly.
    pub fn into_endpoint(self, id: u32) -> Endpoint {
        let GroundStationSpec {
            name,
            primary,
            latitude_deg,
            longitude_deg,
            altitude_m,
            antennas,
        } = self;
        Endpoint {
            id: EndpointId::Ground(id),
            name,
            kind: EndpointKind::Ground { primary, latitude_deg, longitude_deg, altitude_m },
            motion: Some(MotionModel::Surface {
                parent: primary,
                latitude_deg,
                longitude_deg,
                altitude_m,
            }),
            primary_body: Some(primary),
            antennas,
            is_predictable: true,
            path_to_home: PathSummary::default(),
        }
    }
}

/// KSC on `kerbin`. Lat/lon/alt match stock-KSP launch-pad coordinates.
/// Tx/Gain/MaxRate/RefDistance set several orders of magnitude above
/// any vessel-mounted antenna so the homeworld stays the dominant link
/// partner; RefDistance reaches outer-system so distant probes still
/// close a usable link to KSC at full hardware rate.
pub fn ksc(kerbin: BodyId) -> GroundStationSpec {
    GroundStationSpec {
        name: "KSC".to_string(),
        primary: kerbin,
        latitude_deg: -0.0972,
        longitude_deg: -74.5577,
        altitude_m: 75.0,
        antennas: vec![Antenna {
            tx_power: 1.0e9,
            gain: 1.0e3,
            max_rate: 1.0e9,
            ref_distance: 1.0e10,
        }],
    }
}
