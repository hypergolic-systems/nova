using HarmonyLib;

namespace Nova.Patches;

/// <summary>
/// Kill stock asteroid + comet spawning entirely. Nova's rule is
/// "nothing gets spawned that the simulator doesn't drive" — until an
/// asteroid/comet pipeline exists in nova-sim, no such vessels enter
/// the world. Otherwise <see cref="Components.NovaVesselModule.OnLoadVessel"/>
/// would surface them as missing-handle errors.
///
/// We patch the two <see cref="DiscoverableObjectsUtil"/> primitives
/// (instead of every <see cref="ScenarioDiscoverableObjects"/> spawner)
/// so every path that creates an asteroid or comet — scenario polling,
/// debug menu, mission contracts, save-state restore, anything else —
/// short-circuits at the same chokepoint.
/// </summary>
[HarmonyPatch]
public static class AsteroidPatches {

  [HarmonyPrefix]
  [HarmonyPatch(typeof(DiscoverableObjectsUtil), nameof(DiscoverableObjectsUtil.SpawnAsteroid))]
  public static bool SpawnAsteroid_Prefix(ref ProtoVessel __result) {
    __result = null;
    return false;
  }

  [HarmonyPrefix]
  [HarmonyPatch(typeof(DiscoverableObjectsUtil), nameof(DiscoverableObjectsUtil.SpawnComet))]
  public static bool SpawnComet_Prefix(ref ProtoVessel __result) {
    __result = null;
    return false;
  }
}
