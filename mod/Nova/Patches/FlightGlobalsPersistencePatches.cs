using HarmonyLib;
using Nova;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Nova.Patches;

/// <summary>
/// Makes FlightGlobals and all Vessel GameObjects persist across scene
/// transitions via DontDestroyOnLoad. Stock KSP destroys everything on
/// scene change and recreates from disk — we keep it all alive instead.
/// </summary>
[HarmonyPatch]
public static class FlightGlobalsPersistencePatches {

  static FlightGlobals persistentInstance;
  static bool sceneEventHooked;

  /// <summary>
  /// Pack all vessels before leaving the FLIGHT scene. Vessels normally get
  /// destroyed on scene change, so stock doesn't bother packing them.
  /// Since ours survive, they need to be on rails or they'll have active
  /// physics in scenes that don't expect it.
  /// </summary>
  static void OnLeavingFlight() {
    if (FlightGlobals.Vessels == null) return;

    foreach (var vessel in FlightGlobals.Vessels) {
      if (vessel == null) continue;
      if (!vessel.packed)
        vessel.GoOnRails();
      // Tear down FLIGHT-only MonoBehaviours that stock would normally
      // destroy when the active vessel goes inactive. Without this they
      // keep ticking on the persisted vessel.gameObject in non-FLIGHT
      // scenes (OrbitTargeter.LateUpdate spams NREs every frame).
      // We bypass Vessel.DetachPatchedConicsSolver because it touches
      // orbitRenderer unconditionally, and OrbitRenderer self-destroys on
      // every scene change (OrbitRenderer.OnSceneChange) — so by the time
      // OnSceneLoaded fires here, orbitRenderer is already null.
      DetachFlightOnlyComponents(vessel);
      // Keep loaded=true — parts stay alive. Setting loaded=false would
      // cause Vessel.Load/MakeActive to call protoVessel.LoadObjects()
      // and duplicate all parts on next FLIGHT entry.
    }

    if (FlightGlobals.fetch != null)
      FlightGlobals.fetch.activeVessel = null;

    // FlightGlobals.OnSceneChange sets ready=false, which prevents
    // Vessel.Update from accessing null ActiveVessel.

    NovaLog.Log($"[Persistence] Packed {FlightGlobals.Vessels.Count} vessels for scene transition");
  }

  /// <summary>
  /// On first creation, DontDestroyOnLoad the FlightGlobals instance.
  /// On subsequent scene loads (re-entering FLIGHT), the scene prefab
  /// creates a duplicate — destroy it and keep ours.
  /// </summary>
  [HarmonyPostfix]
  [HarmonyPatch(typeof(FlightGlobals), "Awake")]
  public static void Awake_Postfix(FlightGlobals __instance) {
    if (persistentInstance == null) {
      persistentInstance = __instance;
      Object.DontDestroyOnLoad(__instance.gameObject);
      EnsureSceneEventHooked();
      NovaLog.Log("[Persistence] FlightGlobals marked persistent");
      return;
    }

    if (persistentInstance == __instance) return; // Same instance, no-op

    // Duplicate created by re-entering FLIGHT scene. The new scene's
    // components reference this new instance, so we can't just destroy it.
    // Instead: migrate our vessel data INTO the new instance, then let
    // the old one go. The new one becomes the persistent instance.
    NovaLog.Log($"[Persistence] Migrating {FlightGlobals.Vessels?.Count ?? 0} vessels to new FlightGlobals");

    // The new instance's Awake already set _fetch = this.
    // Copy vessels from old instance to new and restore loaded state.
    if (persistentInstance.vessels != null) {
      foreach (var vessel in persistentInstance.vessels) {
        if (vessel == null) continue;
        FlightGlobals.AddVessel(vessel);
      }
    }

    // Let old instance be destroyed normally
    var old = persistentInstance;
    persistentInstance = __instance;
    Object.DontDestroyOnLoad(__instance.gameObject);

    // Clear old vessel list so its OnDestroy doesn't interfere
    old.vessels?.Clear();
    Object.Destroy(old.gameObject);
  }

