using System.Collections.Generic;
using System.Text;

namespace Nova.Core.Telemetry;

// Engine-map wire frame (Dragonglass-compatible).
//
// Wire format (mirrors stock DG `EngineTopic` exactly so the UI's
// `engines` subscription is indistinguishable across mod/sim):
//   [vesselId, [
//     [id, mapX, mapY, status, throttle, maxThrust, isp,
//      [crossfeedPartId, ...],
//      [[propName, propAbbr, amount, capacity], ...]
//     ], ...
//   ]]
//
// Status byte:
//   0 = burning, 1 = flameout, 2 = failed, 3 = shutdown, 4 = idle.
//
// Coordinate convention: each engine's (x, z) projected into the
// vessel body frame — the bottom-up orthographic "engine map" view.
// Both mod and sim populate `MapX` / `MapY` from the same source
// (mod via `ReferenceTransform.InverseTransformDirection`, sim via
// part positions in the VirtualVessel's local frame).
public sealed class EngineFrame {
  public string Id;
  public float MapX, MapY;
  public byte Status;
  public float Throttle;
  public float MaxThrust;
  public float Isp;
  public List<string> CrossfeedPartIds = new List<string>();
  public List<EnginePropellantFrame> Propellants = new List<EnginePropellantFrame>();
}

public struct EnginePropellantFrame {
  public string Name;
  public string Abbreviation;
  public double Amount;
  public double Capacity;
}

public static class EngineFrameFormatter {
  public static void Write(StringBuilder sb, string vesselId, IEnumerable<EngineFrame> engines) {
    sb.Append('[');
    JsonWriter.WriteString(sb, vesselId ?? "");
    sb.Append(',');
    sb.Append('[');
    bool first = true;
    if (engines != null) {
      foreach (var e in engines) {
        if (!first) sb.Append(',');
        first = false;
        WriteEngine(sb, e);
      }
    }
    sb.Append(']');
    sb.Append(']');
  }

  private static void WriteEngine(StringBuilder sb, EngineFrame e) {
    sb.Append('[');
    JsonWriter.WriteString(sb, e.Id ?? "");
    sb.Append(',');
    JsonWriter.WriteFloat(sb, e.MapX);
    sb.Append(',');
    JsonWriter.WriteFloat(sb, e.MapY);
    sb.Append(',');
    sb.Append(e.Status);
    sb.Append(',');
    JsonWriter.WriteFloat(sb, e.Throttle);
    sb.Append(',');
    JsonWriter.WriteFloat(sb, e.MaxThrust);
    sb.Append(',');
    JsonWriter.WriteFloat(sb, e.Isp);

    sb.Append(',');
    sb.Append('[');
    if (e.CrossfeedPartIds != null) {
      for (int j = 0; j < e.CrossfeedPartIds.Count; j++) {
        if (j > 0) sb.Append(',');
        JsonWriter.WriteString(sb, e.CrossfeedPartIds[j]);
      }
    }
    sb.Append(']');

    sb.Append(',');
    sb.Append('[');
    if (e.Propellants != null) {
      for (int j = 0; j < e.Propellants.Count; j++) {
        if (j > 0) sb.Append(',');
        var pf = e.Propellants[j];
        sb.Append('[');
        JsonWriter.WriteString(sb, pf.Name ?? "");
        sb.Append(',');
        JsonWriter.WriteString(sb, pf.Abbreviation ?? "");
        sb.Append(',');
        JsonWriter.WriteDouble(sb, pf.Amount);
        sb.Append(',');
        JsonWriter.WriteDouble(sb, pf.Capacity);
        sb.Append(']');
      }
    }
    sb.Append(']');

    sb.Append(']');
  }
}
