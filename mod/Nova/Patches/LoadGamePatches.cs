using System;
using Nova.Core.Persistence;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using Nova.Persistence;
using Nova;
using Proto = Nova.Core.Persistence.Protos;

namespace Nova.Patches;

/// <summary>
/// Intercepts all game load paths. Disk reads only happen through TryLoadHgs
/// (called by explicit user actions: main menu load, quickload, esc menu load).
/// Scene transitions never trigger loads — state is persistent in memory.
/// </summary>
[HarmonyPatch]
public static class LoadGamePatches {

  static readonly HashSet<string> ExcludedDirs = new(StringComparer.OrdinalIgnoreCase) {
    "training", "scenarios", "missions", "test_missions", ".svn"
  };

  /// <summary>
  /// Load an .hgs save file from disk. Only called from explicit user actions.
  /// Sets PendingProto/PendingGame so Game.Load creates vessels.
  /// </summary>
  static Game TryLoadHgs(string saveFolder, string filename) {
    var dir = Path.Combine(KSPUtil.ApplicationRootPath, "saves", saveFolder);
    var hgsPath = Path.Combine(dir, filename + ".hgs");
    if (!File.Exists(hgsPath)) return null;

    Proto.SaveFile save;
    using (var stream = File.OpenRead(hgsPath)) {
      var (type, version) = NovaFileFormat.ReadPrefix(stream);
      save = ProtoBuf.Serializer.Deserialize<Proto.SaveFile>(stream);
    }

    NovaLog.Log($"[LoadGame] Loaded {hgsPath} ({save.Vessels.Count} vessels, {save.Crews.Count} crew)");

    var game = NovaSaveLoader.BuildGameFromProto(save);
    NovaSaveLoader.PendingProto = save;
    NovaSaveLoader.PendingGame = game;
    return game;
  }

  /// <summary>
  /// GamePersistence.LoadGame — returns in-memory game state.
  /// Never reads from disk. Loading from disk only happens through TryLoadHgs.
  /// </summary>
  [HarmonyPrefix]
  [HarmonyPatch(typeof(GamePersistence), "LoadGame",
    typeof(string), typeof(string), typeof(bool), typeof(bool))]
  public static bool LoadGame_Prefix(ref Game __result) {
    __result = HighLogic.CurrentGame;
    return false;
  }

  /// <summary>
  /// Replace LoadGameDialog.PersistentLoadGame — scan for .hgs files instead
  /// of .sfs. Build save metadata from proto directly. No compatibility checks,
  /// no KSPUpgradePipeline, no .loadmeta files.
  /// </summary>
  static readonly Type ProfileType = AccessTools.Inner(typeof(LoadGameDialog), "PlayerProfileInfo");

  /// <summary>
  /// Skip vessel-in-flight compatibility check — our format is always compatible.
  /// Stock calls LoadGame internally which returns null at main menu → NRE.
  /// Applied manually in HarmonyPatcher since the target is an internal nested type.
  /// </summary>
  public static bool AreVesselsInFlightCompatible_Prefix(ref bool __result) {
    __result = true;
    return false;
  }

