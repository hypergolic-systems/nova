using HarmonyLib;
using Nova.Ffi;

namespace Nova.Patches;

/// <summary>
/// Force <c>Vessel.id</c> to the GUID assigned by the Rust simulator.
///
/// Stock <c>ShipConstruction.AssembleForLaunch</c>:468 unconditionally
/// runs <c>vessel.id = Guid.NewGuid()</c>. For Nova-driven launches
/// the simulator has already minted (or restored) the canonical GUID
/// at <c>nova_vessel_new</c> time and exposed it through
/// <see cref="NovaVesselHandle.Guid"/>; this postfix overwrites
/// KSP's freshly-rolled GUID so both halves agree on identity.
///
/// Without this, the UI's per-vessel topic subscriptions
/// (<c>nova/vessel-structure/{vessel.id}</c>) miss the simulator
/// entry, and the wire goes silent.
///
/// Non-Nova vessels (e.g. asteroid pseudo-vessels — though we patch
/// most of those out — or anything else KSP creates without going
/// through <c>NovaCraftLoader</c>) hit no handle here, fall through,
/// and keep KSP's <c>Guid.NewGuid()</c>.
/// </summary>
[HarmonyPatch(typeof(ShipConstruction))]
public static class VesselGuidPatch {

  // ShipConstruction.AssembleForLaunch has two overloads: a public
  // 6-arg wrapper (returns void) and the internal 13-arg worker
  // (returns Vessel — and does `vessel.id = Guid.NewGuid()` at
  // line 468). Patch the worker so `__result` is the Vessel.
  [HarmonyPostfix]
  [HarmonyPatch("AssembleForLaunch", new[] {
    typeof(ShipConstruct), typeof(string), typeof(string), typeof(string),
    typeof(Game), typeof(VesselCrewManifest), typeof(bool), typeof(bool),
    typeof(bool), typeof(bool), typeof(Orbit), typeof(bool), typeof(bool),
  })]
  public static void AssembleForLaunch_Postfix(Vessel __result) {
    if (__result == null) return;
    var addon = NovaWorldAddon.Instance;
    if (addon == null) return;
    var handle = addon.LookupHandle(__result.persistentId);
    if (handle == null) return;
    if (handle.Guid == System.Guid.Empty) return;
    if (__result.id == handle.Guid) return;
    __result.id = handle.Guid;
    NovaLog.Log($"[Nova/Telemetry] vessel.id ← {handle.Guid:D} (was KSP-assigned)");
  }
}
