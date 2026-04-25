using System;
using Nova.Core.Persistence;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Nova;
using KSP.UI;
using KSP.UI.Screens;
using UnityEngine;
using UnityEngine.UI;
using Proto = Nova.Core.Persistence.Protos;

namespace Nova.Patches;

/// <summary>
/// LaunchSiteFacility.showShipSelection does its own *.craft enumeration
/// and shows a "no vessels" popup if the list is empty — before firing
/// onGUILaunchScreenSpawn. Without this swap, VesselSpawnDialog never
/// opens and our patches on it never run. The list is used only for a
/// count check; the dialog re-enumerates from CreateVesselList.
/// </summary>
[HarmonyPatch(typeof(LaunchSiteFacility))]
public static class LaunchSiteFacilityPatches {

  [HarmonyTranspiler]
  [HarmonyPatch("showShipSelection")]
  public static IEnumerable<CodeInstruction> ShowShipSelection_Transpiler(
      IEnumerable<CodeInstruction> instructions) {
    foreach (var ins in instructions) {
      if (ins.opcode == OpCodes.Ldstr && (string)ins.operand == "*.craft") {
        yield return new CodeInstruction(OpCodes.Ldstr, "*.hgc");
      } else {
        yield return ins;
      }
    }
  }
}

/// <summary>
/// Make the launch pad's VesselSpawnDialog work with .hgc files.
/// Replaces the craft list coroutine, intercepts the internal
/// VesselDataItem constructor, and skips the upgrade pipeline at launch.
/// </summary>
[HarmonyPatch(typeof(VesselSpawnDialog))]
public static class VesselSpawnPatches {

  internal static readonly Type VesselDataItemType =
    AccessTools.Inner(typeof(VesselSpawnDialog), "VesselDataItem");

  // VesselSpawnDialog instance fields/methods
  static readonly FieldInfo scrollListField =
    AccessTools.Field(typeof(VesselSpawnDialog), "scrollList");
  static readonly FieldInfo vesselDataItemListField =
    AccessTools.Field(typeof(VesselSpawnDialog), "vesselDataItemList");
  static readonly FieldInfo dlgCraftSubfolderField =
    AccessTools.Field(typeof(VesselSpawnDialog), "craftSubfolder");
  static readonly FieldInfo sortButtonField =
    AccessTools.Field(typeof(VesselSpawnDialog), "sortButton");
  static readonly FieldInfo sortAscField =
    AccessTools.Field(typeof(VesselSpawnDialog), "sortAsc");
  static readonly FieldInfo buttonLaunchField =
    AccessTools.Field(typeof(VesselSpawnDialog), "buttonLaunch");
  static readonly FieldInfo buttonDeleteField =
    AccessTools.Field(typeof(VesselSpawnDialog), "buttonDelete");
  static readonly FieldInfo buttonEditField =
    AccessTools.Field(typeof(VesselSpawnDialog), "buttonEdit");
  static readonly FieldInfo vesselDescriptionField =
    AccessTools.Field(typeof(VesselSpawnDialog), "vesselDescription");
  static readonly FieldInfo selectedDataItemField =
    AccessTools.Field(typeof(VesselSpawnDialog), "selectedDataItem");

  static readonly MethodInfo addVesselDataItemMethod =
    AccessTools.Method(typeof(VesselSpawnDialog), "AddVesselDataItem");
  static readonly MethodInfo onVesselListSortMethod =
    AccessTools.Method(typeof(VesselSpawnDialog), "OnVesselListSort");
  static readonly MethodInfo selectVesselDataItemMethod =
    AccessTools.Method(typeof(VesselSpawnDialog), "SelectVesselDataItem");
  static readonly MethodInfo getVesselDataItemByNameMethod =
    AccessTools.Method(typeof(VesselSpawnDialog), "GetVesselDataItem",
      new[] { typeof(string) });
  static readonly MethodInfo getVesselDataItemByIndexMethod =
    AccessTools.Method(typeof(VesselSpawnDialog), "GetVesselDataItem",
      new[] { typeof(int) });
  static readonly MethodInfo launchSelectedVesselMethod =
    AccessTools.Method(typeof(VesselSpawnDialog), "LaunchSelectedVessel");

