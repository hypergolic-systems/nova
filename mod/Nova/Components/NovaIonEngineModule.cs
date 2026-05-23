using System.Collections.Generic;
using Nova.Core.Components.Propulsion;
using Nova.Effects;
using Waterfall;

namespace Nova.Components;

// NSTAR-class ion thruster part module. Subclasses NovaEngineModule
// for the entire FX / gimbal / thrust-application pipeline; only the
// throttle-input hook is overridden so a tripped engine ignores
// mainThrottle (the engine stays at 0 until the player resets the
// trip latch via the `setIonResetTrip` NovaPartTopic op).
//
// All player-facing ion controls flow through the topic — no PAW
// events, no [KSPEvent] buttons. `Ion` is the upcast Engine reference
// used by op handlers and telemetry.
public class NovaIonEngineModule : NovaEngineModule {

  /// <summary>Upcast accessor — same instance as `Engine`, typed.</summary>
  public IonEngine Ion => Engine as IonEngine;

  protected override bool ApplyPlayerThrottle(float throttle) {
    var ion = Ion;
    if (ion == null) return base.ApplyPlayerThrottle(throttle);
    // Tripped engines ignore mainThrottle entirely. Trip is sticky;
    // only `setIonResetTrip` clears it. Forcing throttle to 0 here is
    // belt-and-braces on top of IonEngine.OnPreSolve's gate.
    var effective = ion.Tripped ? 0f : throttle;
    if (effective == lastThrottle) return false;
    lastThrottle = effective;
    ion.Throttle = effective;
    return true;
  }

  // IEngineStatus override — base reports `isOperational` based on
  // engine.Throttle > 0. A tripped engine is not operational regardless
  // of the player's throttle setpoint.
  public override bool isOperational =>
      Ion != null && Ion.Tripped
          ? false
          : base.isOperational;

  public override float normalizedOutput =>
      Ion != null && Ion.Tripped
          ? 0f
          : base.normalizedOutput;

  // Stock `FXModuleAnimateThrottle` on the Dawn cfg looks up its
  // driving engine via `part.Modules.FindEngineNearby("Ion", …)` (the
  // value mirrors the stock `ModuleEnginesFX.engineID = Ion` we strip).
  // The lookup matches on `IEngineStatus.engineName` — the base
  // NovaEngineModule reports "Nova", so the FX module fails to bind
  // and silently falls back to driving the colorAnimation off
  // `vessel.ctrlState.mainThrottle`. That's why the engine glow
  // pulsed continuously whenever the player had main throttle up,
  // even on an unstaged ion drive. Returning "Ion" here re-binds
  // the FX module to our `isOperational` / `throttleSetting`, so
  // the animation gates on Active + Throttle + !Tripped correctly.
  public override string engineName => "Ion";

  // Ion-specific Waterfall controllers, on top of base throttle/active:
  //
  //   ionTripped  — 1 when the trip latch is set (any reason). Templates
  //                 multiply plume intensity by `1 - ionTripped` to kill
  //                 the beam on trip. Sticky until `setIonResetTrip`.
  //   ionHeat     — CoreTempK normalized to (AmbientK, MaxOperatingTempK).
  //                 Drives thermal glow modifiers; saturates at 1 just
  //                 as the overtemp trip fires.
  //   ionEcDraw   — CurrentEcW / RatedPowerW (0..1). Tracks actual EC
  //                 satisfaction, not commanded throttle — when EC is
  //                 starved the plume should dim regardless of throttle.
  public override IEnumerable<WaterfallController> CreateWaterfallControllers() {
    foreach (var c in base.CreateWaterfallControllers()) yield return c;

    yield return new NovaWaterfallController("ionTripped",
        () => Ion != null && Ion.Tripped ? 1f : 0f);

    yield return new NovaWaterfallController("ionHeat", () => {
      var ion = Ion;
      if (ion == null) return 0f;
      double span = ion.MaxOperatingTempK - ion.AmbientK;
      if (span <= 0) return 0f;
      double t = (ion.CoreTempK - ion.AmbientK) / span;
      if (t < 0) t = 0;
      if (t > 1) t = 1;
      return (float)t;
    });

    yield return new NovaWaterfallController("ionEcDraw", () => {
      var ion = Ion;
      if (ion == null || ion.RatedPowerW <= 0) return 0f;
      double f = ion.CurrentEcW / ion.RatedPowerW;
      if (f < 0) f = 0;
      if (f > 1) f = 1;
      return (float)f;
    });
  }
}
