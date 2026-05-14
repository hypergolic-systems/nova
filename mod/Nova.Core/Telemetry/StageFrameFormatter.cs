using System.Collections.Generic;
using System.Text;

namespace Nova.Core.Telemetry;

// Per-stage telemetry wire frame (Dragonglass-compatible).
//
// Wire format (mirrors stock DG `StageTopic` exactly so the UI's
// `stage` subscription is indistinguishable across mod/sim):
//   [vesselId, currentStageIdx, [
//     [stageNum, dvActual, twrActual,
//      [[kind, persistentId, iconName, [cousinsInStage]], ...]
//     ], ...
//   ]]
//
// Kind strings: "engine" | "decoupler" | "parachute" | "clamp" | "other".
// `cousinsInStage` holds persistentIds of symmetry cousins currently
// sharing the same stage as the representative; empty for singletons.
//
// `currentStageIdx` is -1 in non-flight contexts (editor, sim before
// staging fires).
public sealed class StageFrame {
  public int Stage;
  public double DeltaVActual;
  public float TwrActual;
  public List<StagePartFrame> Parts = new List<StagePartFrame>();
}

public struct StagePartFrame {
  public string Kind;
  public string PersistentId;
  public string IconName;
  public List<string> CousinsInStage;
}

public static class StageFrameFormatter {
  public const string KindEngine    = "engine";
  public const string KindDecoupler = "decoupler";
  public const string KindParachute = "parachute";
  public const string KindClamp     = "clamp";
  public const string KindOther     = "other";

  public static void Write(StringBuilder sb, string vesselId, int currentStageIdx,
      IEnumerable<StageFrame> stages) {
    sb.Append('[');
    JsonWriter.WriteString(sb, vesselId ?? "");
    sb.Append(',');
    JsonWriter.WriteLong(sb, currentStageIdx);
    sb.Append(',');
    sb.Append('[');
    bool firstStage = true;
    if (stages != null) {
      foreach (var sf in stages) {
        if (!firstStage) sb.Append(',');
        firstStage = false;
        WriteStage(sb, sf);
      }
    }
    sb.Append(']');
    sb.Append(']');
  }

  private static void WriteStage(StringBuilder sb, StageFrame sf) {
    sb.Append('[');
    JsonWriter.WriteLong(sb, sf.Stage);
    sb.Append(',');
    JsonWriter.WriteDouble(sb, sf.DeltaVActual);
    sb.Append(',');
    JsonWriter.WriteFloat(sb, sf.TwrActual);

    sb.Append(',');
    sb.Append('[');
    if (sf.Parts != null) {
      for (int j = 0; j < sf.Parts.Count; j++) {
        if (j > 0) sb.Append(',');
        var pf = sf.Parts[j];
        sb.Append('[');
        JsonWriter.WriteString(sb, pf.Kind ?? KindOther);
        sb.Append(',');
        JsonWriter.WriteString(sb, pf.PersistentId ?? "");
        sb.Append(',');
        JsonWriter.WriteString(sb, pf.IconName ?? "");
        sb.Append(',');
        sb.Append('[');
        var cousins = pf.CousinsInStage;
        if (cousins != null) {
          for (int k = 0; k < cousins.Count; k++) {
            if (k > 0) sb.Append(',');
            JsonWriter.WriteString(sb, cousins[k]);
          }
        }
        sb.Append(']');
        sb.Append(']');
      }
    }
    sb.Append(']');

    sb.Append(']');
  }
}
