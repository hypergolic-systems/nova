using HarmonyLib;

namespace Nova.Patches;

/// <summary>
/// Disable stock asteroid/comet spawning. Will be replaced by Nova system later.
/// </summary>
[HarmonyPatch(typeof(ScenarioDiscoverableObjects))]
public static class AsteroidPatches {

  [HarmonyPrefix]
  [HarmonyPatch("SpawnAsteroid")]
  public static bool SpawnAsteroid_Prefix() => false;

  [HarmonyPrefix]
  [HarmonyPatch("SpawnHomeAsteroid")]
  public static bool SpawnHomeAsteroid_Prefix() => false;

  [HarmonyPrefix]
  [HarmonyPatch("SpawnDresAsteroid")]
  public static bool SpawnDresAsteroid_Prefix() => false;

  [HarmonyPrefix]
  [HarmonyPatch("SpawnComet", new System.Type[0])]
  public static bool SpawnComet_Prefix() => false;
}
