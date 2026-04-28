using System.Collections.Generic;
using Nova.Core.Persistence;
using System.IO;
using System.Linq;
using HarmonyLib;
using Nova.Persistence;
using Nova;
using UnityEngine;
using Proto = Nova.Core.Persistence.Protos;

namespace Nova.Patches;

[HarmonyPatch(typeof(ShipConstruction))]
public static class CraftFilePatches {

  /// <summary>
  /// CheckCraftFileType normally does ConfigNode.Load(path) and reads the
  /// "type" field. Binary .hgc files return null from ConfigNode.Load, so
  /// the stock path returns EditorFacility.None and WrongVesselTypeForLaunchSite
  /// fails. Read Facility straight from the .hgc metadata instead.
  /// </summary>
  [HarmonyPrefix]
  [HarmonyPatch("CheckCraftFileType")]
  public static bool CheckCraftFileType_Prefix(string filePath, ref EditorFacility __result) {
    if (filePath == null || !filePath.EndsWith(".hgc")) return true;

    using var stream = File.OpenRead(filePath);
    NovaFileFormat.ReadPrefix(stream);
    var meta = NovaFileFormat.ReadCraftMetadata(stream);
    __result = (EditorFacility)meta.Facility;
    return false;
  }

  /// <summary>
  /// Intercept craft loading. Resolves .hgc path from whatever stock passes
  /// (usually a .craft path), loads from Nova binary, and sets ShipConfig
  /// so the editor's crew assignment and backup systems still work.
  /// </summary>
  [HarmonyPrefix]
  [HarmonyPatch("LoadShip", typeof(string))]
  public static bool LoadShip_Prefix(string filePath, ref ShipConstruct __result) {
    var hgcPath = ResolveHgcPath(filePath);
    if (hgcPath == null) return true;

    try {
      Proto.CraftFile craft;
      using (var stream = File.OpenRead(hgcPath)) {
        NovaFileFormat.ReadPrefix(stream);
        craft = ProtoBuf.Serializer.Deserialize<Proto.CraftFile>(stream);
      }

      __result = NovaCraftLoader.Load(craft);
      if (__result == null) {
        NovaLog.Log($"Nova craft load failed: {filePath}");
        return false;
      }

      ShipConstruction.ShipConfig = __result.SaveShip();
      NovaLog.Log($"Loaded craft from Nova: {hgcPath} ({__result.parts.Count} parts)");
      return false;
    } catch (System.Exception e) {
      NovaLog.Log($"Nova craft load error: {e.Message}\n{e.StackTrace}");
      return false;
    }
  }

  /// <summary>
  /// Editor "Save" button — SaveShipToPath(shipName, path).
  /// Capture thumbnail, embed in metadata, write .hgc.
  /// </summary>
  [HarmonyPrefix]
  [HarmonyPatch("SaveShipToPath", typeof(string), typeof(string))]
  public static bool SaveShipToPath_Prefix(string shipName, string path, ref string __result) {
    if (EditorLogic.fetch?.ship == null) { __result = ""; return false; }
    var sanitized = KSPUtil.SanitizeString(shipName, '_', true);
    var hgcPath = path + "/" + sanitized + ".hgc";
    var thumbName = HighLogic.SaveFolder + "_"
                  + ShipConstruction.GetShipsSubfolderFor(EditorDriver.editorFacility)
                  + "_" + sanitized;
    SaveHgsCraftWithThumbnail(hgcPath, EditorLogic.fetch.ship, "thumbs", thumbName);
    __result = path;
    return false;
  }

  /// <summary>
  /// SaveShipToPath(gameFolder, facility, localPath, shipName) — rare path.
  /// </summary>
  [HarmonyPrefix]
  [HarmonyPatch("SaveShipToPath", typeof(string), typeof(EditorFacility), typeof(string), typeof(string))]
  public static bool SaveShipToPath4_Prefix(
      string gameFolder, EditorFacility editorFacility,
      string localPath, string shipName, ref string __result) {
    if (EditorLogic.fetch?.ship == null) { __result = ""; return false; }
    var sanitized = KSPUtil.SanitizeString(shipName, '_', true);
    var dir = ShipConstruction.GetShipsPathFor(gameFolder, editorFacility) + "/" + localPath;
    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
    var thumbName = HighLogic.SaveFolder + "_"
                  + ShipConstruction.GetShipsSubfolderFor(editorFacility)
                  + "_" + sanitized;
    SaveHgsCraftWithThumbnail(dir + "/" + sanitized + ".hgc", EditorLogic.fetch.ship, "thumbs", thumbName);
    __result = dir;
    return false;
  }