  static readonly System.Reflection.PropertyInfo patchedConicsAttachedProp =
    typeof(Vessel).GetProperty("PatchedConicsAttached");

  static void DetachFlightOnlyComponents(Vessel vessel) {
    if (!vessel.PatchedConicsAttached) return;
    if (vessel.orbitTargeter != null) Object.Destroy(vessel.orbitTargeter);
    if (vessel.patchedConicRenderer != null) Object.Destroy(vessel.patchedConicRenderer);
    if (vessel.patchedConicSolver != null) Object.Destroy(vessel.patchedConicSolver);
    vessel.orbitTargeter = null;
    vessel.patchedConicRenderer = null;
    vessel.patchedConicSolver = null;
    // Reset the flag so re-entering FLIGHT re-attaches the components
    // (AttachPatchedConicsSolver early-returns if PatchedConicsAttached==true).
    patchedConicsAttachedProp.SetValue(vessel, false);
  }

  static void EnsureSceneEventHooked() {
    if (sceneEventHooked) return;
    SceneManager.sceneLoaded += OnSceneLoaded;
    sceneEventHooked = true;
    NovaLog.Log("[Persistence] Hooked scene change event");
  }

  /// <summary>
  /// Called AFTER a new scene loads. If the previous scene was FLIGHT,
  /// pack all vessels and mark unloaded.
  /// </summary>
  static readonly System.Reflection.MethodInfo addOrbitRendererMethod =
    AccessTools.Method(typeof(Vessel), "AddOrbitRenderer");

  static readonly System.Reflection.FieldInfo framesAtStartupField =
    AccessTools.Field(typeof(Vessel), "framesAtStartup");

  static GameScenes lastScene = GameScenes.MAINMENU;
  static void OnSceneLoaded(Scene scene, LoadSceneMode mode) {
    var currentScene = HighLogic.LoadedScene;

    // Leaving FLIGHT — pack vessels
    if (lastScene == GameScenes.FLIGHT && currentScene != GameScenes.FLIGHT) {
      OnLeavingFlight();
    }

    lastScene = currentScene;
  }

  /// <summary>
  /// Skip OnDestroy entirely for our persistent instance — prevent
  /// Vessels.Clear() and _fetch = null during scene transitions.
  /// </summary>
  [HarmonyPrefix]
  [HarmonyPatch(typeof(FlightGlobals), "OnDestroy")]
  public static bool OnDestroy_Prefix(FlightGlobals __instance) {
    if (__instance == persistentInstance) {
      NovaLog.Log("[Persistence] Blocking FlightGlobals.OnDestroy for persistent instance");
      return false;
    }
    return true;
  }

  /// <summary>
  /// Replace Vessel.Update. Stock's Update has distance-based load/unload
  /// (accesses ActiveVessel without null check) and camera/terrain logic
  /// we don't need. We keep: mission time, UpdateVesselModuleActivation,
  /// autoClean, and autopilot.
  /// </summary>
  [HarmonyPrefix]
  [HarmonyPatch(typeof(Vessel), "Update")]
  public static bool VesselUpdate_Prefix(Vessel __instance) {
    if (__instance.state == Vessel.State.DEAD) return false;

    if (__instance.situation == Vessel.Situations.PRELAUNCH)
      __instance.launchTime = Planetarium.GetUniversalTime();
    __instance.missionTime = Planetarium.GetUniversalTime() - __instance.launchTime;

    __instance.UpdateVesselModuleActivation();

    if (__instance.AutoClean && !__instance.loaded &&
        HighLogic.CurrentGame.CurrenciesAvailable) {
      __instance.Clean(__instance.AutoCleanReason);
      return false;
    }

    if (__instance.IsControllable && !__instance.packed)
      __instance.Autopilot.Update();

    return false;
  }

