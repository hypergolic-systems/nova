//! Antenna spec — static config, no runtime state. The Network reads
//! these to compute per-link SNR and Shannon-scaled rate.
//! Mirrors `mod/Nova.Core/Components/Communications/Antenna.cs`.

#[derive(Copy, Clone, Debug, PartialEq)]
pub struct Antenna {
    /// Maximum transmit power (W). Numerator of SNR(A→B); also the
    /// EC draw once tx becomes a controlled variable in a later layer.
    pub tx_power: f64,
    /// Antenna gain (dimensionless). Multiplies the tx and rx
    /// contributions to received power: `SNR ∝ Gain_A · Gain_B`.
    pub gain: f64,
    /// Hardware ceiling on data rate (bit/s). Reached on a self-link
    /// (A→A) at exactly `ref_distance`; the rate formula caps here for
    /// closer-than-design separations.
    pub max_rate: f64,
    /// Design distance (m): the separation at which two identical
    /// antennas of this type achieve `max_rate`.
    pub ref_distance: f64,
}

impl Antenna {
    /// SNR an A→A self-link would achieve at exactly `ref_distance`:
    /// `tx_power · gain² / (ref_distance² · noise_floor)`. Used as the
    /// denominator when scaling Shannon capacity to a rate.
    pub fn ref_snr(&self, noise_floor: f64) -> f64 {
        self.tx_power * self.gain * self.gain
            / (self.ref_distance * self.ref_distance * noise_floor)
    }
}