  /// <summary>
  /// SaveShip(ShipConstruct, string) — used by mission launch clamp removal.
  /// </summary>
  [HarmonyPrefix]
  [HarmonyPatch("SaveShip", typeof(ShipConstruct), typeof(string))]
  public static bool SaveShip_Ship_Prefix(ShipConstruct ship, string shipFilename, ref string __result) {
    var dir = KSPUtil.ApplicationRootPath + "saves/" + HighLogic.SaveFolder
            + "/Ships/" + ShipConstruction.GetShipsSubfolderFor(EditorDriver.editorFacility);
    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
    var hgcPath = dir + "/" + shipFilename + ".hgc";
    var thumbName = HighLogic.SaveFolder + "_"
                  + ShipConstruction.GetShipsSubfolderFor(EditorDriver.editorFacility)
                  + "_" + shipFilename;
    SaveHgsCraftWithThumbnail(hgcPath, ship, "thumbs", thumbName);
    __result = hgcPath;
    return false;
  }

  /// <summary>
  /// SaveShip(string) — launch path and editor auto-save. Always
  /// re-serialize from the live ShipConstruct so editor-time mutations
  /// (e.g. Set Tank Config) flow through to the .hgc that flight will
  /// actually load. We pin craftIDs from the cached ShipConfig so the
  /// just-built crew manifest still matches the parts flight will see.
  ///
  /// The earlier bug (dab113d) was that re-serializing via BuildFromParts
  /// read live `part.craftID` values, which could diverge from ShipConfig
  /// after events like `onAboutToSaveShip` or thumbnail capture. Reading
  /// IDs straight out of ShipConfig avoids that entire class of drift.
  ///
  /// Earlier this prefix short-circuited when the .hgc already existed
  /// (assuming an explicit Save had just written it), but that lost
  /// any mutation made between the prior save/auto-save and Launch —
  /// the most common path being: place tank → right-click → Set Tank
  /// Config → Launch, which would fly with the pre-mutation loadout.
  /// </summary>
  [HarmonyPrefix]
  [HarmonyPatch("SaveShip", typeof(string))]
  public static bool SaveShip_Name_Prefix(string shipFilename, ref string __result) {
    var dir = KSPUtil.ApplicationRootPath + "saves/" + HighLogic.SaveFolder
            + "/Ships/" + ShipConstruction.GetShipsSubfolderFor(EditorDriver.editorFacility);
    var sanitized = KSPUtil.SanitizeString(shipFilename, '_', true);
    var hgcPath = dir + "/" + sanitized + ".hgc";

    var ship = EditorLogic.fetch?.ship;
    var shipConfig = ShipConstruction.ShipConfig;
    if (ship == null || shipConfig == null)
      throw new System.InvalidOperationException(
        $"SaveShip({shipFilename}): no editor ship or ShipConfig to persist");

    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

    var idMap = BuildCraftIdMap(ship, shipConfig);
    var thumbName = HighLogic.SaveFolder + "_"
                  + ShipConstruction.GetShipsSubfolderFor(EditorDriver.editorFacility)
                  + "_" + sanitized;
    SaveHgsCraftWithThumbnail(hgcPath, ship, "thumbs", thumbName,
      idSelector: p => idMap.TryGetValue(p, out var id) ? id : p.craftID);

    __result = hgcPath;
    return false;
  }

