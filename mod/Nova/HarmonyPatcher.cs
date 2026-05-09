using System.IO;
using System.Reflection;
using HarmonyLib;
using Nova.Patches;

namespace Nova;

/// <summary>
/// Slim Harmony entry point. The previous version applied a sprawl
/// of save/load/build patches living in <c>Nova/_legacy/Patches/</c>;
/// most of those depended on the C# simulator and need to be
/// re-thought for the Rust path. We restore them one at a time as
/// the Rust-side persistence chain ports.
///
/// Phase-1 patches a single Harmony target: <c>ProtoVessel</c>, so we
/// can capture the ConfigNode at vessel-load time and translate it
/// into the proto pair that crosses the FFI.
/// </summary>
public static class HarmonyPatcher {
  public const string HARMONY_ID = "com.nova.ksp";
  private static Harmony harmonyInstance;

  public static void Initialize() {
    NovaLog.Log("Initializing Harmony patches...");
    harmonyInstance = new Harmony(HARMONY_ID);
    harmonyInstance.PatchAll(Assembly.GetExecutingAssembly());

    // PatchAll can't reach `internal` nested types like
    // LoadGameDialog.PlayerProfileInfo via [HarmonyPatch] attributes,
    // so we apply that one manually here.
    var profileType = AccessTools.Inner(typeof(LoadGameDialog), "PlayerProfileInfo");
    if (profileType != null) {
      var target = AccessTools.Method(profileType, "AreVesselsInFlightCompatible");
      var prefix = AccessTools.Method(
          typeof(LoadGamePatches),
          nameof(LoadGamePatches.AreVesselsInFlightCompatible_Prefix));
      if (target != null && prefix != null) {
        harmonyInstance.Patch(target, prefix: new HarmonyMethod(prefix));
        NovaLog.Log("Patched: LoadGameDialog.PlayerProfileInfo.AreVesselsInFlightCompatible");
      } else {
        NovaLog.LogError($"AreVesselsInFlightCompatible patch failed to resolve (target={target}, prefix={prefix})");
      }
    } else {
      NovaLog.LogError("LoadGameDialog.PlayerProfileInfo nested type not found");
    }

    // Same situation for VesselSpawnDialog.VesselDataItem (internal nested):
    // intercept its (FileInfo, bool, bool) constructor so .nvc files don't go
    // through stock's ConfigNode.Load + CraftProfileInfo.SaveToMetaFile path
    // (which trips an NRE on shipName because the ConfigNode is empty for
    // a binary file).
    var vdiType = VesselSpawnPatches.VesselDataItemType;
    if (vdiType != null) {
      var ctor = AccessTools.Constructor(vdiType,
          new[] { typeof(FileInfo), typeof(bool), typeof(bool) });
      var prefix = AccessTools.Method(
          typeof(VesselSpawnPatches),
          nameof(VesselSpawnPatches.VesselDataItem_Ctor_Prefix));
      if (ctor != null && prefix != null) {
        harmonyInstance.Patch(ctor, prefix: new HarmonyMethod(prefix));
        NovaLog.Log("Patched: VesselSpawnDialog.VesselDataItem..ctor");
      } else {
        NovaLog.LogError($"VesselDataItem ctor patch failed to resolve (ctor={ctor}, prefix={prefix})");
      }
    } else {
      NovaLog.LogError("VesselSpawnDialog.VesselDataItem nested type not found");
    }

    var patchedMethods = harmonyInstance.GetPatchedMethods();
    int patchCount = 0;
    foreach (var method in patchedMethods) {
      patchCount++;
      NovaLog.Log($"Patched: {method.DeclaringType?.Name}.{method.Name}");
    }
    NovaLog.Log($"Harmony: {patchCount} method(s) patched.");
  }
}
