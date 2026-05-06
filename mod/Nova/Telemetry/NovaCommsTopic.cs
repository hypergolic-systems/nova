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
//                             // The denominator the comms solver uses for rate
//                             // bucketing. UI uses `directRateBps/directMaxRateBps`
//                             // as the signal-strength fraction.
//    txActive,                // 0/1 — is a Packet currently in flight
//    txRateBps,               // current allocated rate of the active Packet
//    txDeliveredBytes,        // cumulative
//    txTotalBytes]
//
// "Direct" fields are taken off the single direct vessel→KSC edge in
// the graph. They're the meaningful "theoretical bandwidth" readout
// — what THIS antenna pair could achieve to KSC if not bottlenecked
// elsewhere. Zero when there's no direct edge (vessel out of LOS to
// KSC and only reachable via relay), in which case `bottleneckBps`
// is still populated from the relayed path.
//
// Path-bottleneck fields update on `CommunicationsNetwork.Solve()`
// (event-driven). Transmission byte counters tick every Solve as the
// allocator advances DeliveredBytes. Mark dirty every frame for
// simplicity — the broadcaster's flush cadence gates the wire rate;
// stale-frame skipping at this layer would only save StringBuilder
// work, not bandwidth.
public sealed class NovaCommsTopic : Topic {
  private const string LogPrefix = "[Nova/Telemetry] ";

  private Vessel _vessel;
  private NovaVesselModule _vesselModule;
  private string _vesselGuid;
  private string _name;

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
    MarkDirty();
  }

  private void Update() {
    MarkDirty();
  }

  public override void WriteData(StringBuilder sb) {
    JsonWriter.Begin(sb, '[');
    bool first = true;

    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteString(sb, _vesselGuid);

    bool hasPath = false;
    double bottleneckBps = 0;
    double directSnr = 0;
    double directRateBps = 0;
    double directMaxRateBps = 0;
    var addon = NovaCommunicationsAddon.Instance;
    var ep = addon != null ? addon.GetVesselEndpoint(_vessel.id) : null;
    var ksc = addon != null ? addon.KscEndpoint : null;
    if (ep != null && ksc != null && addon.Network != null) {
      var graph = addon.Network.Graph;
      var path = MaxRatePath.Find(graph, ep, ksc);
      if (path != null && path.Count > 0) {
        hasPath = true;
        var minRate = double.PositiveInfinity;
        foreach (var l in path) {
          if (l.RateBps < minRate) minRate = l.RateBps;
        }
        bottleneckBps = minRate == double.PositiveInfinity ? 0 : minRate;
      }
      foreach (var l in graph.Links) {
        if (l.From == ep && l.To == ksc) {
          directSnr = l.Snr;
          directRateBps = l.RateBps;
          break;
        }
      }
      // Antenna-pair hardware ceiling for vessel→KSC, matching the
      // private LinkMaxCeiling helper in CommunicationsNetwork. Reading
      // the endpoint antennas directly avoids exposing that helper.
      foreach (var tx in ep.Antennas) {
        foreach (var rx in ksc.Antennas) {
          var c = System.Math.Min(tx.MaxRate, rx.MaxRate);
          if (c > directMaxRateBps) directMaxRateBps = c;
        }
      }
    }

    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteBoolAsBit(sb, hasPath);
    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteDouble(sb, bottleneckBps);
    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteDouble(sb, directSnr);
    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteDouble(sb, directRateBps);
    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteDouble(sb, directMaxRateBps);

    bool txActive = false;
    double txRate = 0;
    long txDelivered = 0;
    long txTotal = 0;
    var packet = _vesselModule != null && _vesselModule.Virtual != null
        ? _vesselModule.Virtual.Systems.Transmission.ActivePacket
        : null;
    if (packet != null && packet.Status == JobStatus.Active) {
      txActive = true;
      txRate = packet.AllocatedRateBps;
      txDelivered = packet.DeliveredBytes;
      txTotal = packet.TotalBytes;
    }

    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteBoolAsBit(sb, txActive);
    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteDouble(sb, txRate);
    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteDouble(sb, txDelivered);
    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteDouble(sb, txTotal);

    JsonWriter.End(sb, ']');
  }
}