  /// <summary>
  /// Read craftIDs from ShipConfig PART nodes in order and map them onto
  /// the live ship's parts (same order — ShipConfig was built by
  /// ship.SaveShip() which iterates ship.parts sequentially). Returns an
  /// identity map for any positions that don't line up so BuildFromParts
  /// can fall back to the live value.
  /// </summary>
  static Dictionary<Part, uint> BuildCraftIdMap(ShipConstruct ship, ConfigNode shipConfig) {
    var map = new Dictionary<Part, uint>();
    var partNodes = shipConfig.GetNodes("PART");
    int n = System.Math.Min(ship.parts.Count, partNodes.Length);
    for (int i = 0; i < n; i++) {
      var partValue = partNodes[i].GetValue("part");
      if (partValue == null) continue;
      var underscore = partValue.LastIndexOf('_');
      if (underscore < 0) continue;
      if (!uint.TryParse(partValue.Substring(underscore + 1), out var id)) continue;
      map[ship.parts[i]] = id;
    }
    return map;
  }

  static string ResolveHgcPath(string filePath) {
    if (filePath.EndsWith(".hgc") && File.Exists(filePath))
      return filePath;

    if (filePath.EndsWith(".craft")) {
      var hgc = filePath.Substring(0, filePath.Length - 6) + ".hgc";
      if (File.Exists(hgc)) return hgc;
    }

    return null;
  }

  /// <summary>
  /// Capture thumbnail via stock, intercept the PNG bytes via the
  /// OnSnapshotCapture event, then write the .hgc with embedded thumbnail.
  /// </summary>
  static void SaveHgsCraftWithThumbnail(string hgcPath, ShipConstruct ship,
      string thumbFolder, string thumbName,
      NovaVesselBuilder.PartIdSelector idSelector = null) {
    byte[] thumbBytes = null;
    EventData<ShipConstruct, string, byte[]>.OnEvent handler =
      (s, p, b) => { thumbBytes = b; };

    CraftThumbnail.OnSnapshotCapture.Add(handler);
    try {
      ShipConstruction.CaptureThumbnail(ship, thumbFolder, thumbName);
    } finally {
      CraftThumbnail.OnSnapshotCapture.Remove(handler);
    }

    SaveHgsCraft(hgcPath, ship, thumbBytes, idSelector);
  }

  static void SaveHgsCraft(string hgcPath, ShipConstruct ship, byte[] thumbnail,
      NovaVesselBuilder.PartIdSelector idSelector = null) {
    try {
      var craft = BuildCraftFile(ship, thumbnail, idSelector);
      using var stream = File.Create(hgcPath);
      NovaFileFormat.WritePrefix(stream, 'C');
      ProtoBuf.Serializer.Serialize(stream, craft);
      NovaLog.Log($"Saved craft: {hgcPath} ({stream.Length} bytes, thumb={thumbnail?.Length ?? 0})");
    } catch (System.Exception e) {
      NovaLog.Log($"Failed to save craft: {e.Message}\n{e.StackTrace}");
    }
  }

  static Proto.CraftFile BuildCraftFile(ShipConstruct ship, byte[] thumbnail,
      NovaVesselBuilder.PartIdSelector idSelector = null) {
    var (structure, state) = NovaVesselBuilder.BuildFromParts(ship.parts, idSelector);
    var rot = ship.rotation;
    var size = ShipConstruction.CalculateCraftSize(ship);

    var metadata = new Proto.CraftMetadata {
      Name = ship.shipName,
      Description = ship.shipDescription,
      Facility = (int)ship.shipFacility,
      PartCount = ship.parts.Count,
      StageCount = ship.parts.Select(p => p.inverseStage).Distinct().Count(),
      TotalCost = ship.parts.Sum(p => p.partInfo.cost + p.GetModuleCosts(p.partInfo.cost)),
      TotalMass = ship.GetTotalMass(),
      VesselType = (int)(VesselType)AccessTools.Field(typeof(ShipConstruct), "vesselType").GetValue(ship),
      Thumbnail = thumbnail,
      Size = new Proto.Vec3 { X = size.x, Y = size.y, Z = size.z },
    };

    var rootPos = ship.parts[0].transform.position;
    return new Proto.CraftFile {
      Metadata = metadata,
      Vessel = new Proto.Vessel { Structure = structure, State = state },
      Rotation = new Proto.Quat { X = rot.x, Y = rot.y, Z = rot.z, W = rot.w },
      EditorPosition = new Proto.Vec3 { X = rootPos.x, Y = rootPos.y, Z = rootPos.z },
    };
  }
}
