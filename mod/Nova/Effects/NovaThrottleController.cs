using System.Linq;
using UnityEngine;
using Nova.Components;
using Waterfall;

namespace Nova.Effects;

/// <summary>
/// Drop-in replacement for Waterfall's stock <c>ThrottleController</c> on
/// Nova engine parts. Reads <see cref="Nova.Core.Components.Propulsion.Engine.ThrustOutputFraction"/>
/// from the sibling <see cref="NovaEngineModule"/> instead of stock
/// <c>ModuleEngines.currentThrottle</c> (Nova strips <c>ModuleEngines</c>,
/// so the stock controller would silently report zero and plumes would
/// render flat).
///
/// Registered into Waterfall's controller dispatch tables at startup by
/// <see cref="NovaWaterfallRegistration"/>; once registered, every
/// <c>THROTTLECONTROLLER</c> config node (and the legacy
/// <c>linkedTo = throttle</c> form) instantiates this class instead of
/// stock <c>ThrottleController</c>. Templates referencing
/// <c>controllerName = throttle</c> resolve transparently.
///
/// Fields mirror <c>ThrottleController</c>'s persistent surface
/// (<c>responseRateUp</c>, <c>responseRateDown</c>, <c>engineID</c>) so
/// existing community Waterfall configs (WaterfallRestock, etc.) parse
/// unchanged. <c>engineID</c> is accepted but ignored — Nova has one
/// engine per part.
/// </summary>
public class NovaThrottleController : WaterfallController {
  [Persistent] public float responseRateUp = 100f;
  [Persistent] public float responseRateDown = 100f;
  [Persistent] public string engineID = "";

  private NovaEngineModule novaEngine;

  public NovaThrottleController() : base() { }
  public NovaThrottleController(ConfigNode node) : base(node) { }

  public override void Initialize(ModuleWaterfallFX host) {
    base.Initialize(host);
    values = new float[1];
    novaEngine = host.part.Modules.OfType<NovaEngineModule>().FirstOrDefault();
    if (novaEngine == null) {
      Utils.LogWarning($"[NovaThrottleController] No NovaEngineModule on part " +
                       $"{host.part.partInfo?.name} — throttle will report 0.");
    }
  }

  protected override float UpdateSingleValue() {
    if (novaEngine?.Engine == null) return 0f;
    float target = (float)novaEngine.Engine.ThrustOutputFraction;
    float current = values[0];
    if (current == target) return target;
    float rate = target > current ? responseRateUp : responseRateDown;
    if (rate <= 0f) return target;
    return Mathf.MoveTowards(current, target, rate * TimeWarp.deltaTime);
  }
}
