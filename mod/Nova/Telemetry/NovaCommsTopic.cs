using System.Text;
using Dragonglass.Telemetry.Topics;
using Nova.Communications;
using Nova.Components;
using Nova.Core.Communications;
using UnityEngine;

namespace Nova.Telemetry;

// Per-vessel comms summary topic. Drives the HUD top bar's "LINK"
// chip (path-to-KSC + bottleneck rate) and "XMIT" progress (active
// science transmission, if any).
//
// MonoBehaviour attached to the Vessel's GameObject by
// NovaSubscriptionManager when a `NovaComms/<vesselGuid>` subscribe
// signal arrives. Lifetime tied to the Vessel.
//
// Wire format (positional):
//   [vesselId,
//    hasPathToKsc,           // 0/1
//    bottleneckBps,           // bytes/sec along the chosen path; 0 if no path
//    directSnr,               // direct vessel→KSC link SNR (linear); 0 if no direct edge
//    directRateBps,           // direct vessel→KSC link RateBps (theoretical capacity
//                             // before path bottlenecks); 0 if no direct edge
//    directMaxRateBps,        // antenna-pair hardware ceiling along vessel→KSC.
//    directSnrFloor,          // linear SNR threshold below which the direct edge
//                             // drops to zero rate (bucket-1 cutoff for the chosen
//                             // antenna pair). Display as the link's noise floor.
//    peerLabel,               // "KSC" for direct, "KSC (via NAME)" when the chosen
//                             // path's first hop is a relay vessel. Empty when DARK.
//    txActive,                // 0/1 — is a Packet currently in flight
//    txRateBps,               // current allocated rate of the active Packet
//    txDeliveredBytes,        // cumulative
//    txTotalBytes]
//
// All path/connectivity fields read from `Endpoint.PathToHome`, which
// CommunicationsNetwork refreshes once per Solve. Update() poll-and-
// compares against the last emitted state and only MarkDirty's when a
// field would actually change on the wire — keeping the broadcaster
// quiet when nothing's moving (the common case at rest).
public sealed class NovaCommsTopic : Topic {
  private const string LogPrefix = "[Nova/Telemetry] ";

  private Vessel _vessel;
  private NovaVesselModule _vesselModule;
  private string _vesselGuid;
  private string _name;

  // Last-emitted snapshot. Update() recomputes and compares; Update()
  // only marks dirty when at least one field would change. WriteData
  // emits straight from these so the wire payload always matches what
  // the change-detector saw.
  private bool _hasPath;
  private double _bottleneckBps;
  private double _directSnr;
  private double _directRateBps;
  private double _directMaxRateBps;
  private double _directSnrFloor;
  private string _peerLabel = "";
  private bool _txActive;
  private double _txRateBps;
  private long _txDelivered;
  private long _txTotal;

  public override string Name => _name;

  protected override void OnEnable() {
    _vessel = GetComponent<Vessel>();
    if (_vessel == null) {
      Debug.LogWarning(LogPrefix + "NovaCommsTopic attached to non-Vessel GameObject; disabling");
      enabled = false;
      return;
    }
    _vesselModule = _vessel.GetComponent<NovaVesselModule>();
    _vesselGuid = _vessel.id.ToString("D");
    _name = "NovaComms/" + _vesselGuid;
    base.OnEnable();
    // Force the first emit so subscribers see initial state even if
    // it happens to match the zero-default cached fields.
    SampleAndUpdate(forceEmit: true);
  }

  private void Update() {
    SampleAndUpdate(forceEmit: false);
  }

