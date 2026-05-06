using System.Text;
using Dragonglass.Telemetry.Topics;
using UnityEngine;

namespace Nova.Telemetry;

// Singleton topic carrying KSP's timewarp state — current rate and
// mode (rails vs physics). Always-on, attached to Dragonglass's
// persistent telemetry host alongside NovaSceneTopic.
//
// UT and MET deliberately do NOT live here. UT advances every frame
// during warp; including it would push a wire frame per Update. The
// flight-HUD top bar instead derives the mission clock from the
// per-vessel NovaOrbit/<id> topic (which carries missionTime).
//
// Wire format: [rate, mode]
//   rate  — float, TimeWarp.CurrentRate (1.0 = realtime)
//   mode  — "physics" (TimeWarp.Modes.LOW) or "rails" (TimeWarp.Modes.HIGH)
public sealed class NovaTimewarpTopic : Topic {
  public override string Name => "NovaTimewarp";

  private float _cachedRate = 1f;
  private TimeWarp.Modes _cachedMode = TimeWarp.Modes.HIGH;

  protected override void OnEnable() {
    _cachedRate = TimeWarp.CurrentRate;
    _cachedMode = TimeWarp.WarpMode;
    base.OnEnable();
    MarkDirty();
  }

  private void Update() {
    var r = TimeWarp.CurrentRate;
    var m = TimeWarp.WarpMode;
    if (r == _cachedRate && m == _cachedMode) return;
    _cachedRate = r;
    _cachedMode = m;
    MarkDirty();
  }

  public override void WriteData(StringBuilder sb) {
    JsonWriter.Begin(sb, '[');
    bool first = true;
    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteDouble(sb, _cachedRate);
    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteString(sb, _cachedMode == TimeWarp.Modes.HIGH ? "rails" : "physics");
    JsonWriter.End(sb, ']');
  }
}
