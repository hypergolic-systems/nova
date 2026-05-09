//! Mirror of `mod/Nova.Tests/Communications/AntennaTests.cs`.

use approx::assert_relative_eq;
use nova_sim::comms::{Antenna, NOISE_FLOOR};

#[test]
fn ref_snr_matches_closed_form() {
    // tx_power · gain² / (ref_distance² · n0)
    let a = Antenna { tx_power: 100.0, gain: 10.0, max_rate: 1000.0, ref_distance: 10.0 };
    let n0 = 0.5;
    let expected = 100.0 * 10.0 * 10.0 / (10.0 * 10.0 * n0);
    assert_relative_eq!(a.ref_snr(n0), expected, epsilon = 1e-9);
}

#[test]
fn self_link_at_design_distance_achieves_max_rate() {
    // A→A at exactly ref_distance: SNR == SNR_ref → ratio = 1 → rate = max_rate.
    let a = Antenna { tx_power: 100.0, gain: 10.0, max_rate: 1000.0, ref_distance: 10.0 };
    let n0 = NOISE_FLOOR;
    let snr_at_ref =
        a.tx_power * a.gain * a.gain / (a.ref_distance * a.ref_distance * n0);
    assert_relative_eq!(a.ref_snr(n0), snr_at_ref, epsilon = 1e-9);

    let ratio = (1.0 + snr_at_ref).ln() / (1.0 + a.ref_snr(n0)).ln();
    assert_relative_eq!(ratio, 1.0, epsilon = 1e-12);
}

#[test]
fn degenerate_antenna_ref_snr_is_zero_when_gain_zero() {
    let a = Antenna { tx_power: 100.0, gain: 0.0, max_rate: 1000.0, ref_distance: 10.0 };
    assert_relative_eq!(a.ref_snr(1.0), 0.0);
}
