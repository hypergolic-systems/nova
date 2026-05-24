using CommNet;
using HarmonyLib;
using Nova.Communications;
using UnityEngine;

namespace Nova.Patches;

// Two prefixes on CommNetScenario:
//
//  - OnAwake: bypass stock's `EnableCommNet` difficulty check so the
//    scenario (and therefore our CommNetUI subclass) is always alive,
//    regardless of the player's toggle. Nova replaces stock comms
//    entirely — the stock toggle is meaningless in the Nova world.
//
//  - Start: substitute NovaCommNetUI for stock's CommNetUI. The
//    inherited Awake assigns `Instance = this` on our subclass so
//    every reader of CommNetUI.Instance gets Nova's renderer.
public static class CommNetScenarioPatches {

  [HarmonyPatch(typeof(CommNetScenario), "OnAwake")]
  public static class OnAwakePatch {
    [HarmonyPrefix]
    public static bool Prefix(CommNetScenario __instance) {
      var existing = CommNetScenario.Instance;
      if (existing != null && existing != __instance) {
        Object.Destroy(existing);
      }
      AccessTools.Property(typeof(CommNetScenario), nameof(CommNetScenario.Instance))
        .SetValue(null, __instance, null);
      return false;
    }
  }

  [HarmonyPatch(typeof(CommNetScenario), "Start")]
  public static class StartPatch {
    [HarmonyPrefix]
    public static bool Prefix(CommNetScenario __instance) {
      __instance.gameObject.AddComponent<NovaCommNetUI>();
      __instance.gameObject.AddComponent<CommNetNetwork>();
      return false;
    }
  }
}
