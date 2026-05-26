using System.Text;
using Dragonglass.Telemetry.Topics;
using Nova.Communications;
using Nova.Components;
using Nova.Core.Communications;
using Nova.Core.Telemetry;
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
//    linkSnr,                 // first-hop link SNR (linear); 0 if DARK
//    linkRateBps,             // first-hop link RateBps (live, before path bottleneck)
//    linkMaxRateBps,          // first-hop antenna-pair hardware ceiling
//    linkSnrFloor,            // linear SNR threshold below which the first hop
//                             // drops to zero rate (bucket-1 cutoff for the
//                             // chosen antenna pair). Display as the link's
//                             // noise floor.
//    peerLabel,               // "KSC" for direct, "KSC (via NAME)" when the
//                             // chosen path's first hop is a relay vessel.
//                             // Empty when DARK.
//    txActive,                // 0/1 — is a Packet currently in flight
//    txRateBps,               // current allocated rate of the active Packet
//    txDeliveredBytes,        // cumulative
//    txTotalBytes]
//
// HasPath / BottleneckBps / NextHopId read from `Endpoint.PathToHome`,
// which CommunicationsNetwork refreshes once per Solve. Link stats
// (SNR / rate / ceiling / floor) are recomputed live per Update via
// `CommunicationsNetwork.ComputeLinkStats(Path[0])` so they track
// distance even when no Solve has fired — Solve cadence is bucket-
// event-driven and would otherwise freeze the dB readout during time
// warp once geometry sits inside the top bucket. Update() poll-and-
// compares against the last emitted state and only MarkDirty's when
// a field would actually change on the wire.
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
  private double _linkSnr;
  private double _linkRateBps;
  private double _linkMaxRateBps;
  private double _linkSnrFloor;
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
    var (linkSnr, linkRate, linkMaxRate, linkSnrFloor) = ComputeFirstHopStats(summary);
    var peerLabel = ResolvePeerLabel(summary);
    var (txActive, txRate, txDelivered, txTotal) = ReadTransmission();

    bool changed = forceEmit
        || summary.HasPath != _hasPath
        || summary.BottleneckBps != _bottleneckBps
        || linkSnr != _linkSnr
        || linkRate != _linkRateBps
        || linkMaxRate != _linkMaxRateBps
        || linkSnrFloor != _linkSnrFloor
        || peerLabel != _peerLabel
        || txActive != _txActive
        || txRate != _txRateBps
        || txDelivered != _txDelivered
        || txTotal != _txTotal;

    if (!changed) return;

    _hasPath = summary.HasPath;
    _bottleneckBps = summary.BottleneckBps;
    _linkSnr = linkSnr;
    _linkRateBps = linkRate;
    _linkMaxRateBps = linkMaxRate;
    _linkSnrFloor = linkSnrFloor;
    _peerLabel = peerLabel;
    _txActive = txActive;
    _txRateBps = txRate;
    _txDelivered = txDelivered;
    _txTotal = txTotal;

    MarkDirty();
  }

  // Live first-hop stats: walks the chosen path's first link and
  // recomputes (SNR, rate, ceiling, noise floor) from current
  // endpoint positions at Planetarium.GetUniversalTime(). Returns
  // zeros when the vessel has no path, mirroring the DARK state on
  // the wire. Tied to Update() so the dB readout tracks distance
  // smoothly during time warp — Solve cadence is bucket-event-driven
  // and would otherwise freeze these values between transitions.
  private static (double snr, double rate, double maxRate, double snrFloor)
      ComputeFirstHopStats(PathSummary s) {
    if (!s.HasPath || s.Path == null || s.Path.Count == 0) return (0, 0, 0, 0);
    var firstHop = s.Path[0];
    if (firstHop == null) return (0, 0, 0, 0);
    var ut = Planetarium.GetUniversalTime();
    return CommunicationsNetwork.ComputeLinkStats(firstHop.From, firstHop.To, ut);
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
    CommsFormatter.Write(sb, _vesselGuid,
        _hasPath, _bottleneckBps,
        _linkSnr, _linkRateBps, _linkMaxRateBps,
        _linkSnrFloor, _peerLabel,
        _txActive, _txRateBps, _txDelivered, _txTotal);
  }
}
