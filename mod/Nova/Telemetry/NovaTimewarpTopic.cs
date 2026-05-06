using System.Text;
using Dragonglass.Telemetry.Topics;
using UnityEngine;

namespace Nova.Telemetry;

// Singleton topic carrying KSP's timewarp state — target rate and
// mode (rails vs physics). Always-on, attached to Dragonglass's
// persistent telemetry host alongside NovaSceneTopic.
//
// We publish the *target* rate (from CurrentRateIndex) rather than
// KSP's animated `CurrentRate`. CurrentRate ramps up over ~1 s
// between warp levels — the visual gauge would lag the player's
// keypress by a noticeable beat. Snapping to target makes the bar
// react instantly; the UI does its own short tween on the displayed
// number for visual smoothness without holding the gauge hostage.
//
// UT and MET deliberately do NOT live here. UT advances every frame
// during warp; including it would push a wire frame per Update. The
// flight-HUD top bar derives the mission clock from the per-vessel
// NovaOrbit/<id> topic (which carries missionTime).
//
// Wire format: [rate, mode]
//   rate  — float, target rate at CurrentRateIndex (1.0 = realtime)
//   mode  — "physics" (TimeWarp.Modes.LOW) or "rails" (TimeWarp.Modes.HIGH)
public sealed class NovaTimewarpTopic : Topic {
  public override string Name => "NovaTimewarp";

  private int _cachedIndex = 0;
  private TimeWarp.Modes _cachedMode = TimeWarp.Modes.HIGH;
  private float _cachedRate = 1f;

  protected override void OnEnable() {
    Refresh();
    base.OnEnable();
    MarkDirty();
  }

  private void Update() {
    int idx = TimeWarp.CurrentRateIndex;
    var mode = TimeWarp.WarpMode;
    if (idx == _cachedIndex && mode == _cachedMode) return;
    _cachedIndex = idx;
    _cachedMode = mode;
    _cachedRate = ResolveTargetRate(mode, idx);
    MarkDirty();
  }

  private void Refresh() {
    _cachedIndex = TimeWarp.CurrentRateIndex;
    _cachedMode = TimeWarp.WarpMode;
    _cachedRate = ResolveTargetRate(_cachedMode, _cachedIndex);
  }

  // Look up the discrete rate at `index` in the appropriate table.
  // `TimeWarp.fetch` is the singleton MonoBehaviour holding the
  // configured rate arrays; safe to read once it exists. Defensive on
  // null/range so a misconfigured save can't NRE the broadcaster.
  private static float ResolveTargetRate(TimeWarp.Modes mode, int index) {
    var fetch = TimeWarp.fetch;
    if (fetch == null) return 1f;
    var table = mode == TimeWarp.Modes.HIGH ? fetch.warpRates : fetch.physicsWarpRates;
    if (table == null || table.Length == 0) return 1f;
    if (index < 0) index = 0;
    if (index >= table.Length) index = table.Length - 1;
    return table[index];
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
