using System.Linq;
using UnityEngine;
using Nova.Components;
using Waterfall;

namespace Nova.Effects;

/// <summary>
/// Drop-in replacement for Waterfall's stock <c>RCSController</c> on
/// Nova RCS parts. Reads per-thruster activity from the sibling
/// <see cref="NovaRcsModule.NozzlePower"/> instead of stock
/// <c>ModuleRCSFX.thrustForces[]</c> (Nova strips ModuleRCSFX, so the
/// stock controller would silently return zero on every nozzle and
/// every plume would render flat).
///
/// Registered into Waterfall's controller dispatch tables at startup by
/// <see cref="NovaWaterfallRegistration"/>; once registered, every
/// <c>RCSCONTROLLER</c> config node (and the legacy
/// <c>linkedTo = rcs</c> form) instantiates this class instead of stock
/// <c>RCSController</c>. WaterfallRestock's RCS cfgs and any other
/// community pack parse unchanged.
///
/// Multi-value: <c>values[i]</c> holds the activity for the i-th active
/// thruster transform, in the same order as
/// <see cref="NovaRcsModule.ThrusterTransforms"/>. Both Nova and stock
/// use the <c>gameObject.activeInHierarchy</c> filter to pick which
/// thrusters are active, so the indexing aligns.
///
/// Persistent fields mirror stock <c>RCSController</c>
/// (<c>responseRateUp</c>, <c>responseRateDown</c>,
/// <c>thrusterTransformName</c>) so existing cfgs deserialize cleanly.
/// <c>thrusterTransformName</c> picks among multiple Nova RCS modules
/// on the same part when present (mirror of the stock disambiguation).
/// </summary>
public class NovaRcsController : WaterfallController {
  [Persistent] public float responseRateUp = 100f;
  [Persistent] public float responseRateDown = 100f;
  [Persistent] public string thrusterTransformName = "";

  private NovaRcsModule novaRcs;

  public NovaRcsController() : base() { }
  public NovaRcsController(ConfigNode node) : base(node) { }

  public override void Initialize(ModuleWaterfallFX host) {
    base.Initialize(host);

    novaRcs = host.part.Modules.OfType<NovaRcsModule>()
                  .FirstOrDefault(m => m.thrusterTransformName == thrusterTransformName)
              ?? host.part.Modules.OfType<NovaRcsModule>().FirstOrDefault();

    if (novaRcs == null) {
      Utils.LogWarning($"[NovaRcsController] No NovaRcsModule on part " +
                       $"{host.part.partInfo?.name} — all nozzles will report 0.");
      values = System.Array.Empty<float>();
      return;
    }

    values = new float[novaRcs.ThrusterCount];
  }

  protected override bool UpdateInternal() {
    if (novaRcs == null) return false;
    var power = novaRcs.NozzlePower;
    bool awake = false;
    int n = System.Math.Min(values.Length, power.Count);
    for (int i = 0; i < n; i++) {
      float target = power[i];
      float old = values[i];
      if (Utils.ApproximatelyEqual(old, target)) continue;
      float rate = target > old ? responseRateUp : responseRateDown;
      values[i] = rate <= 0f
          ? target
          : Mathf.MoveTowards(old, target, rate * TimeWarp.deltaTime);
      awake = true;
    }
    return awake;
  }
}
