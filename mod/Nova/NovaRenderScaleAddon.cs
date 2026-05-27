namespace Nova;

using Dragonglass.Hud;
using UnityEngine;

// Nova opts into Dragonglass's 1.5× CEF supersampling — a sweet-spot
// middle ground. Cost is ~2.25× the 1× baseline (vs 4× for full 2×
// SS) but buys roughly 75% of the glyph-crispness win: enough to
// rescue text and SVG from CEF's grayscale-AA softness without the
// 4× HUD-texture memory hit of integer-2× supersampling. Subpixel
// AA is unreachable in Unity's offscreen-then-composite pipeline
// regardless of OS — see notes in SidecarHost.SetRenderScale.
//
// Trade-off: non-integer downsample has minor bilinear-phase
// artifacts on hairline geometry (1px borders can shimmer faintly),
// but is well-behaved at this ratio. Apple ships the same pattern
// for macOS "More Space" Retina mode.
//
// Must run at Startup.Instantly: Dragonglass's SidecarBootstrap
// coroutine spawns the sidecar one frame past Instantly, and the
// render-scale flag is baked into the process spawn arguments. An
// addon firing later (MainMenu, Flight, etc.) would land the call
// after the sidecar was already running at 1× and have no effect.
[KSPAddon(KSPAddon.Startup.Instantly, true)]
public class NovaRenderScaleAddon : MonoBehaviour {
  void Awake() {
    SidecarHost.SetRenderScale(1.5f);
    NovaLog.Log("requested 1.5x CEF render scale (supersampled HUD)");
  }
}
