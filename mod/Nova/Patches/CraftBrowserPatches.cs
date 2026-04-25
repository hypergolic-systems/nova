using System.Collections.Generic;
using Nova.Core.Persistence;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Nova;
using KSP.UI.Screens;
using UnityEngine;
using UnityEngine.UI;
using Proto = Nova.Core.Persistence.Protos;

namespace Nova.Patches;

/// <summary>
/// Make the craft browser work with .hgc files. Metadata and thumbnails
/// are read from the embedded CraftMetadata (protobuf field 1) without
/// parsing the full vessel payload.
/// Stock craft are excluded entirely.
/// </summary>
[HarmonyPatch(typeof(CraftBrowserDialog))]
public static class CraftBrowserPatches {

  static readonly FieldInfo directoryControllerField =
    AccessTools.Field(typeof(CraftBrowserDialog), "directoryController");
  static readonly FieldInfo facilityField =
    AccessTools.Field(typeof(CraftBrowserDialog), "facility");
  static readonly FieldInfo nonGameSaveDirsField =
    AccessTools.Field(typeof(CraftBrowserDialog), "nonGameSaveDirectories");

  [HarmonyPrefix]
  [HarmonyPatch("GetPlayerCraftFiles")]
  public static bool GetPlayerCraftFiles_Prefix(
      CraftBrowserDialog __instance, ref FileInfo[] __result) {
    bool isSub = __instance.IsSubdirectorySearch;
    bool isAll = __instance.IsAllGameSearch;
    try {
      if (isAll) {
        var opt = isSub ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        __result = GetAllGameHgcFiles(__instance, opt);
        return false;
      }
      var dc = (DirectoryController)directoryControllerField.GetValue(__instance);
      var dir = new DirectoryInfo(dc.CurrentSelectedDirectoryPath);
      if (!dir.Exists) dir.Create();
      var search = isSub ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
      __result = dir.GetFiles("*.hgc", search);
    } catch {
      __result = new FileInfo[0];
    }
    return false;
  }

  [HarmonyPrefix]
  [HarmonyPatch("GetMissionCraftFiles")]
  public static bool GetMissionCraftFiles_Prefix(ref FileInfo[] __result) {
    __result = new FileInfo[0];
    return false;
  }

  [HarmonyPrefix]
  [HarmonyPatch("BuildStockCraftList")]
  public static bool BuildStockCraftList_Prefix() => false;

  /// <summary>
  /// Stock pipeSelectedItem runs KSPUpgradePipeline.Process on the craft's
  /// ConfigNode, which is null for .hgc files. Skip the pipeline and call
  /// the file-selected callback directly.
  /// </summary>
  /// <summary>
  /// Stock pipeSelectedItem runs KSPUpgradePipeline.Process on the craft's
  /// ConfigNode, which is null for .hgc files. Skip the pipeline and use
  /// the file-path load path instead (EditorLogic registers with
  /// OnConfigNodeSelected, not OnFileSelected, so we call LoadShipFromFile).
  /// </summary>
  [HarmonyPrefix]
  [HarmonyPatch("pipeSelectedItem")]
  public static bool PipeSelectedItem_Prefix(CraftBrowserDialog __instance,
      CraftEntry sItem, CraftBrowserDialog.LoadType loadType) {
    if (!sItem.fullFilePath.EndsWith(".hgc")) return true;

    AccessTools.Method(typeof(CraftBrowserDialog), "HideForLater")
      .Invoke(__instance, null);
    EditorLogic.LoadShipFromFile(sItem.fullFilePath);
    return false;
  }

  static FileInfo[] GetAllGameHgcFiles(CraftBrowserDialog instance, SearchOption searchOption) {
    var fac = (EditorFacility)facilityField.GetValue(instance);
    var nonGameDirs = (string[])nonGameSaveDirsField.GetValue(instance);

    var dir = new DirectoryInfo(ShipConstruction.GetShipsPathFor(HighLogic.SaveFolder));
    var parent = dir?.Parent;
    if (parent == null) return new FileInfo[0];

    var list = new List<FileInfo>();
    foreach (var sub in parent.GetDirectories()) {
      if (System.Array.IndexOf(nonGameDirs, sub.Name) >= 0) continue;
      var shipsDir = new DirectoryInfo(ShipConstruction.GetShipsPathFor(sub.Name, fac));
      if (!shipsDir.Exists) continue;
      list.AddRange(shipsDir.GetFiles("*.hgc", searchOption));
    }
    return list.ToArray();
  }
}

/// <summary>
/// Prevent stock and steam directory entries from being created in the
/// left-hand directory tree. Only player save directories remain.
/// </summary>
[HarmonyPatch(typeof(DirectoryController))]
public static class DirectoryControllerPatches {

  [HarmonyPrefix]
  [HarmonyPatch("BuildStockDirectoryUI")]
  public static bool BuildStockDirectoryUI_Prefix() => false;

  [HarmonyPrefix]
  [HarmonyPatch("BuildSteamDirectoryUI")]
  public static bool BuildSteamDirectoryUI_Prefix() => false;

