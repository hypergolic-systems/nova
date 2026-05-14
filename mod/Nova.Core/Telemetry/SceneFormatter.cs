using System.Text;

namespace Nova.Core.Telemetry;

// Wire format: [virtualScene: string]
public static class SceneFormatter {
  public static void Write(StringBuilder sb, string virtualScene) {
    sb.Append('[');
    JsonWriter.WriteString(sb, virtualScene ?? "");
    sb.Append(']');
  }
}