  /// <summary>
  /// Replace Vessel.LateUpdate. Stock recalculates Landed/situation from
  /// physics state every frame — this overwrites our saved values when
  /// the vessel is packed (no physics, no ground contact → "FLYING").
  /// Only run situation updates for unpacked vessels in FLIGHT.
  /// </summary>
  static readonly System.Reflection.MethodInfo updateSituationMethod =
    AccessTools.Method(typeof(Vessel), "updateSituation");
  static readonly System.Reflection.MethodInfo checkControllableMethod =
    AccessTools.Method(typeof(Vessel), "CheckControllable");

  [HarmonyPrefix]
  [HarmonyPatch(typeof(Vessel), "LateUpdate")]
  public static bool VesselLateUpdate_Prefix(Vessel __instance) {
    if (__instance.state == Vessel.State.DEAD) return false;

    checkControllableMethod.Invoke(__instance, null);

    // Only recalculate situation/landed when unpacked (physics active).
    // Packed vessels keep their saved Landed/situation values intact.
    if (!__instance.packed) {
      __instance.UpdateLandedSplashed();
      updateSituationMethod.Invoke(__instance, null);
    }

    return false;
  }

  /// <summary>
  /// Persistent landed vessels have stale framesAtStartup (set in Vessel.Awake
  /// during creation, potentially in a different scene). Without a reset,
  /// GoOffRails skips the 75-frame physics hold and unpacks before terrain
  /// generates — the vessel falls through empty space and tumbles.
  /// Reset once per vessel instance using the Unity instance ID.
  /// </summary>
  static readonly System.Collections.Generic.HashSet<int> physicsHoldReset = new();

  [HarmonyPrefix]
  [HarmonyPatch(typeof(Vessel), "GoOffRails")]
  public static void GoOffRails_Prefix(Vessel __instance) {
    if (!__instance.LandedOrSplashed || !__instance.packed) return;
    int id = __instance.GetInstanceID();
    if (physicsHoldReset.Contains(id)) return;
    physicsHoldReset.Add(id);

    // Reset physics hold — stale framesAtStartup from creation in a
    // previous scene would skip the 75-frame terrain loading delay.
    framesAtStartupField.SetValue(__instance, Time.frameCount);

    // Fix terrainNormal — Initialize computed it with the vessel's world
    // rotation from creation time, which may have used wrong body transforms.
    // By now SetLandedPosRot has corrected the world rotation, so
    // recomputing gives the correct value for CheckGroundCollision's
    // terrain slope alignment.
    var upAxis = FlightGlobals.getUpAxis(__instance.mainBody, __instance.vesselTransform.position);
    __instance.terrainNormal = __instance.vesselTransform.InverseTransformDirection(upAxis);
  }

  [HarmonyPostfix]
  [HarmonyPatch(typeof(FlightGlobals), "AddVessel")]
  public static void AddVessel_Postfix(Vessel vessel) {
    if (vessel != null && vessel.gameObject != null) {
      Object.DontDestroyOnLoad(vessel.gameObject);
    }
  }

  /// <summary>
  /// When MapView initializes (FLIGHT map view, TRACKSTATION), recreate
  /// orbit renderers for persistent vessels. AddOrbitRenderer checks
  /// MapView.fetch != null, which is set in MapView.Awake before this
  /// postfix runs.
  /// </summary>
  [HarmonyPostfix]
  [HarmonyPatch(typeof(MapView), "Awake")]
  public static void MapView_Awake_Postfix() {
    if (FlightGlobals.fetch == null || FlightGlobals.Vessels == null) return;

    int count = 0;
    foreach (var vessel in FlightGlobals.Vessels) {
      if (vessel != null && vessel.orbitRenderer == null) {
        addOrbitRendererMethod.Invoke(vessel, null);
        count++;
      }
    }
    if (count > 0)
      NovaLog.Log($"[Persistence] Recreated orbit renderers for {count} vessels");
  }
}
