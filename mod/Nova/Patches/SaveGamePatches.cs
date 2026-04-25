using System.IO;
using Nova.Core.Persistence;
using HarmonyLib;
using Nova.Persistence;
using Nova;
using Proto = Nova.Core.Persistence.Protos;

namespace Nova.Patches;

[HarmonyPatch(typeof(GamePersistence))]
public static class SaveGamePatches {

  [HarmonyPrefix]
  [HarmonyPatch("SaveGame", typeof(Game), typeof(string), typeof(string), typeof(SaveMode))]
  public static bool SaveGame_Prefix(
      Game game, string saveFileName, string saveFolder,
      ref string __result) {

    if (saveFileName == "persistent" && !game.Parameters.Flight.CanAutoSave) {
      __result = string.Empty;
      return false;
    }

    try {
      foreach (var c in Path.GetInvalidFileNameChars())
        saveFileName = saveFileName.Replace(c, '_');

      var dir = Path.Combine(KSPUtil.ApplicationRootPath, "saves", saveFolder);
      if (!Directory.Exists(dir))
        Directory.CreateDirectory(dir);

      var save = NovaSaveBuilder.Build();
      var hgsPath = Path.Combine(dir, saveFileName + ".hgs");

      using var stream = File.Create(hgsPath);
      NovaFileFormat.WritePrefix(stream, 'S');
      ProtoBuf.Serializer.Serialize(stream, save);
      NovaLog.Log($"Saved: {hgsPath} ({stream.Length} bytes, {save.Vessels.Count} vessels, {save.Crews.Count} crew)");
    } catch (System.Exception e) {
      NovaLog.Log($"Failed to save .hgs: {e.Message}\n{e.StackTrace}");
    }

    __result = saveFileName;
    return false;
  }

  /// <summary>
  /// Block the GameBackup overload — called by FlightDriver after entering
  /// FLIGHT (PostInitState) and during revert-to-launch (PreLaunchState).
  /// Without this, stock writes persistent.sfs via the unpatched overload.
  /// </summary>
  [HarmonyPrefix]
  [HarmonyPatch("SaveGame", typeof(GameBackup), typeof(string), typeof(string), typeof(SaveMode))]
  public static bool SaveGame_GameBackup_Prefix(ref string __result) {
    __result = string.Empty;
    return false;
  }
}
