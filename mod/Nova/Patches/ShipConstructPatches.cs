using System.Collections.Generic;
using HarmonyLib;

namespace Nova.Patches;

[HarmonyPatch(typeof(ShipConstruct))]
public static class ShipConstructPatches {

  // Store root children between prefix and postfix to restore non-PART nodes.
  private static List<ConfigNode> allNodes = new();

  [HarmonyPrefix]
  [HarmonyPatch("LoadShip", typeof(ConfigNode), typeof(uint))]
  public static void LoadShip_Prefix(ConfigNode root) {
    allNodes.Clear();
    foreach (var node in root.nodes) {
      allNodes.Add(node as ConfigNode);
    }

    // Strip non-PART root nodes so KSP doesn't choke on them.
    root.nodes.Clear();
    foreach (var node in allNodes) {
      if (node.name != "PART") {
        continue;
      }
      root.nodes.Add(node);
    }
  }

  [HarmonyPostfix]
  [HarmonyPatch("LoadShip", typeof(ConfigNode), typeof(uint))]
  public static void LoadShip_Postfix(ShipConstruct __instance, ConfigNode root, bool __result) {
    root.nodes.Clear();
    foreach (var node in allNodes) {
      root.nodes.Add(node);
    }
  }
}
