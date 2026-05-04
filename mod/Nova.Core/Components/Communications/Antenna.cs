namespace Nova.Core.Components.Communications;

// Communications antenna. Static-config component today: the four
// fields below come straight from cfg via the (future) factory; no
// runtime state, no LP/staging registration. The Network reads
// these to compute per-link SNR and Shannon-scaled rate.
public class Antenna : VirtualComponent {

  // Maximum transmit power (W). Numerator of SNR(A→B); also the EC
  // draw once tx becomes a controlled variable in a later layer.
  public double TxPower;

  // Antenna gain (dimensionless). Multiplies the tx and rx
  // contributions to received power: SNR ∝ Gain_A · Gain_B.
  public double Gain;

  // Hardware ceiling on data rate (bit/s). Reached on a self-link
  // (A→A) at exactly RefDistance; the rate formula caps here for
  // closer-than-design separations.
  public double MaxRate;

  // Design distance (m): the separation at which two identical
  // antennas of this type achieve MaxRate. Defines this antenna's
  // reference SNR via the formula in RefSnr.
  public double RefDistance;

  // SNR an A→A self-link would achieve at exactly RefDistance.
  // Closed form: TxPower · Gain² / (RefDistance² · noiseFloor).
  // Used as the denominator when scaling Shannon capacity to a rate.
  public double RefSnr(double noiseFloor) {
    return TxPower * Gain * Gain / (RefDistance * RefDistance * noiseFloor);
  }
}
