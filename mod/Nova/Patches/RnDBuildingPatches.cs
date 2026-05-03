using HarmonyLib;
using Nova.Telemetry;

namespace Nova.Patches;

// Replace the stock R&D click flow with a navigation to Nova's virtual
// "RND" scene. Patching OnClicked (rather than RDSceneSpawner.onRDSpawn)
// is the only safe hook: stock OnClicked unconditionally sets
// ControlTypes.KSC_ALL under key "RnDFacility" before firing
// onGUIRnDComplexSpawn, and only releases that lock when the despawn
// event fires. Suppressing the spawner alone would leak the lock and
// freeze the KSC. Returning false here skips the lock, the event fire,
// and the popup paths in one cut, then flips Nova's scene topic so the
// UI router navigates to the R&D view.
[HarmonyPatch(typeof(RnDBuilding), "OnClicked")]
public static class RnDBuildingPatches {

  // Wire-format scene name. Must match the string the UI router
  // checks for (Nova Hud routes "RND" → RndScene).
  private const string RndScene = "RND";

  [HarmonyPrefix]
  public static bool Prefix() {
    if (NovaSceneTopic.Instance != null) {
      NovaSceneTopic.Instance.VirtualScene = RndScene;
      NovaLog.Log("[RnD] Click intercepted; virtual scene → " + RndScene + ".");
    } else {
      NovaLog.LogWarning("[RnD] Click intercepted but NovaSceneTopic not yet attached; UI will not navigate.");
    }
    return false;
  }
}
