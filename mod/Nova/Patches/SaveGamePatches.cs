using System.IO;
using Nova.Core.Persistence;
using HarmonyLib;
using Nova.Persistence;
using Nova;
using Proto = Nova.Core.Persistence.Protos;

namespace Nova.Patches;

[HarmonyPatch(typeof(GamePersistence))]
public static class SaveGamePatches {

  /// <summary>
  /// In-memory snapshot of the active vessel's just-launched state.
  /// Captured at PostInit (15 frames after `FlightDriver.Start`), where
  /// stock simultaneously sets `FlightDriver.PostInitState` and writes
  /// `persistent.sfs`. `RevertPatches.RevertToLaunch_Prefix` reads this
  /// to restore the launch state without a scene reload — mirroring
  /// stock's in-memory `PostInitState` so subsequent autosaves/quicksaves
  /// don't overwrite the revert source.
  /// </summary>
  public static Proto.SaveFile PostInitSnapshot;

  /// <summary>
  /// Shared write — strips invalid filename chars, builds the .nvs, writes
  /// it under saves/&lt;folder&gt;/, returns the built proto so callers
  /// (e.g. the PostInit GameBackup path) can stash it. `null` on failure.
  /// </summary>
  static Proto.SaveFile WriteNvs(string saveFileName, string saveFolder, string logTag) {
    try {
      foreach (var c in Path.GetInvalidFileNameChars())
        saveFileName = saveFileName.Replace(c, '_');

      var dir = Path.Combine(KSPUtil.ApplicationRootPath, "saves", saveFolder);
      if (!Directory.Exists(dir))
        Directory.CreateDirectory(dir);

      var save = NovaSaveBuilder.Build();
      var nvsPath = Path.Combine(dir, saveFileName + ".nvs");

      using var stream = File.Create(nvsPath);
      NovaFileFormat.WritePrefix(stream, 'S');
      ProtoBuf.Serializer.Serialize(stream, save);
      NovaLog.Log($"Saved{logTag}: {nvsPath} ({stream.Length} bytes, {save.Vessels.Count} vessels, {save.Crews.Count} crew)");
      return save;
    } catch (System.Exception e) {
      NovaLog.Log($"Failed to save .nvs: {e.Message}\n{e.StackTrace}");
      return null;
    }
  }

  [HarmonyPrefix]
  [HarmonyPatch("SaveGame", typeof(Game), typeof(string), typeof(string), typeof(SaveMode))]
  public static bool SaveGame_Prefix(
      Game game, string saveFileName, string saveFolder,
      ref string __result) {

    if (saveFileName == "persistent" && !game.Parameters.Flight.CanAutoSave) {
      __result = string.Empty;
      return false;
    }

    WriteNvs(saveFileName, saveFolder, logTag: "");
    __result = saveFileName;
    return false;
  }

  /// <summary>
  /// The GameBackup overload — called by FlightDriver after entering
  /// FLIGHT (PostInitState) and during revert-to-launch (PreLaunchState).
  /// We write `persistent.nvs` (mirrors stock's `persistent.sfs` write
  /// for crash recovery) AND stash an in-memory copy for `RevertToLaunch`
  /// to restore from — independent of subsequent persistent.nvs overwrites
  /// by autosave / scene transitions.
  /// </summary>
  [HarmonyPrefix]
  [HarmonyPatch("SaveGame", typeof(GameBackup), typeof(string), typeof(string), typeof(SaveMode))]
  public static bool SaveGame_GameBackup_Prefix(
      string saveFileName, string saveFolder, ref string __result) {
    if (saveFileName != "persistent") {
      __result = string.Empty;
      return false;
    }

    var save = WriteNvs(saveFileName, saveFolder, logTag: " (PostInit)");
    if (save != null) PostInitSnapshot = save;
    __result = saveFileName;
    return false;
  }
}
