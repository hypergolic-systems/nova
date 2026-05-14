using System.Text;

namespace Nova.Core.Telemetry;

// Per-vessel comms summary frame.
//
// Wire format (positional):
//   [vesselId,
//    hasPath, bottleneckBps,
//    directSnr, directRateBps, directMaxRateBps, directSnrFloor, peerLabel,
//    txActive, txRateBps, txDeliveredBytes, txTotalBytes]
//
// `directSnrFloor` is the linear SNR threshold below which the direct
// edge drops to zero rate (bucket-1 cutoff for the chosen antenna
// pair). UI displays it as the link's noise floor.
//
// `peerLabel` is "KSC" for direct paths or "KSC (via NAME)" when the
// chosen path's first hop is a relay vessel; "" when the link is DARK.
public static class CommsFormatter {
  public static void Write(StringBuilder sb,
      string vesselGuid,
      bool hasPath, double bottleneckBps,
      double directSnr, double directRateBps, double directMaxRateBps,
      double directSnrFloor, string peerLabel,
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
    JsonWriter.WriteDouble(sb, directSnr);
    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteDouble(sb, directRateBps);
    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteDouble(sb, directMaxRateBps);
    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteDouble(sb, directSnrFloor);
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
