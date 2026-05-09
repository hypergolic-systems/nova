using System.Globalization;
using System.Text;

namespace Nova.Telemetry;

// Tiny positional-array JSON writer for Nova's telemetry topics.
// Wire payloads are nested arrays of numbers, strings, and small
// fixed booleans encoded as 0/1 (mirroring the convention used by
// stock Dragonglass PartTopic for compactness). Reaches for
// StringBuilder directly — the wire spec is small and we want
// allocation-free emission inside the broadcast hot path. No null
// in the output: callers should encode "absent" as `null` literal
// only when the schema explicitly allows it (parent id of root part).
public static class JsonWriter {
  // Group helpers — writers track comma-separation themselves via the
  // `first` ref bool so callers stay readable.

  public static void Begin(StringBuilder sb, char open) {
    sb.Append(open);
  }

  public static void End(StringBuilder sb, char close) {
    sb.Append(close);
  }

  public static void Sep(StringBuilder sb, ref bool first) {
    if (first) first = false;
    else sb.Append(',');
  }

  public static void WriteString(StringBuilder sb, string value) {
    if (value == null) {
      sb.Append("null");
      return;
    }
    sb.Append('"');
    for (int i = 0; i < value.Length; i++) {
      char c = value[i];
      switch (c) {
        case '"':  sb.Append("\\\""); break;
        case '\\': sb.Append("\\\\"); break;
        case '\b': sb.Append("\\b");  break;
        case '\f': sb.Append("\\f");  break;
        case '\n': sb.Append("\\n");  break;
        case '\r': sb.Append("\\r");  break;
        case '\t': sb.Append("\\t");  break;
        default:
          if (c < 0x20) {
            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
          } else {
            sb.Append(c);
          }
          break;
      }
    }
    sb.Append('"');
  }

  public static void WriteUint(StringBuilder sb, uint value) {
    sb.Append(value.ToString(CultureInfo.InvariantCulture));
  }

  public static void WriteUintAsString(StringBuilder sb, uint value) {
    // Stock DG emits part ids as strings on the wire — JS numbers cap
    // at 2^53 and KSP persistent ids are 32-bit, but matching stock's
    // shape lets the TS decoder treat all id fields uniformly.
    sb.Append('"');
    sb.Append(value.ToString(CultureInfo.InvariantCulture));
    sb.Append('"');
  }

  public static void WriteNullableUintAsString(StringBuilder sb, uint? value) {
    if (value.HasValue) WriteUintAsString(sb, value.Value);
    else sb.Append("null");
  }

  public static void WriteDouble(StringBuilder sb, double value) {
    if (double.IsNaN(value) || double.IsInfinity(value)) {
      sb.Append("0");
      return;
    }
    sb.Append(value.ToString("G17", CultureInfo.InvariantCulture));
  }

  public static void WriteBoolAsBit(StringBuilder sb, bool value) {
    sb.Append(value ? '1' : '0');
  }
}
