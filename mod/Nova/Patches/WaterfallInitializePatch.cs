using System.Linq;
using HarmonyLib;
using Nova.Effects;
using Waterfall;

namespace Nova.Patches;

/// <summary>
/// Inject Nova-specific Waterfall controllers (throttle, reactor temp,
/// ion-trip, etc.) into every <see cref="ModuleWaterfallFX"/> right after
/// it finishes initializing. Waterfall's controller-type discovery is
/// reflection-scoped to its own assembly, so a Nova-declared
/// <see cref="WaterfallController"/> subclass can't appear in a CONTROLLER
/// node — but the public <see cref="ModuleWaterfallFX.AddController"/>
/// API accepts any controller instance, and the postfix is the cleanest
/// hook for "after Initialize completes."
///
/// Fires on the first <c>Start()</c>-triggered Initialize and on every
/// subsequent re-init (OnLoad after the FX module is already started).
/// Guarded by <see cref="ModuleWaterfallFX.FindController"/> so
/// re-injection doesn't duplicate names; Waterfall logs an error on
/// duplicate adds.
/// </summary>
[HarmonyPatch(typeof(ModuleWaterfallFX), "Initialize")]
public static class WaterfallInitializePatch {

  [HarmonyPostfix]
  public static void Postfix(ModuleWaterfallFX __instance) {
    if (__instance?.part == null) return;

    foreach (var provider in __instance.part.Modules.OfType<IWaterfallControllerProvider>()) {
      foreach (var controller in provider.CreateWaterfallControllers()) {
        if (controller == null) continue;
        if (__instance.FindController(controller.name) != null) continue;
        __instance.AddController(controller);
      }
    }
  }
}
