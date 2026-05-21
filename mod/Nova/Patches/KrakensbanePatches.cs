using HarmonyLib;
using UnityEngine;

namespace Nova.Patches;

/// <summary>
/// Stock <c>Krakensbane.SafeToEngage</c> bails if any non-active vessel in
/// <c>FlightGlobals.VesselsLoaded</c> is landed/splashed or below
/// <c>altThreshold</c> — the conservative guard that keeps a world shift
/// from disturbing nearby physics-active craft. In stock KSP that filter
/// is harmless because distance-based unloading culls far vessels out of
/// <c>VesselsLoaded</c>; only true neighbours remain. Nova keeps every
/// vessel <c>loaded=true</c> for background simulation, so a single
/// landed craft anywhere on the planet permanently disables Krakensbane
/// once the player has visited it — the active vessel then drifts through
/// world space at orbital velocity post-warp and rendering blows out.
///
/// Packed vessels are kinematic and not part of physics; a world shift
/// can't perturb them in ways that would matter. Skipping packed vessels
/// in the inner loop restores stock-equivalent semantics for Nova's
/// always-loaded set.
/// </summary>
[HarmonyPatch]
public static class KrakensbanePatches {

  // `loadedVesselsCount` is protected — Krakensbane caches it here so its
  // own AddExcess/Zero can reuse the count after SafeToEngage returns.
  // We must keep it in sync from our prefix.
  static readonly AccessTools.FieldRef<Krakensbane, int> loadedVesselsCountRef =
      AccessTools.FieldRefAccess<Krakensbane, int>("loadedVesselsCount");

  [HarmonyPrefix]
  [HarmonyPatch(typeof(Krakensbane), nameof(Krakensbane.SafeToEngage))]
  public static bool SafeToEngage_Prefix(Krakensbane __instance, out bool safeForFloatingOrigin, ref bool __result) {
    safeForFloatingOrigin = false;
    var active = FlightGlobals.ActiveVessel;
    if (active == null) {
      __result = false;
      return false;
    }
    int loadedCount = FlightGlobals.VesselsLoaded.Count;
    loadedVesselsCountRef(__instance) = loadedCount;
    if (active.state == Vessel.State.DEAD) {
      __result = false;
      return false;
    }
    safeForFloatingOrigin = true;

    int index = loadedCount;
    while (index-- > 0) {
      var vessel = FlightGlobals.VesselsLoaded[index];
      if (vessel == active) continue;
      // Packed vessels are kinematic-only — Krakensbane skips them in
      // AddExcess/Zero anyway, and FloatingOrigin moves them coherently
      // with their reference body. No physics interaction means a shift
      // is safe regardless of their situation.
      if (vessel.packed) continue;
      if (!vessel.LandedOrSplashed && !(vessel.radarAltitude < __instance.altThreshold)) continue;
      safeForFloatingOrigin = false;
      __result = false;
      return false;
    }

    if (active.LandedOrSplashed) {
      __result = false;
      return false;
    }
    double remainingAlt = active.radarAltitude - __instance.extraAltOffsetForVel;
    double descentPerTick = Vector3d.Dot(active.velocityD, -active.upAxis) * Time.fixedDeltaTime;
    if (descentPerTick <= remainingAlt) {
      double altGate = loadedCount <= 1 ? __instance.altThresholdAlone : __instance.altThreshold;
      if (remainingAlt >= altGate) {
        __result = true;
        return false;
      }
    }
    __result = false;
    return false;
  }
}