  // VesselDataItem fields
  static readonly FieldInfo vdiStock = AccessTools.Field(VesselDataItemType, "stock");
  static readonly FieldInfo vdiSteamItem = AccessTools.Field(VesselDataItemType, "steamItem");
  static readonly FieldInfo vdiFullFilePath = AccessTools.Field(VesselDataItemType, "fullFilePath");
  static readonly FieldInfo vdiConfigNode = AccessTools.Field(VesselDataItemType, "_configNode");
  static readonly FieldInfo vdiName = AccessTools.Field(VesselDataItemType, "name");
  static readonly FieldInfo vdiDescription = AccessTools.Field(VesselDataItemType, "description");
  static readonly FieldInfo vdiParts = AccessTools.Field(VesselDataItemType, "parts");
  static readonly FieldInfo vdiStages = AccessTools.Field(VesselDataItemType, "stages");
  static readonly FieldInfo vdiCompatibility = AccessTools.Field(VesselDataItemType, "compatibility");
  static readonly FieldInfo vdiIsValid = AccessTools.Field(VesselDataItemType, "isValid");
  static readonly FieldInfo vdiIsExperimental = AccessTools.Field(VesselDataItemType, "isExperimental");
  static readonly FieldInfo vdiCraftProfileInfo = AccessTools.Field(VesselDataItemType, "craftProfileInfo");
  static readonly FieldInfo vdiThumbURL = AccessTools.Field(VesselDataItemType, "thumbURL");
  static readonly FieldInfo vdiThumbnail = AccessTools.Field(VesselDataItemType, "thumbnail");
  static readonly FieldInfo vdiListItem = AccessTools.Field(VesselDataItemType, "listItem");

  static readonly ConstructorInfo vdiCtor = AccessTools.Constructor(
    VesselDataItemType, new[] { typeof(FileInfo), typeof(bool), typeof(bool) });

  // scrollList is UIList<VesselSpawnDialog.ListItem>; resolve methods at runtime
  static readonly MethodInfo scrollListClearMethod =
    AccessTools.Method(scrollListField.FieldType, "Clear", new[] { typeof(bool) });

  /// <summary>
  /// Replace the craft list coroutine with one that searches *.hgc in
  /// the player save directory only. Stock and expansion craft are skipped.
  /// </summary>
  [HarmonyPrefix]
  [HarmonyPatch("CreateVesselList")]
  public static bool CreateVesselList_Prefix(VesselSpawnDialog __instance,
      string craftSubfolder, string profileName, ref IEnumerator __result) {
    __result = CreateHgcVesselList(__instance, craftSubfolder, profileName);
    return false;
  }

  /// <summary>
  /// Skip KSPUpgradePipeline for .hgc files — it runs against a ConfigNode
  /// and would try to overwrite the binary .hgc with text on save. Launch
  /// directly instead.
  /// </summary>
  [HarmonyPrefix]
  [HarmonyPatch("ConfirmLaunch")]
  public static bool ConfirmLaunch_Prefix(VesselSpawnDialog __instance) {
    var selected = selectedDataItemField.GetValue(__instance);
    if (selected == null) return true;
    var path = (string)vdiFullFilePath.GetValue(selected);
    if (path == null || !path.EndsWith(".hgc")) return true;

    var info = (CraftProfileInfo)vdiCraftProfileInfo.GetValue(selected);
    if (info != null && (!info.shipPartsUnlocked || !info.shipPartModulesAvailable))
      UnityEngine.Debug.LogWarning(
        "Loading craft with the following issues:\n" + info.GetErrorMessage());

    launchSelectedVesselMethod.Invoke(__instance, null);
    return false;
  }

