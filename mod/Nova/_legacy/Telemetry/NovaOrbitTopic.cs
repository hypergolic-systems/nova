using System.Text;
using Dragonglass.Telemetry.Topics;
using UnityEngine;

namespace Nova.Telemetry;

// Per-vessel orbit + mission-time topic. The HUD top bar's "ORBIT"
// section reads apA/peA + period/eccentricity here; the "MISSION
// CLOCK" section reads missionTime + launchTime from the same frame
// so a single subscription drives both readouts.
//
// MonoBehaviour attached to the Vessel's GameObject by
// NovaSubscriptionManager when a `NovaOrbit/<vesselGuid>` subscribe
// signal arrives. Lifetime tied to the Vessel.
//
// Wire format (positional):
//   [vesselId, bodyName,
//    apA, peA, eccentricity, inclination, period,
//    missionTime, launchTime]
//
// `inclination` is in degrees (KSP's stock unit on `Orbit.inclination`).
//
// `period` is 0 for sub-orbital / hyperbolic trajectories
// (eccentricity ≥ 1); the UI hides the period readout in that case.
// `apA`/`peA` are 0 only when `vessel.orbit` itself is null (rare —
// loading transitions); the UI greys out the orbit section in that
// case rather than displaying zeros.
public sealed class NovaOrbitTopic : Topic {
  private const string LogPrefix = "[Nova/Telemetry] ";

  private Vessel _vessel;
  private string _vesselGuid;
  private string _name;

  public override string Name => _name;

  protected override void OnEnable() {
    _vessel = GetComponent<Vessel>();
    if (_vessel == null) {
      Debug.LogWarning(LogPrefix + "NovaOrbitTopic attached to non-Vessel GameObject; disabling");
      enabled = false;
      return;
    }
    _vesselGuid = _vessel.id.ToString("D");
    _name = "NovaOrbit/" + _vesselGuid;
    base.OnEnable();
    MarkDirty();
  }

  // Orbit elements + missionTime change continuously while the vessel
  // is in flight (under thrust, drag, or even just clock advance).
  // Mark dirty every frame; the broadcaster's flush cadence gates the
  // wire rate.
  private void Update() {
    MarkDirty();
  }

  public override void WriteData(StringBuilder sb) {
    JsonWriter.Begin(sb, '[');
    bool first = true;

    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteString(sb, _vesselGuid);

    var bodyName = _vessel.mainBody != null ? (_vessel.mainBody.bodyName ?? "") : "";
    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteString(sb, bodyName);

    double apA = 0, peA = 0, ecc = 0, inc = 0, period = 0;
    var orbit = _vessel.orbitDriver != null ? _vessel.orbitDriver.orbit : null;
    if (orbit != null) {
      apA = orbit.ApA;
      peA = orbit.PeA;
      ecc = orbit.eccentricity;
      inc = orbit.inclination;
      period = ecc < 1.0 ? orbit.period : 0.0;
    }

    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteDouble(sb, apA);
    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteDouble(sb, peA);
    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteDouble(sb, ecc);
    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteDouble(sb, inc);
    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteDouble(sb, period);

    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteDouble(sb, _vessel.missionTime);
    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteDouble(sb, _vessel.launchTime);

    JsonWriter.End(sb, ']');
  }
}
