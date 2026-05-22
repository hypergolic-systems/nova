namespace Nova;

using Dragonglass.Telemetry.Topics;
using UnityEngine;

// Registers Nova's per-scene UI modules into Dragonglass's
// SceneMapTopic. Dragonglass's hud shell (the default
// @dragonglass/hud entry installed alone) consumes the topic on every
// frame: when KSP enters a scene listed here, the shell dynamic-imports
// the registered specifier and mounts its default export inside the
// shared <DragonglassRoot>. Exits unmount it. Nova ships one module
// per scene (`flight.ts`, `editor.ts`, `rnd.ts`) so the splits land on
// natural lifecycle boundaries — no in-Nova scene router needed.
//
// Pre-f1339cc1 we took over the whole entry via SidecarHost.OverrideEntry
// because DG's shell *was* the stock flight HUD. After f1339cc1 made
// DG pure infrastructure, the SceneMapTopic plug-in is the right path —
// matches what kad's KadUiOverrideAddon does.
//
// MainMenu startup (not Instantly): SceneMapTopic.Instance is populated
// during the telemetry addon's OnEnable, which fires later than
// Startup.Instantly Awake. MainMenu is the first scene that runs *after*
// the topic exists, and the override persists across scene changes —
// one call covers every subsequent FLIGHT/EDITOR/RND entry.
[KSPAddon(KSPAddon.Startup.MainMenu, true)]
public class NovaUiOverrideAddon : MonoBehaviour {
  void Awake() {
    var topic = SceneMapTopic.Instance;
    if (topic == null) {
      NovaLog.LogWarning("SceneMapTopic.Instance is null — is Dragonglass.Telemetry loaded?");
      return;
    }
    // KSP scene IDs match GameScenes enum names (uppercase).
    topic.SetOverride("MAINMENU", "@nova/mainmenu");
    topic.SetOverride("FLIGHT", "@nova/flight");
    topic.SetOverride("EDITOR", "@nova/editor");
    // RND is a Nova virtual scene id (not a KSP GameScenes value);
    // RnDBuilding.OnClicked patches publish to VirtualSceneTopic so the
    // hud shell's `virtual ?? ksp` precedence flips routing here.
    topic.SetOverride("RND", "@nova/rnd");
    NovaLog.Log("UI scene overrides registered: MAINMENU/FLIGHT/EDITOR/RND → @nova/{mainmenu,flight,editor,rnd}");
  }
}
