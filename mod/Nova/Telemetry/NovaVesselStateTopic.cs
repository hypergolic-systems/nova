using System.Text;
using Dragonglass.Telemetry.Topics;
using Nova.Core.Telemetry;
using UnityEngine;

namespace Nova.Telemetry;

// Per-vessel dynamic-state topic. Drives the Vessel section at the
// top of the side-rack accordion: identity (name · situation · body)
// and live state (mass · parts · crew).
//
// MonoBehaviour attached to the Vessel's GameObject by
// NovaSubscriptionManager when a `NovaVesselState/<vesselGuid>`
// subscribe signal arrives. Lifetime tied to the Vessel.
//
// Wire format (positional):
//   [vesselId, vesselName,
//    situation, bodyName,
//    totalMassKg, partCount,
//    crewCount, crewCapacity]
//
// Cadence: poll-and-compare each Update; MarkDirty only on change.
// Mass / crew / situation / body are step functions in ordinary play
// (stage fires, EVA, dock, SoI change). The broadcaster's flush
// cadence handles the rest.
public sealed class NovaVesselStateTopic : Topic {
  private const string LogPrefix = "[Nova/Telemetry] ";

  private Vessel _vessel;
  private string _vesselGuid;
  private string _name;

  // Last-emitted snapshot.
  private string _vesselName = "";
  private int _situation = -1;
  private string _bodyName = "";
  private double _totalMassKg;
  private int _partCount;
  private int _crewCount;
  private int _crewCapacity;

  public override string Name => _name;

  protected override void OnEnable() {
    _vessel = GetComponent<Vessel>();
    if (_vessel == null) {
      Debug.LogWarning(LogPrefix + "NovaVesselStateTopic attached to non-Vessel GameObject; disabling");
      enabled = false;
      return;
    }
    _vesselGuid = _vessel.id.ToString("D");
    _name = "NovaVesselState/" + _vesselGuid;
    base.OnEnable();
    SampleAndUpdate(forceEmit: true);
  }

  private void Update() {
    SampleAndUpdate(forceEmit: false);
  }

  private void SampleAndUpdate(bool forceEmit) {
    var name = _vessel.GetDisplayName() ?? _vessel.vesselName ?? "";
    var situation = (int)_vessel.situation;
    var bodyName = _vessel.mainBody != null ? (_vessel.mainBody.bodyName ?? "") : "";
    var massT = _vessel.GetTotalMass();
    var massKg = massT * 1000.0;
    var partCount = _vessel.parts != null ? _vessel.parts.Count : 0;
    var crewCount = _vessel.GetCrewCount();
    var crewCapacity = 0;
    if (_vessel.parts != null) {
      for (int i = 0; i < _vessel.parts.Count; i++) {
        var p = _vessel.parts[i];
        if (p != null) crewCapacity += p.CrewCapacity;
      }
    }

    bool changed = forceEmit
        || name != _vesselName
        || situation != _situation
        || bodyName != _bodyName
        || massKg != _totalMassKg
        || partCount != _partCount
        || crewCount != _crewCount
        || crewCapacity != _crewCapacity;

    if (!changed) return;

    _vesselName = name;
    _situation = situation;
    _bodyName = bodyName;
    _totalMassKg = massKg;
    _partCount = partCount;
    _crewCount = crewCount;
    _crewCapacity = crewCapacity;

    MarkDirty();
  }

  public override void WriteData(StringBuilder sb) {
    VesselStateFormatter.Write(sb, _vesselGuid, _vesselName,
        _situation, _bodyName,
        _totalMassKg, _partCount,
        _crewCount, _crewCapacity);
  }
}
