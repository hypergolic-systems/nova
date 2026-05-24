using System;
using System.Collections.Generic;
using System.Reflection;
using Waterfall;
using Waterfall.EffectControllers;

namespace Nova.Effects;

/// <summary>
/// One-time startup hook that swaps Nova-aware controller types into
/// Waterfall's static type-dispatch tables, so Waterfall instantiates our
/// types from the start instead of stock controllers that bind to
/// Nova-stripped modules.
///
/// Waterfall's controller-type discovery (<see cref="EffectControllersMetadata"/>)
/// reflection-scans only its own assembly, so Nova-declared controllers
/// can't appear there naturally. Patching the dispatch dictionaries by
/// hand here lets us reuse Waterfall's entire config-driven instantiation
/// pipeline (<c>CONTROLLER</c> / <c>THROTTLECONTROLLER</c> node names,
/// legacy <c>linkedTo = …</c> form) without rewriting any of it.
///
/// Called from <see cref="HypergolicSystemsMod.InitializeSystems"/> at
/// <c>KSPAddon.Startup.Instantly</c>, which fires well before any
/// <see cref="ModuleWaterfallFX.Initialize"/> runs (those happen on
/// flight-scene <c>Start()</c>).
///
/// Three dicts get swapped per Nova controller:
///   * <c>ControllersByType</c> — instance lookup by type
///   * <c>ControllersByConfigNodeName</c> — new-format node-name → type
///   * <c>ControllersByLegacyControllerTypeIds</c> — old-format
///     <c>linkedTo</c> value → type
///
/// The dicts are exposed as <c>IReadOnlyDictionary</c> but constructed
/// as <c>Dictionary</c> (<c>ToDictionary(…)</c>) — the downcast works.
/// Fields are <c>readonly</c> in the C# sense, but that forbids
/// re-assigning the reference, not mutating the dict contents.
/// </summary>
public static class NovaWaterfallRegistration {

  private static bool registered;

  public static void Register() {
    if (registered) return;
    registered = true;

    SwapController<NovaThrottleController>(
        replacing: typeof(ThrottleController),
        legacyTypeId: "throttle");

    SwapController<NovaRcsController>(
        replacing: typeof(RCSController),
        legacyTypeId: "rcs");
  }

  private static void SwapController<TNova>(Type replacing, string legacyTypeId)
      where TNova : WaterfallController {
    var byType = Reflect<Dictionary<Type, EffectControllerInfo>>("ControllersByType");
    var byNodeName = Reflect<Dictionary<string, EffectControllerInfo>>("ControllersByConfigNodeName");
    var byLegacy = Reflect<Dictionary<string, EffectControllerInfo>>("ControllersByLegacyControllerTypeIds");

    var info = new EffectControllerInfo(typeof(TNova));

    // Drop the stock entry first so byType doesn't end up with two
    // EffectControllerInfo objects keyed at different types but sharing
    // the same ConfigNodeName (would confuse Waterfall's editor UI).
    byType.Remove(replacing);
    byType[typeof(TNova)] = info;

    // New-format key = uppercased type name. The stock and Nova types
    // typically share the same node name (THROTTLECONTROLLER), so this
    // overwrites the stock binding.
    byNodeName[EffectControllersMetadata.GetConfigNodeName(typeof(TNova))] = info;

    // Legacy `linkedTo = throttle` form. Repoint at the Nova type.
    byLegacy[legacyTypeId] = info;
  }

  private static T Reflect<T>(string fieldName) where T : class {
    var field = typeof(EffectControllersMetadata).GetField(
        fieldName, BindingFlags.Static | BindingFlags.Public);
    if (field == null)
      throw new InvalidOperationException(
          $"EffectControllersMetadata.{fieldName} not found — Waterfall API drift?");
    var value = field.GetValue(null) as T;
    if (value == null)
      throw new InvalidOperationException(
          $"EffectControllersMetadata.{fieldName} is not the expected {typeof(T).Name} — Waterfall API drift?");
    return value;
  }
}
