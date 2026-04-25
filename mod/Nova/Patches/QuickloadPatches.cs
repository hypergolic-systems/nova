using HarmonyLib;
using Nova.Persistence;
namespace Nova.Patches;

[HarmonyPatch(typeof(QuickSaveLoad))]
public static class QuickloadPatches {

  [HarmonyPrefix]
  [HarmonyPatch("quickLoad")]
  public static bool quickLoad_Prefix(string filename, string folder) {
    return !NovaSaveLoader.TryQuickload(filename, folder);
  }
}
