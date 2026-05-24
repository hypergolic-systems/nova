using System.Collections.Generic;
using HarmonyLib;
using Nova.Effects;
using Waterfall;
using Waterfall.EffectControllers;

namespace Nova.Patches;

/// <summary>
/// Prefix-replace <c>ModuleWaterfallFX.LoadControllers</c> so Nova owns
/// the routing for the controller types it cares about
/// (<c>NovaThrottleController</c>, <c>NovaRcsController</c>). Everything
/// else (<c>AtmosphereDensityController</c>, <c>RandomnessController</c>,
/// <c>LightController</c>, etc.) defers to Waterfall's own
/// <see cref="EffectControllersMetadata"/> dispatch unchanged.
///
/// Why prefix-replace instead of swapping type-registrations: Waterfall's
/// <c>DetermineControllerTypeFromLegacyConfigNode</c> unconditionally
/// indexes <c>ControllersByType[typeof(ThrottleController)]</c> as the
/// default fallback for legacy <c>CONTROLLER</c> nodes; removing the
/// stock entry from that dict throws <c>KeyNotFoundException</c> from
/// inside <c>LoadControllers</c>, abandoning <c>Initialize</c> with
/// zero controllers and a silent plume failure. Adding our type without
/// removing the stock one works for routing but leaves Waterfall's
/// internal data structures in an inconsistent state (two types sharing
/// the same node name). Owning <c>LoadControllers</c> directly is the
/// clean answer — we never touch Waterfall's dispatch tables.
///
/// Re-fires on every re-init (OnLoad on an already-started FX module)
/// so live-edits via Waterfall's in-game editor stay correct.
/// </summary>
[HarmonyPatch(typeof(ModuleWaterfallFX), "LoadControllers")]
public static class WaterfallLoadControllersPatch {

  // Field accessors cached once; AccessTools' cache is internal but the
  // FieldInfo lookup is non-trivial.
  private static readonly AccessTools.FieldRef<ModuleWaterfallFX, List<WaterfallController>>
      AllControllersRef = AccessTools.FieldRefAccess<ModuleWaterfallFX, List<WaterfallController>>("allControllers");

  [HarmonyPrefix]
  public static bool Prefix(ModuleWaterfallFX __instance, ConfigNode node) {
    var allControllers = AllControllersRef(__instance);
    allControllers.Clear();

    foreach (var childNode in node.GetNodes()) {
      var controller = CreateController(childNode);
      if (controller == null) continue;
      if (__instance.FindController(controller.name) != null) continue;
      controller.mask = 1ul << allControllers.Count;
      allControllers.Add(controller);
    }

    return false; // skip original
  }

  // Build a controller for one CONTROLLER / *CONTROLLER cfg node.
  // Routes Nova-owned types (throttle, rcs) to our subclasses; defers
  // everything else to Waterfall's existing dispatch.
  private static WaterfallController CreateController(ConfigNode childNode) {
    // Nova-routed legacy form: CONTROLLER { linkedTo = throttle/rcs … }
    if (childNode.name == WaterfallConstants.LegacyControllerNodeName) {
      string linkedTo = null;
      if (childNode.TryGetValue(WaterfallController.LegacyControllerTypeNodeName, ref linkedTo)) {
        if (linkedTo == "throttle") return new NovaThrottleController(childNode);
        if (linkedTo == "rcs") return new NovaRcsController(childNode);
      }
      // Non-Nova legacy controller — defer to Waterfall's lookup, which
      // handles default-fallback to ThrottleController etc.
      return ResolveStockLegacy(childNode);
    }

    // Nova-routed new format. Match by the uppercased type name
    // Waterfall expects.
    if (childNode.name == "THROTTLECONTROLLER") return new NovaThrottleController(childNode);
    if (childNode.name == "RCSCONTROLLER") return new NovaRcsController(childNode);

    // Anything else: look up Waterfall's registered type by node name.
    if (EffectControllersMetadata.ControllersByConfigNodeName.TryGetValue(childNode.name, out var info)) {
      return info.CreateFromConfig(childNode);
    }
    return null;
  }

  // Mirror of Waterfall's `DetermineControllerTypeFromLegacyConfigNode`,
  // inlined so we don't have to reflect into a private method. Skips the
  // stock-default fallback to ThrottleController — if a legacy CONTROLLER
  // lacks a known `linkedTo`, we drop it rather than instantiate a stock
  // ThrottleController that would silently fail on a Nova engine.
  private static WaterfallController ResolveStockLegacy(ConfigNode childNode) {
    string linkedTo = null;
    if (!childNode.TryGetValue(WaterfallController.LegacyControllerTypeNodeName, ref linkedTo))
      return null;
    if (EffectControllersMetadata.ControllersByLegacyControllerTypeIds.TryGetValue(linkedTo, out var info)) {
      return info.CreateFromConfig(childNode);
    }
    return null;
  }
}
