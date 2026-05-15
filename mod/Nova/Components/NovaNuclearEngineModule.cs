using System.Linq;
using Nova.Core.Components.Propulsion;

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
}