  [HarmonyPrefix]
  [HarmonyPatch(typeof(LoadGameDialog), "PersistentLoadGame")]
  public static bool PersistentLoadGame_Prefix(LoadGameDialog __instance) {
    var saves = AccessTools.Method(typeof(List<>).MakeGenericType(ProfileType), "get_Count") != null
      ? Activator.CreateInstance(typeof(List<>).MakeGenericType(ProfileType)) as System.Collections.IList
      : null;

    var directory = (string)AccessTools.Field(typeof(LoadGameDialog), "directory").GetValue(__instance);
    var savesPath = KSPUtil.ApplicationRootPath + directory;

    if (Directory.Exists(savesPath)) {
      foreach (var subDir in new DirectoryInfo(savesPath).GetDirectories()) {
        if (ExcludedDirs.Contains(subDir.Name)) continue;

        var hgsPath = Path.Combine(subDir.FullName, "persistent.hgs");
        if (!File.Exists(hgsPath)) continue;

        var info = Activator.CreateInstance(ProfileType);
        AccessTools.Field(ProfileType, "name").SetValue(info, subDir.Name);
        try {
          using (var stream = File.OpenRead(hgsPath)) {
            NovaFileFormat.ReadPrefix(stream);
            var save = ProtoBuf.Serializer.Deserialize<Proto.SaveFile>(stream);

            AccessTools.Field(ProfileType, "gameCompatible").SetValue(info, true);
            AccessTools.Field(ProfileType, "vesselCount").SetValue(info, save.Vessels.Count);
            AccessTools.Field(ProfileType, "UT").SetValue(info, save.UniversalTime);
            if (save.Game != null)
              AccessTools.Field(ProfileType, "gameMode").SetValue(info, (Game.Modes)save.Game.Mode);
          }
          AccessTools.Field(ProfileType, "lastWriteTime")
            .SetValue(info, new FileInfo(hgsPath).LastWriteTimeUtc.Ticks);
        } catch (Exception e) {
          AccessTools.Field(ProfileType, "errorAccess").SetValue(info, true);
          AccessTools.Field(ProfileType, "errorDetails").SetValue(info, e.Message);
        }
        saves.Add(info);
      }
    }

    AccessTools.Field(typeof(LoadGameDialog), "saves").SetValue(__instance, saves);
    AccessTools.Field(typeof(LoadGameDialog), "selectedGame").SetValue(__instance, "");
    AccessTools.Field(typeof(LoadGameDialog), "selectedSave").SetValue(__instance, null);
    AccessTools.Method(typeof(LoadGameDialog), "SetHidden", new[] { typeof(bool) })
      .Invoke(__instance, new object[] { false });
    AccessTools.Method(typeof(LoadGameDialog), "CreateLoadList")
      .Invoke(__instance, null);
    return false;
  }

  /// <summary>
  /// MainMenu.OnLoadDialogFinished — user selected a save from the load dialog.
  /// Reads .hgs from disk and starts the game.
  /// </summary>
  [HarmonyPrefix]
  [HarmonyPatch(typeof(MainMenu), "OnLoadDialogFinished")]
  public static bool OnLoadDialogFinished_Prefix(string save) {
    InputLockManager.RemoveControlLock("loadGameDialog");
    if (string.IsNullOrEmpty(save)) return false;

    var game = TryLoadHgs(save, "persistent");
    if (game == null) return true;

    var savesDir = Path.Combine(KSPUtil.ApplicationRootPath, "saves", save);
    Directory.CreateDirectory(Path.Combine(savesDir, "Ships"));
    Directory.CreateDirectory(Path.Combine(savesDir, "Ships", "VAB"));
    Directory.CreateDirectory(Path.Combine(savesDir, "Ships", "SPH"));

    HighLogic.CurrentGame = game;
    GamePersistence.UpdateScenarioModules(game);
    HighLogic.SaveFolder = save;
    game.Start();
    return false;
  }

  /// <summary>
  /// KSCPauseMenu.quickLoad — user-initiated load from space center pause menu.
  /// </summary>
  [HarmonyPrefix]
  [HarmonyPatch(typeof(KSCPauseMenu), "quickLoad")]
  public static bool KSCQuickLoad_Prefix(string filename, string folder) {
    var game = TryLoadHgs(folder, filename);
    if (game == null) return true;

    // Restore saved scene
    var savedScene = NovaSaveLoader.PendingProto?.Game?.Scene ?? 0;
    if (savedScene > 0)
      game.startScene = (GameScenes)savedScene;

    GamePersistence.UpdateScenarioModules(game);
    HighLogic.CurrentGame = game;
    HighLogic.SaveFolder = folder;
    game.Start();
    return false;
  }

  /// <summary>
  /// Game.Load — completely replaced. Sets up game infrastructure (always),
  /// creates vessels only when PendingProto is set (user-initiated load).
  /// Stock Game.Load never runs.
  /// </summary>
  [HarmonyPrefix]
  [HarmonyPatch(typeof(Game), "Load")]
  public static bool Load_Prefix(Game __instance) {
    // Game infrastructure (idempotent)
    HighLogic.CurrentGame = __instance;
    ScenarioRunner.SetProtoModules(__instance.scenarios);
    __instance.CrewRoster.Init(__instance);

    // Create vessels only when explicitly requested
    var save = NovaSaveLoader.PendingProto;
    if (save != null && __instance == NovaSaveLoader.PendingGame) {
      NovaSaveLoader.PendingProto = null;
      NovaSaveLoader.PendingGame = null;
      NovaSaveLoader.LoadScene(save);
      NovaLog.Log($"[LoadGame] Created {FlightGlobals.Vessels.Count} vessels from proto");
    }

    return false;
  }
}