  private void SampleAndUpdate(bool forceEmit) {
    var summary = ReadPathSummary();
    var peerLabel = ResolvePeerLabel(summary);
    var (txActive, txRate, txDelivered, txTotal) = ReadTransmission();

    bool changed = forceEmit
        || summary.HasPath != _hasPath
        || summary.BottleneckBps != _bottleneckBps
        || summary.DirectSnr != _directSnr
        || summary.DirectRateBps != _directRateBps
        || summary.DirectMaxRateBps != _directMaxRateBps
        || summary.DirectSnrFloor != _directSnrFloor
        || peerLabel != _peerLabel
        || txActive != _txActive
        || txRate != _txRateBps
        || txDelivered != _txDelivered
        || txTotal != _txTotal;

    if (!changed) return;

    _hasPath = summary.HasPath;
    _bottleneckBps = summary.BottleneckBps;
    _directSnr = summary.DirectSnr;
    _directRateBps = summary.DirectRateBps;
    _directMaxRateBps = summary.DirectMaxRateBps;
    _directSnrFloor = summary.DirectSnrFloor;
    _peerLabel = peerLabel;
    _txActive = txActive;
    _txRateBps = txRate;
    _txDelivered = txDelivered;
    _txTotal = txTotal;

    MarkDirty();
  }

  // Build the "PEER" string for the UI. Direct paths read as plain
  // "KSC"; relayed paths annotate the destination with the immediate
  // next-hop vessel's name ("KSC (via Probe Mün-1)") so the player
  // knows which craft is doing the relaying. Returns empty when the
  // vessel has no path — the UI hides the row in that state. The
  // FlightGlobals.Vessels scan is cheap (handful of vessels) and
  // only runs when the next hop or path state actually changes.
  private string ResolvePeerLabel(PathSummary s) {
    if (!s.HasPath) return "";
    var homeId = NovaCommunicationsAddon.Instance?.KscEndpoint?.Id ?? "KSC";
    if (string.IsNullOrEmpty(s.NextHopId) || s.NextHopId == homeId) return homeId;
    var viaName = LookupVesselName(s.NextHopId) ?? s.NextHopId;
    return homeId + " (via " + viaName + ")";
  }

  private static string LookupVesselName(string guid) {
    var vessels = FlightGlobals.Vessels;
    if (vessels == null) return null;
    for (int i = 0; i < vessels.Count; i++) {
      var v = vessels[i];
      if (v == null) continue;
      if (v.id.ToString("D") == guid) return v.vesselName;
    }
    return null;
  }

  // Pull the cached path-to-KSC summary off our endpoint. Returns
  // `default` when the comms addon hasn't registered us yet (race
  // window between vessel load and addon Awake) or when the network
  // hasn't run a Solve yet — both produce a "DARK" link on the UI,
  // which is the correct degraded state.
  private PathSummary ReadPathSummary() {
    var addon = NovaCommunicationsAddon.Instance;
    if (addon == null) return default;
    var ep = addon.GetVesselEndpoint(_vessel.id);
    return ep == null ? default : ep.PathToHome;
  }

  private (bool active, double rate, long delivered, long total) ReadTransmission() {
    if (_vesselModule == null || _vesselModule.Virtual == null) {
      return (false, 0, 0, 0);
    }
    var packet = _vesselModule.Virtual.Systems.Transmission.ActivePacket;
    if (packet == null || packet.Status != JobStatus.Active) {
      return (false, 0, 0, 0);
    }
    return (true, packet.AllocatedRateBps, packet.DeliveredBytes, packet.TotalBytes);
  }

  public override void WriteData(StringBuilder sb) {
    JsonWriter.Begin(sb, '[');
    bool first = true;

    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteString(sb, _vesselGuid);

    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteBoolAsBit(sb, _hasPath);
    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteDouble(sb, _bottleneckBps);
    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteDouble(sb, _directSnr);
    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteDouble(sb, _directRateBps);
    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteDouble(sb, _directMaxRateBps);
    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteDouble(sb, _directSnrFloor);
    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteString(sb, _peerLabel ?? "");

    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteBoolAsBit(sb, _txActive);
    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteDouble(sb, _txRateBps);
    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteDouble(sb, _txDelivered);
    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteDouble(sb, _txTotal);

    JsonWriter.End(sb, ']');
  }
}
