using UnityEngine;

namespace Nova;

/// <summary>
/// Minimal mod entry point. The previous version owned a HarmonyPatcher
/// + Or-Tools test harness + Registry walk over VirtualComponent
/// subclasses. All of that lived alongside the C# simulator that's
/// now in `_legacy/`. The Rust simulator (nova-sim, via nova-ksp) is
/// the source of truth; the Nova KSP shim is reduced to:
///
///   - <see cref="NovaWorldAddon"/> — owns the Rust world handle
///     (separate KSPAddon, lifetime = whole game session).
///   - <see cref="NovaUiOverrideAddon"/> — Dragonglass entry override.
///   - <see cref="Components.NovaVesselModule"/> — per-vessel handle
///     holder. Builds proto bytes at load time and feeds them to Rust.
///   - <see cref="Components.NovaBatteryModule"/> /
///     <see cref="Components.NovaCommandModule"/> — KSP attachment
///     points; read state from the FFI handle.
///
/// This class no longer does anything itself. Kept as a logging
/// pulse so we can see in the KSP log that Nova has loaded.
/// </summary>
[KSPAddon(KSPAddon.Startup.Instantly, true)]
public class NovaMod : MonoBehaviour {
  void Awake() {
    DontDestroyOnLoad(gameObject);
    NovaLog.Log("Nova online (Rust simulator).");
    HarmonyPatcher.Initialize();
  }
}
