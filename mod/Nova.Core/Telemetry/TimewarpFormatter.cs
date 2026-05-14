using System.Text;

namespace Nova.Core.Telemetry;

// Wire format: [rate, mode]
//   rate — float, target rate (1.0 = realtime)
//   mode — "physics" | "rails"
public static class TimewarpFormatter {
  public const string ModeRails = "rails";
  public const string ModePhysics = "physics";

  public static void Write(StringBuilder sb, double rate, string mode) {
    JsonWriter.Begin(sb, '[');
    bool first = true;
    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteDouble(sb, rate);
    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteString(sb, mode);
    JsonWriter.End(sb, ']');
  }
}