  static IEnumerator CreateHgcVesselList(VesselSpawnDialog instance,
      string craftSubfolder, string profileName) {
    yield return null;

    dlgCraftSubfolderField.SetValue(instance, craftSubfolder);

    var scrollList = scrollListField.GetValue(instance);
    scrollListClearMethod.Invoke(scrollList, new object[] { true });
    var vesselDataItemList = (IList)vesselDataItemListField.GetValue(instance);
    vesselDataItemList.Clear();

    if (string.IsNullOrEmpty(profileName)) yield break;

    var dir = KSPUtil.ApplicationRootPath + "saves/" + profileName + "/Ships/" + craftSubfolder;
    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

    FileInfo[] files;
    try {
      files = new DirectoryInfo(dir).GetFiles("*.hgc");
    } catch (Exception e) {
      NovaLog.Log($"VesselSpawnDialog: failed to enumerate {dir}: {e.Message}");
      files = new FileInfo[0];
    }

    foreach (var file in files) {
      try {
        var item = vdiCtor.Invoke(new object[] { file, false, false });
        addVesselDataItemMethod.Invoke(instance, new[] { vesselDataItemList, item });
      } catch (Exception e) {
        NovaLog.Log($"VesselSpawnDialog: failed to add {file.Name}: {e.Message}");
      }
    }

    var sortButton = (int)sortButtonField.GetValue(instance);
    var sortAsc = (bool)sortAscField.GetValue(instance);
    onVesselListSortMethod.Invoke(instance, new object[] { sortButton, sortAsc });

    if (vesselDataItemList.Count > 0) {
      foreach (var item in vesselDataItemList) {
        var listItem = (UIListItem)vdiListItem.GetValue(item);
        var radio = listItem != null ? listItem.GetComponent<UIRadioButton>() : null;
        if (radio != null) radio.Interactable = true;
      }

      var autoName = EditorLogic.autoShipName;
      object selected = null;
      if (!string.IsNullOrEmpty(autoName))
        selected = getVesselDataItemByNameMethod.Invoke(instance, new object[] { autoName });
      if (selected == null)
        selected = getVesselDataItemByIndexMethod.Invoke(instance, new object[] { 0 });
      if (selected != null)
        selectVesselDataItemMethod.Invoke(instance, new[] { selected });
    } else {
      ((Button)buttonLaunchField.GetValue(instance)).interactable = false;
      ((Button)buttonDeleteField.GetValue(instance)).interactable = false;
      ((Button)buttonEditField.GetValue(instance)).interactable = false;
      ((TMPro.TextMeshProUGUI)vesselDescriptionField.GetValue(instance)).text = "";
    }
  }

  /// <summary>
  /// Manually patched on VesselDataItem's (FileInfo, bool, bool) constructor
  /// from HarmonyPatcher. Reads embedded CraftMetadata + structure from .hgc
  /// and populates the item without touching the disk via ConfigNode.Load.
  /// </summary>
  public static bool VesselDataItem_Ctor_Prefix(object __instance,
      FileInfo fInfo, bool stock, bool steamItem) {
    if (fInfo == null || fInfo.Extension != ".hgc") return true;

    Proto.CraftFile craft;
    using (var stream = fInfo.OpenRead()) {
      NovaFileFormat.ReadPrefix(stream);
      craft = ProtoBuf.Serializer.Deserialize<Proto.CraftFile>(stream);
    }

    var meta = craft.Metadata;
    var structure = craft.Vessel.Structure;

    var info = new CraftProfileInfo {
      shipName = meta.Name,
      description = meta.Description,
      partCount = meta.PartCount,
      stageCount = meta.StageCount,
      totalCost = meta.TotalCost,
      totalMass = meta.TotalMass,
      shipFacility = (EditorFacility)meta.Facility,
      compatibility = VersionCompareResult.COMPATIBLE,
      shipSize = new Vector3(meta.Size.X, meta.Size.Y, meta.Size.Z),
    };

    vdiStock.SetValue(__instance, stock);
    vdiSteamItem.SetValue(__instance, steamItem);
    vdiFullFilePath.SetValue(__instance, fInfo.FullName);
    vdiCraftProfileInfo.SetValue(__instance, info);
    vdiIsValid.SetValue(__instance, true);
    vdiIsExperimental.SetValue(__instance, false);
    vdiParts.SetValue(__instance, info.partCount);
    vdiStages.SetValue(__instance, info.stageCount);
    vdiCompatibility.SetValue(__instance, VersionCompareResult.COMPATIBLE);
    vdiName.SetValue(__instance, info.shipName);
    vdiDescription.SetValue(__instance, info.description);

    var tex = new Texture2D(2, 2);
    ImageConversion.LoadImage(tex, meta.Thumbnail);
    vdiThumbnail.SetValue(__instance, tex);

    var baseName = fInfo.Name.Replace(fInfo.Extension, "");
    var thumbURL = "thumbs/" + HighLogic.SaveFolder + "_" + fInfo.Directory.Name + "_"
                 + KSPUtil.SanitizeFilename(baseName);
    vdiThumbURL.SetValue(__instance, thumbURL);

    // Build a ConfigNode that satisfies DefaultCrewForVessel, ButtonEdit,
    // and ShipTemplate.LoadShip without round-tripping through disk.
    // VesselDataItem needs a _configNode for stock code paths that read
    // configNode.GetValue("type"), etc. Build a minimal one — this is the
    // one unavoidable ConfigNode since stock dialogs lazy-read from it.
    var node = new ConfigNode("ShipConstruct");
    node.AddValue("ship", meta.Name);
    node.AddValue("type", (EditorFacility)meta.Facility == EditorFacility.SPH ? "SPH" : "VAB");
    node.AddValue("description", meta.Description);
    node.AddValue("size", KSPUtil.WriteVector(new Vector3(meta.Size.X, meta.Size.Y, meta.Size.Z)));
    foreach (var ps in structure.Parts) {
      var partNode = node.AddNode("PART");
      partNode.AddValue("part", $"{ps.PartName}_{ps.Id}");
    }
    vdiConfigNode.SetValue(__instance, node);

    return false;
  }

