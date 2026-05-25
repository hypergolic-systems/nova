using Nova.Core.Persistence.Protos;

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

  // Whether the antenna is currently deployed/extended. Fixed (non-
  // deployable) and integrated antennas keep this true and never
  // touch it; deployable antennas drive it from their KSP-side
  // wrapper. The network filters non-deployed antennas at every
  // iteration site (BestPair, LinkMaxCeiling, etc.) — a retracted
  // antenna behaves as if absent, but still appears in
  // `Endpoint.Antennas` so UI / telemetry can surface its state.
  public bool IsDeployed = true;

  // True iff the part has a deploy animation — i.e. the cfg named an
  // `animationName` that the KSP-side wrapper resolved. Fixed (non-
  // deployable) and integrated antennas leave this false; their
  // IsDeployed stays true for the part's lifetime. Pushed in by
  // NovaAntennaModule.OnStart so telemetry / UI can gate the
  // EXT/RET control without reaching back into the module.
  public bool IsDeployable;

  // True iff a deployable antenna can be retracted after extension.
  // Mirrors the cfg's `retractable` flag (default true). One-shot
  // deployables (cfg `retractable = false`) leave this false — the
  // UI shows an EXT button while retracted and no control once
  // deployed. Fixed (non-deployable) antennas leave this false too,
  // but the UI gates on IsDeployable first so the value is moot.
  public bool IsRetractable;

  // True iff Load() consumed an AntennaState from proto. The KSP-side
  // wrapper uses this to decide whether to push IsDeployed into the
  // stock ModuleDeployableAntenna (loaded save) or pull from it
  // (fresh launch — stock's deployState carries the editor-time
  // intent through the launch pipeline).
  public bool LoadedFromSave;

  // SNR an A→A self-link would achieve at exactly RefDistance.
  // Closed form: TxPower · Gain² / (RefDistance² · noiseFloor).
  // Used as the denominator when scaling Shannon capacity to a rate.
  public double RefSnr(double noiseFloor) {
    return TxPower * Gain * Gain / (RefDistance * RefDistance * noiseFloor);
  }

  public override void Save(PartState state) {
    state.Antenna = new AntennaState { IsDeployed = IsDeployed };
  }

  public override void Load(PartState state) {
    if (state.Antenna == null) return;
    IsDeployed = state.Antenna.IsDeployed;
    LoadedFromSave = true;
  }
}
