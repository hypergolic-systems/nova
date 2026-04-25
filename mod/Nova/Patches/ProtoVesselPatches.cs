using System.Runtime.CompilerServices;
using HarmonyLib;
using Nova.Components;
namespace Nova.Patches;

[HarmonyPatch(typeof(ProtoVessel))]
public static class ProtoVesselPatches {

  private static ConditionalWeakTable<ProtoVessel, ConfigNode> vesselNodes = new();

  /// <summary>
  /// Get the captured ConfigNode for a ProtoVessel, if available.
  /// Used by NovaVesselModule.EnsureVirtual for lazy VirtualVessel creation.
  /// </summary>
  public static ConfigNode GetCapturedNode(ProtoVessel pv) {
    if (pv != null && vesselNodes.TryGetValue(pv, out var node))
      return node;
    return null;
  }

  [HarmonyPostfix]
  [HarmonyPatch(MethodType.Constructor, typeof(ConfigNode), typeof(Game))]
  public static void Constructor_Postfix(ProtoVessel __instance, ConfigNode node) {
    vesselNodes.Add(__instance, node);
  }

  [HarmonyPostfix]
  [HarmonyPatch("Load", typeof(FlightState), typeof(Vessel))]
  public static void Load_Postfix(ProtoVessel __instance) {
    if (!vesselNodes.TryGetValue(__instance, out var node)) {
      return;
    }
    // Not all vessels have vesselRef or vessel modules.
    __instance.vesselRef?.FindVesselModuleImplementing<NovaVesselModule>()?.OnVesselFullLoad(node);
  }
}