  /// <summary>
  /// BuildGameNameDirectories skips any save folder that lacks persistent.sfs.
  /// Nova saves only write persistent.hgs, so without this rewrite the player's
  /// save never appears in the dialog's directory tree and no craft files are
  /// listed. Replace the string literal in IL.
  /// </summary>
  [HarmonyTranspiler]
  [HarmonyPatch("BuildGameNameDirectories")]
  public static IEnumerable<CodeInstruction> BuildGameNameDirectories_Transpiler(
      IEnumerable<CodeInstruction> instructions) {
    foreach (var ins in instructions) {
      if (ins.opcode == OpCodes.Ldstr && (string)ins.operand == "persistent.sfs") {
        yield return new CodeInstruction(OpCodes.Ldstr, "persistent.hgs");
      } else {
        yield return ins;
      }
    }
  }
}

/// <summary>
/// Patch DirectoryActionGroup to count .hgc files instead of .craft.
/// </summary>
[HarmonyPatch(typeof(DirectoryActionGroup))]
public static class DirectoryActionGroupPatches {

  static readonly FieldInfo directoryHeaderField =
    AccessTools.Field(typeof(DirectoryActionGroup), "directoryHeader");

  [HarmonyPrefix]
  [HarmonyPatch("UpdateCraftFileCount")]
  public static bool UpdateCraftFileCount_Prefix(DirectoryActionGroup __instance) {
    string name = __instance.gameObject.name;
    int count = 0;
    try {
      var dir = new DirectoryInfo(__instance.Path);
      if (dir.Exists) count = dir.GetFiles("*.hgc").Length;
    } catch { }
    var header = (TMPro.TextMeshProUGUI)directoryHeaderField.GetValue(__instance);
    header.text = $"{name} ({count})";
    return false;
  }
}

/// <summary>
/// Patch CraftEntry.Init to read metadata from embedded CraftMetadata
/// in the .hgc file instead of .loadmeta + separate thumbnail.
/// </summary>
[HarmonyPatch(typeof(CraftEntry))]
public static class CraftEntryPatches {

  static readonly MethodInfo uiUpdateMethod =
    AccessTools.Method(typeof(CraftEntry), "UIUpdate", new[] { typeof(SteamCraftInfo) });
  static readonly MethodInfo showPathMethod =
    AccessTools.Method(typeof(CraftEntry), "ShowPath", new[] { typeof(bool) });
  static readonly MethodInfo onValueChangedMethod =
    AccessTools.Method(typeof(CraftEntry), "onValueChanged");

  /// <summary>
  /// Prefix on Init(FileInfo, bool, bool, SteamCraftInfo) — the 4-arg overload
  /// called by Create(). For .hgc files, read embedded metadata and thumbnail,
  /// then set up the UI the same way stock Init does.
  /// </summary>
  [HarmonyPrefix]
  [HarmonyPatch("Init", typeof(FileInfo), typeof(bool), typeof(bool), typeof(SteamCraftInfo))]
  public static bool Init_Prefix(CraftEntry __instance,
      FileInfo fInfo, bool stock, bool steamItem, SteamCraftInfo steamCraftInfo) {
    if (fInfo == null || fInfo.Extension != ".hgc") return true;

    __instance.craftName = fInfo.Name.Replace(fInfo.Extension, "");
    __instance.fullFilePath = fInfo.FullName;

    Proto.CraftMetadata meta = null;
    try {
      using (var stream = fInfo.OpenRead()) {
        var (type, version) = NovaFileFormat.ReadPrefix(stream);
        if (type == 'C')
          meta = NovaFileFormat.ReadCraftMetadata(stream);
      }
    } catch (System.Exception e) {
      NovaLog.Log($"Failed to read craft metadata: {fInfo.FullName}: {e.Message}");
    }

    // Populate CraftProfileInfo from embedded metadata
    var info = new CraftProfileInfo();
    if (meta != null) {
      info.shipName = meta.Name ?? __instance.craftName;
      info.description = meta.Description ?? "";
      info.partCount = meta.PartCount;
      info.stageCount = meta.StageCount;
      info.totalCost = meta.TotalCost;
      info.totalMass = meta.TotalMass;
      info.shipFacility = (EditorFacility)meta.Facility;
      info.compatibility = VersionCompareResult.COMPATIBLE;
    }
    __instance.craftProfileInfo = info;

    // Load thumbnail from embedded PNG bytes
    Texture2D tex = null;
    if (meta?.Thumbnail != null && meta.Thumbnail.Length > 0) {
      tex = new Texture2D(2, 2);
      ImageConversion.LoadImage(tex, meta.Thumbnail);
    }
    AccessTools.Field(typeof(CraftEntry), "thumbnail").SetValue(__instance, tex);
    var thumbImg = (RawImage)AccessTools.Field(typeof(CraftEntry), "craftThumbImg").GetValue(__instance);
    if (thumbImg != null)
      thumbImg.texture = tex;

    // Replicate the tail of stock Init: set instance fields, wire toggle, update UI
    __instance.isValid = true;
    __instance.partCount = info.partCount;
    __instance.stageCount = info.stageCount;
    __instance.compatibility = info.compatibility;

    var toggle = __instance.Toggle;
    if (toggle != null) {
      toggle.interactable = true;
      var entry = __instance; // capture for lambda
      toggle.onValueChanged.AddListener((bool st) => {
        if (st) onValueChangedMethod.Invoke(entry, new object[] { st });
      });
    }

    showPathMethod.Invoke(__instance, new object[] { false });
    uiUpdateMethod.Invoke(__instance, new object[] { null });

    return false;
  }
}
