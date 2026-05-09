//! Mirror of `mod/Nova.Tests/Communications/OccluderSetTests.cs`.

use nova_sim::comms::occluder_set;
use nova_sim::ephem::{Body, BodyId, BodyRotation, Ephemeris};

// Body ids in the stock-shape tree below.
const SUN: BodyId = BodyId(0);
const MOHO: BodyId = BodyId(1);
const EVE: BodyId = BodyId(2);
const GILLY: BodyId = BodyId(3);
const KERBIN: BodyId = BodyId(4);
const MUN: BodyId = BodyId(5);
const MINMUS: BodyId = BodyId(6);
const DUNA: BodyId = BodyId(7);
const IKE: BodyId = BodyId(8);
const JOOL: BodyId = BodyId(9);
const LAYTHE: BodyId = BodyId(10);
const VALL: BodyId = BodyId(11);
const TYLO: BodyId = BodyId(12);
const BOP: BodyId = BodyId(13);
const POL: BodyId = BodyId(14);

fn placeholder(id: BodyId, name: &str, parent: Option<BodyId>) -> Body {
    Body {
        id,
        name: name.into(),
        parent,
        mu: 1.0,
        radius: 1.0,
        soi_radius: f64::INFINITY,
        atmosphere: None,
        rotation: BodyRotation::default(),
        orbit: None,
    }
}

/// Sun → {Moho, Eve→Gilly, Kerbin→{Mun, Minmus}, Duna→Ike,
/// Jool→{Laythe, Vall, Tylo, Bop, Pol}}.
fn stock_shape() -> Ephemeris {
    Ephemeris::new(vec![
        placeholder(SUN, "Sun", None),
        placeholder(MOHO, "Moho", Some(SUN)),
        placeholder(EVE, "Eve", Some(SUN)),
        placeholder(GILLY, "Gilly", Some(EVE)),
        placeholder(KERBIN, "Kerbin", Some(SUN)),
        placeholder(MUN, "Mun", Some(KERBIN)),
        placeholder(MINMUS, "Minmus", Some(KERBIN)),
        placeholder(DUNA, "Duna", Some(SUN)),
        placeholder(IKE, "Ike", Some(DUNA)),
        placeholder(JOOL, "Jool", Some(SUN)),
        placeholder(LAYTHE, "Laythe", Some(JOOL)),
        placeholder(VALL, "Vall", Some(JOOL)),
        placeholder(TYLO, "Tylo", Some(JOOL)),
        placeholder(BOP, "Bop", Some(JOOL)),
        placeholder(POL, "Pol", Some(JOOL)),
    ])
}

fn sorted(mut v: Vec<BodyId>) -> Vec<BodyId> {
    v.sort_by_key(|b| b.0);
    v
}

#[test]
fn same_soi_ksc_and_kerbin_orbit_only_kerbin() {
    let ephem = stock_shape();
    let set = occluder_set(Some(KERBIN), Some(KERBIN), &ephem);
    assert_eq!(sorted(set), vec![KERBIN]);
}

#[test]
fn cross_soi_mun_orbit_and_kerbin_orbit_kerbin_and_mun() {
    let ephem = stock_shape();
    let set = occluder_set(Some(MUN), Some(KERBIN), &ephem);
    assert_eq!(sorted(set), sorted(vec![KERBIN, MUN]));
}

#[test]
fn same_soi_two_mun_orbiters_only_mun() {
    let ephem = stock_shape();
    let set = occluder_set(Some(MUN), Some(MUN), &ephem);
    assert_eq!(sorted(set), vec![MUN]);
}

#[test]
fn interplanetary_kerbin_to_moho_kerbin_subtree_plus_moho_plus_sun() {
    let ephem = stock_shape();
    let set = occluder_set(Some(KERBIN), Some(MOHO), &ephem);
    assert_eq!(sorted(set), sorted(vec![SUN, MOHO, KERBIN, MUN, MINMUS]));
}

#[test]
fn interplanetary_kerbin_to_moho_excludes_eve_and_gilly() {
    let ephem = stock_shape();
    let set = occluder_set(Some(KERBIN), Some(MOHO), &ephem);
    assert!(!set.contains(&EVE));
    assert!(!set.contains(&GILLY));
}

#[test]
fn interplanetary_kerbin_to_laythe_ten_bodies() {
    let ephem = stock_shape();
    let set = occluder_set(Some(KERBIN), Some(LAYTHE), &ephem);
    let mut expected = vec![SUN, KERBIN, MUN, MINMUS, JOOL, LAYTHE, VALL, TYLO, BOP, POL];
    expected.sort_by_key(|b| b.0);
    assert_eq!(sorted(set), expected);
}

#[test]
fn null_primary_body_either_side_returns_empty() {
    let ephem = stock_shape();
    assert!(occluder_set(None, Some(KERBIN), &ephem).is_empty());
    assert!(occluder_set(Some(KERBIN), None, &ephem).is_empty());
    assert!(occluder_set(None, None, &ephem).is_empty());
}
