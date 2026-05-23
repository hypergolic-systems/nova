using System.Linq;
using HarmonyLib;
using Nova.Components;
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
/// Also rewires Nova-incompatible stock controllers: any
/// <see cref="ThrottleController"/> on a part with a sibling
/// <see cref="NovaEngineModule"/> gets swapped for a
/// <see cref="NovaWaterfallController"/> that reads
/// <c>Engine.ThrustOutputFraction</c> instead of stock
/// <c>ModuleEngines.currentThrottle</c> (Nova strips ModuleEngines, so
/// the stock controller would otherwise silently stay at zero — a dead
/// plume on every WaterfallRestock-covered Nova engine). The original
/// controller's <c>responseRateUp/Down</c> are preserved so template
/// authors' chosen spool ramp is kept intact.
///
/// Fires on the first <c>Start()</c>-triggered Initialize and on every
/// subsequent re-init (OnLoad after the FX module is already started).
/// Guarded against duplicate adds.
/// </summary>
[HarmonyPatch(typeof(ModuleWaterfallFX), "Initialize")]
public static class WaterfallInitializePatch {

  [HarmonyPostfix]
  public static void Postfix(ModuleWaterfallFX __instance) {
    if (__instance?.part == null) return;

    RewireStockControllers(__instance);
    InjectProviderControllers(__instance);
  }

  // Swap stock controllers that read from Nova-stripped stock modules
  // (ThrottleController → ModuleEngines.currentThrottle) for Nova-aware
  // equivalents on the same `name`, so modifier references in the
  // template keep resolving and the plume actually reflects engine state.
  // Only fires on parts that have a NovaEngineModule — leaves stock-
  // only parts (if any survive in a Nova install) untouched.
  private static void RewireStockControllers(ModuleWaterfallFX fx) {
    var novaEngine = fx.part.Modules.OfType<NovaEngineModule>().FirstOrDefault();
    if (novaEngine == null) return;

    // Snapshot before mutating: AddController inside the loop would
    // shift the list and re-enter this scan via InitializeEffects.
    var toReplace = fx.Controllers.OfType<ThrottleController>().ToList();
    foreach (var stock in toReplace) {
      var replacement = new NovaWaterfallController(
          stock.name,
          () => novaEngine.Engine == null ? 0f : (float)novaEngine.Engine.ThrustOutputFraction,
          stock.responseRateUp,
          stock.responseRateDown);
      fx.RemoveController(stock);
      fx.AddController(replacement);
    }
  }

  private static void InjectProviderControllers(ModuleWaterfallFX fx) {
    foreach (var provider in fx.part.Modules.OfType<IWaterfallControllerProvider>()) {
      foreach (var controller in provider.CreateWaterfallControllers()) {
        if (controller == null) continue;
        if (fx.FindController(controller.name) != null) continue;
        fx.AddController(controller);
      }
    }
  }
}
