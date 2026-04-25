using System;
using System.Reflection;
using HarmonyLib;
using Nova.Patches;
using Nova;
namespace Nova;
public static class HarmonyPatcher {
  private static Harmony harmonyInstance;

  public const string HARMONY_ID = "com.hypergolicsystems.ksp";

  public static void Initialize() {
    NovaLog.Log("Initializing Harmony patches...");
    harmonyInstance = new Harmony(HARMONY_ID);
    harmonyInstance.PatchAll(Assembly.GetExecutingAssembly());
    LoadingProfiler.PatchAnalyticsUtil(harmonyInstance);

    // Manual patch: PlayerProfileInfo is internal, can't use [HarmonyPatch] attribute
    var profileType = AccessTools.Inner(typeof(LoadGameDialog), "PlayerProfileInfo");
    var original = AccessTools.Method(profileType, "AreVesselsInFlightCompatible");
    var prefix = AccessTools.Method(typeof(LoadGamePatches), "AreVesselsInFlightCompatible_Prefix");
    harmonyInstance.Patch(original, prefix: new HarmonyMethod(prefix));

    // Manual patch: VesselSpawnDialog.VesselDataItem is internal nested
    var vdiCtor = AccessTools.Constructor(VesselSpawnPatches.VesselDataItemType,
      new[] { typeof(System.IO.FileInfo), typeof(bool), typeof(bool) });
    var vdiPrefix = AccessTools.Method(typeof(VesselSpawnPatches), "VesselDataItem_Ctor_Prefix");
    harmonyInstance.Patch(vdiCtor, prefix: new HarmonyMethod(vdiPrefix));

    var patchedMethods = harmonyInstance.GetPatchedMethods();
    int patchCount = 0;
    foreach (var method in patchedMethods) {
      patchCount++;
      NovaLog.Log($"Patched: {method.DeclaringType?.Name}.{method.Name}");
    }
  }
}
