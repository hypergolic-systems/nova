namespace Nova;

using Dragonglass.Hud;
using UnityEngine;

// Nova opts into Dragonglass's 2× CEF supersampling. The cost (4×
// CEF GPU work, ~100 MB extra HUD texture at 1920×1080, ~400 MB at
// 4K) buys us text and SVG that survive CEF's grayscale anti-aliasing
// without thickening every glyph or restricting our font palette.
// Subpixel AA is unreachable in Unity's offscreen-then-composite
// pipeline regardless of OS — see notes in SidecarHost.SetRenderScale.
//
// Must run at Startup.Instantly: Dragonglass's SidecarBootstrap
// coroutine spawns the sidecar one frame past Instantly, and the
// render-scale flag is baked into the process spawn arguments. An
// addon firing later (MainMenu, Flight, etc.) would land the call
// after the sidecar was already running at 1× and have no effect.
[KSPAddon(KSPAddon.Startup.Instantly, true)]
public class NovaRenderScaleAddon : MonoBehaviour {
  void Awake() {
    SidecarHost.SetRenderScale(2);
    NovaLog.Log("requested 2x CEF render scale (supersampled HUD)");
  }
}