  /// <summary>
  /// Populate a ShipTemplate's fields directly from an .hgc file, bypassing
  /// ShipTemplate.LoadShip(ConfigNode). Called from the launchChecks
  /// transpiler below. Only sets the fields the stock preflight tests read
  /// (shipName, shipSize, partCount, stageCount, totalMass, totalCost).
  /// </summary>
  public static void LoadShipTemplateFromHgc(ShipTemplate template, string path) {
    Proto.CraftFile craft;
    using (var stream = File.OpenRead(path)) {
      NovaFileFormat.ReadPrefix(stream);
      craft = ProtoBuf.Serializer.Deserialize<Proto.CraftFile>(stream);
    }
    var meta = craft.Metadata;
    template.filename = path;
    template.shipName = meta.Name;
    template.shipDescription = meta.Description;
    template.shipType = meta.Facility;
    template.partCount = meta.PartCount;
    template.stageCount = meta.StageCount;
    template.totalCost = meta.TotalCost;
    template.totalMass = meta.TotalMass;
    template.shipSize = new Vector3(meta.Size.X, meta.Size.Y, meta.Size.Z);
    template.shipPartsUnlocked = true;
    template.shipPartsExperimental = false;
  }
}

/// <summary>
/// LaunchSiteFacility.launchChecks builds a ShipTemplate via
/// `new ShipTemplate(); template.LoadShip(ConfigNode.Load(path));`, which
/// is useless for our binary .hgc files — the ConfigNode is empty, shipSize
/// stays zero, and CraftWithinSizeLimits fails with "no size information".
///
/// Transpile the pair of calls away: remove `ConfigNode.Load(path)` and
/// replace `ShipTemplate.LoadShip(ConfigNode)` with a call to our helper
/// that reads proto fields from the .hgc directly. The path argument
/// pushed for ConfigNode.Load stays on the stack and is consumed by our
/// helper instead.
/// </summary>
[HarmonyPatch(typeof(LaunchSiteFacility))]
public static class LaunchSiteFacilityLaunchChecksPatches {

  static readonly MethodInfo configNodeLoad =
    AccessTools.Method(typeof(ConfigNode), "Load", new[] { typeof(string) });
  static readonly MethodInfo shipTemplateLoadShip =
    AccessTools.Method(typeof(ShipTemplate), "LoadShip", new[] { typeof(ConfigNode) });
  static readonly MethodInfo hgcHelper =
    AccessTools.Method(typeof(VesselSpawnPatches), nameof(VesselSpawnPatches.LoadShipTemplateFromHgc));

  [HarmonyTranspiler]
  [HarmonyPatch("launchChecks")]
  public static IEnumerable<CodeInstruction> LaunchChecks_Transpiler(
      IEnumerable<CodeInstruction> instructions) {
    foreach (var ins in instructions) {
      if (ins.Calls(configNodeLoad)) {
        // Drop the ConfigNode.Load call; leave the path string on the stack.
        continue;
      }
      if (ins.Calls(shipTemplateLoadShip)) {
        // Replace LoadShip(ConfigNode) with our helper(ShipTemplate, string).
        yield return new CodeInstruction(OpCodes.Call, hgcHelper);
        continue;
      }
      yield return ins;
    }
  }
}
