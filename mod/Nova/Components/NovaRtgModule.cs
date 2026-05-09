using System.Linq;
using System.Reflection;
using UnityEngine;
using Nova.Core.Components.Electrical;

namespace Nova.Components;

// PB-NUK part module. Drives the part's stock thermal-emission glow
// from Nova's heat buffer state — we do NOT write to part.skinTemperature
// (the stock thermal system stays untouched on Nova-thermal parts).
//
// Stock pipeline: Part.Update fires temperatureRenderer.Update with a
// color derived from skinTemperature each frame. Since Nova doesn't
// touch skinTemperature, that stock-driven color is effectively
// transparent. Our LateUpdate runs after, overwriting with a color
// derived from rtg.CurrentTempC. MaterialColorUpdater short-circuits
// when the color hasn't changed, so the per-frame cost is just the
// gradient evaluation when temperature actually shifts.
//
// `Part.temperatureRenderer` is private; we reach for it via reflection
// (the Nova convention — KSP 1.x is frozen, names won't change).
public class NovaRtgModule : NovaPartModule {

  // Glow is a UX-feedback signal — "this part is unreasonably hot" —
  // not a physically-accurate Stefan-Boltzmann curve. Skip stock's
  // `GetBlackBodyRadiation` entirely (its alpha is multiplied through
  // `BlackBodyRadiationAlphaMult = 0.75` × per-part mult and yields
  // only ~10 % opacity even at 1700 K, way too subtle for our needs).
  // Build our own color ramp directly: dark-red at the threshold,
  // bright-orange at max operating temp, fully opaque at saturation.
  private const float GlowStartTempC = 200f;
  private static readonly Color GlowStartColor = new Color(0.6f, 0f,   0f, 0f);
  private static readonly Color GlowMaxColor   = new Color(1f,   0.4f, 0f, 1f);

  private static readonly FieldInfo TemperatureRendererField =
    typeof(Part).GetField("temperatureRenderer",
      BindingFlags.NonPublic | BindingFlags.Instance);

  private Rtg rtg;

  public override void OnStart(StartState state) {
    base.OnStart(state);
    rtg = Components.OfType<Rtg>().FirstOrDefault();
  }

  public void LateUpdate() {
    if (rtg == null) return;
    if (!HighLogic.LoadedSceneIsFlight) return;

    // Stock initializes Part.temperatureRenderer in a coroutine; if
    // we run before it lands, the field is null. ResetMPB() is the
    // public, idempotent way to force-create the MaterialColorUpdater
    // without touching anything else.
    var renderer = TemperatureRendererField?.GetValue(part) as MaterialColorUpdater;
    if (renderer == null) {
      part.ResetMPB();
      renderer = TemperatureRendererField?.GetValue(part) as MaterialColorUpdater;
      if (renderer == null) return;
    }

    float currentTempC = (float)rtg.CurrentTempC;
    float maxTempC = (float)rtg.MaxOperatingTempC;
    float t = Mathf.Clamp01((currentTempC - GlowStartTempC)
                          / Mathf.Max(1f, maxTempC - GlowStartTempC));
    var color = Color.Lerp(GlowStartColor, GlowMaxColor, t);
    renderer.Update(color);
  }
}
