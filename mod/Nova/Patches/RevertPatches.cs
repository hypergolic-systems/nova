using HarmonyLib;
using KSP.UI.Screens;
using Nova.Persistence;
using Nova;

namespace Nova.Patches;

/// <summary>
/// Treat revert-to-launch as a quickload from the in-memory PostInit
/// snapshot. Stock revert rebuilds <see cref="FlightDriver.FlightStateCache"/>
/// from <see cref="FlightDriver.PostInitState"/> and triggers a scene reload
/// (<see cref="HighLogic.LoadScene"/>) — but Nova vessels survive scene
/// changes via <see cref="FlightGlobalsPersistencePatches"/>, so the
/// scene-reload path leaves the live vessel with its mid-flight rigidbody
/// state and crashes through terrain at launchpad altitude. Instead we
/// replay the launch snapshot through <see cref="NovaSaveLoader.ApplyQuickload"/>,
/// matching the existing matched-vessel quickload semantics: same
/// VirtualVessel instance, fresh runtime state, no scene transition.
/// </summary>
[HarmonyPatch(typeof(FlightDriver))]
public static class RevertPatches {

  [HarmonyPrefix]
  [HarmonyPatch(nameof(FlightDriver.RevertToLaunch))]
  public static bool RevertToLaunch_Prefix() {
    var snapshot = SaveGamePatches.PostInitSnapshot;
    if (snapshot == null) {
      NovaLog.Log("[Revert] No PostInit snapshot available — falling back to stock revert.");
      return true;
    }

    NovaLog.Log($"[Revert] Applying PostInit snapshot — UT={snapshot.UniversalTime:F1}, vessels={snapshot.Vessels.Count}");
    NovaSaveLoader.ApplyQuickload(snapshot);

    // Reset the staging stack to "all stages remaining". Stock revert's
    // scene reload runs FlightDriver.setStartupNewVessel which calls
    // StageManager.BeginFlight as a side effect — that walks parts to
    // find the highest inverseStage and sets currentStage = max + 1.
    // Without this, vessel.currentStage retains its mid-flight value
    // (decremented every time the player staged), so a freshly-reverted
    // vessel can't be staged because KSP thinks every stage already
    // fired. BeginFlight also flips canSeparate true and rebuilds the
    // icon list so the staging UI re-displays the launch configuration.
    StageManager.BeginFlight();

    // Stock revert was invoked from the pause menu, which relies on the
    // scene reload (HighLogic.LoadScene → scene tear-down) to close
    // itself. We skipped the reload, so the menu's internal isOpen flag
    // stays latched true — which gates Dragonglass's overlay canvas off
    // (DragonglassHudAddon.IsPauseMenuOpen). Close it ourselves.
    if (PauseMenu.exists && PauseMenu.isOpen) PauseMenu.Close();
    return false;
  }
}
