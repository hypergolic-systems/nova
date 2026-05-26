using System.Text;

namespace Nova.Core.Telemetry;

// Per-vessel comms summary frame.
//
// Wire format (positional):
//   [vesselId,
//    hasPath, bottleneckBps,
//    linkSnr, linkRateBps, linkMaxRateBps, linkSnrFloor, peerLabel,
//    txActive, txRateBps, txDeliveredBytes, txTotalBytes]
//
// `link*` fields describe the vessel's *first hop* — the link from
// this vessel to its immediate peer (KSC for direct paths, the relay
// vessel for relayed paths). For relayed paths, this is the link the
// vessel itself manages, so the dB readout and signal bars reflect
// the player-visible link quality. Computed live per frame from
// current endpoint positions so the SNR tracks distance continuously
// during time warp (Solve cadence is bucket-event-driven; per-Solve
// numbers would freeze once geometry sits in the top rate bucket).
//
// `linkSnrFloor` is the linear SNR threshold below which the link's
// quantised rate drops to zero (bucket-1 cutoff for the chosen
// antenna pair). UI displays it as the link's noise floor.
//
// `peerLabel` is "KSC" for direct paths or "KSC (via NAME)" when the
// chosen path's first hop is a relay vessel; "" when the link is DARK.
public static class CommsFormatter {
  public static void Write(StringBuilder sb,
      string vesselGuid,
      bool hasPath, double bottleneckBps,
      double linkSnr, double linkRateBps, double linkMaxRateBps,
      double linkSnrFloor, string peerLabel,
      bool txActive, double txRateBps, long txDeliveredBytes, long txTotalBytes) {
    JsonWriter.Begin(sb, '[');
    bool first = true;

    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteString(sb, vesselGuid);

    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteBoolAsBit(sb, hasPath);
    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteDouble(sb, bottleneckBps);
    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteDouble(sb, linkSnr);
    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteDouble(sb, linkRateBps);
    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteDouble(sb, linkMaxRateBps);
    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteDouble(sb, linkSnrFloor);
    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteString(sb, peerLabel ?? "");

    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteBoolAsBit(sb, txActive);
    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteDouble(sb, txRateBps);
    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteLong(sb, txDeliveredBytes);
    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteLong(sb, txTotalBytes);

    JsonWriter.End(sb, ']');
  }
}
