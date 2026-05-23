using System.Collections.Generic;
using System.Linq;
using Nova.Core.Components.Propulsion;
using Nova.Effects;
using Waterfall;

namespace Nova.Components;

// LV-N nuclear-thermal-rocket part module. Subclasses NovaEngineModule
// to reuse the entire FX / gimbal / thrust-application pipeline,
// overriding only the throttle-input hook so the player's
// `mainThrottle` lands on `NuclearEngine.PlayerThrottle` (the
// reactor state machine's setpoint) instead of `engine.Throttle`
// (the LP demand, which the reactor's OnPreSolve drives internally).
//
// All player-facing reactor controls flow through NovaPartTopic ops
// (`setReactorActive`) — no PAW events, no [KSPEvent] buttons.
// `Reactor` is the upcast Engine reference for callers that need the
// reactor-specific surface (e.g. the NovaPartTopic op handler).
public class NovaNuclearEngineModule : NovaEngineModule {

  /// <summary>Upcast accessor — same instance as `Engine`, typed.</summary>
  public NuclearEngine Reactor => Engine as NuclearEngine;

  protected override bool ApplyPlayerThrottle(float throttle) {
    var reactor = Reactor;
    if (reactor == null) return base.ApplyPlayerThrottle(throttle);
    // The base writes lastThrottle so the flameout-FX path keeps
    // working; we route the value to PlayerThrottle instead of
    // overwriting the reactor's LP-demand Throttle.
    if (throttle == lastThrottle) return false;
    lastThrottle = throttle;
    reactor.PlayerThrottle = throttle;
    return true;
  }

  // IEngineStatus override — base reports `isOperational` based on
  // engine.Throttle, which for the NTR includes idle cooling flow.
  // We want stock/SAS code paths to see the reactor as "operational"
  // only when actually producing thrust (Throttled state).
  public override bool isOperational =>
      Reactor != null
          ? Reactor.State == ReactorState.Throttled
            && Reactor.ThrustOutputFraction > 0
          : base.isOperational;

  public override float normalizedOutput =>
      Reactor != null ? (float)Reactor.ThrustOutputFraction : base.normalizedOutput;

  // NTR-specific Waterfall controllers, on top of base throttle/active:
  //
  //   reactorTemp        — CoreTempK normalized to (AmbientK, OperatingTempK).
  //                        Drives tungsten-glow ramps; saturates at 1 once the
  //                        core is at operating temperature.
  //   playerThrottle     — clock-anchored ThrottleActual (0..1). Smoother than
  //                        `throttle` (which is ThrustOutputFraction and gets
  //                        gated to 0 outside Throttled state) for slew-driven
  //                        FX like nozzle-glow ramping during spool-up.
  //   reactorStateCold   — one-hot bit, 1 when State == Cold (else 0).
  //   reactorStateWarming
  //   reactorStateThrottled
  //   reactorStateCooling — same pattern for the other three live states.
  //                         Lets templates light up state-specific FX
  //                         (e.g. cooling LH₂ vent during shutdown)
  //                         without having to threshold a scalar state.
  public override IEnumerable<WaterfallController> CreateWaterfallControllers() {
    foreach (var c in base.CreateWaterfallControllers()) yield return c;

    yield return new NovaWaterfallController("reactorTemp", () => {
      var r = Reactor;
      if (r == null) return 0f;
      double span = r.OperatingTempK - r.AmbientK;
      if (span <= 0) return 0f;
      double t = (r.CoreTempK - r.AmbientK) / span;
      if (t < 0) t = 0;
      if (t > 1) t = 1;
      return (float)t;
    });

    yield return new NovaWaterfallController("playerThrottle",
        () => Reactor == null ? 0f : (float)Reactor.ThrottleActual);

    yield return new NovaWaterfallController("reactorStateCold",
        () => Reactor != null && Reactor.State == ReactorState.Cold ? 1f : 0f);
    yield return new NovaWaterfallController("reactorStateWarming",
        () => Reactor != null && Reactor.State == ReactorState.Warming ? 1f : 0f);
    yield return new NovaWaterfallController("reactorStateThrottled",
        () => Reactor != null && Reactor.State == ReactorState.Throttled ? 1f : 0f);
    yield return new NovaWaterfallController("reactorStateCooling",
        () => Reactor != null && Reactor.State == ReactorState.Cooling ? 1f : 0f);
  }
}
