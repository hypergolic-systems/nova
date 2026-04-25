namespace Nova;

using UnityEngine;
using Dragonglass.Hud;

// Tells Dragonglass's CEF sidecar to import @nova/hud as the UI
// entry point instead of the bundled @dragonglass/stock. Runs at
// Startup.Instantly so the override is in place before Dragonglass's
// SidecarBootstrap coroutine resumes (one frame later) and spawns
// the sidecar with --entry=. Dragonglass's importmap auto-discovers
// GameData/Nova/UI/hud.js — no path computation needed here.
[KSPAddon(KSPAddon.Startup.Instantly, true)]
public class NovaUiOverrideAddon : MonoBehaviour {
  void Awake() {
    SidecarHost.OverrideEntry("@nova/hud");
    NovaLog.Log("UI entry override registered: @nova/hud");
  }
}
