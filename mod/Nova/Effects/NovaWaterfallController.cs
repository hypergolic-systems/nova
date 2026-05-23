using System;
using UnityEngine;
using Waterfall;

namespace Nova.Effects;

// A WaterfallController whose value is whatever a delegate returns.
// One class for every Nova-specific signal — no per-signal subclasses —
// because Waterfall's modifier surface only cares about the named scalar
// in `values[0]`, and the Func<float> closes over whatever VirtualComponent
// or PartModule field the signal actually lives on.
//
// Wired in by `WaterfallInitializePatch`: after `ModuleWaterfallFX.Initialize`
// runs, the patch walks sibling modules implementing `IWaterfallControllerProvider`
// and registers each yielded controller via `AddController`. Re-fires on
// every Initialize (which includes save-load re-init), so a stale value
// from a previous load is replaced with a fresh closure.
//
// Optional slew (responseRateUp/Down, units = 1/sec) ramps `values[0]`
// toward `source()` at the named rate, matching the cosmetic spool-up
// behaviour of stock `ThrottleController`. Used when we hot-swap a stock
// ThrottleController for a Nova-aware one and want to preserve the
// template author's chosen ramp feel; zero (default) = instantaneous.
//
// Save() returns a no-op marker node — Waterfall's loader skips unknown
// node names, and our re-injection happens unconditionally on Initialize,
// so the marker is never actually load-bearing. We just need *something*
// non-null because Waterfall's ExportModule walks Controllers and calls
// Save() on each.
public class NovaWaterfallController : WaterfallController {
  private readonly Func<float> source;
  private readonly float responseRateUp;
  private readonly float responseRateDown;

  public NovaWaterfallController(string controllerName, Func<float> source,
      float responseRateUp = 0f, float responseRateDown = 0f) {
    this.name = controllerName;
    this.source = source;
    this.responseRateUp = responseRateUp;
    this.responseRateDown = responseRateDown;
    this.values = new float[1];
  }

  // Defensive ConfigNode ctor. Not used in practice (Nova never
  // registers this type with Waterfall's reflection scan, so the
  // type-discovery loader never instantiates one from CONTROLLER nodes),
  // but `WaterfallController`-derived types are documented to need a
  // (ConfigNode) ctor, and `EffectControllerInfo` asserts on it. Cheap
  // to ship; impossible to forget.
  public NovaWaterfallController(ConfigNode node) : base(node) {
    this.values = new float[1];
    this.source = null;
  }

  protected override float UpdateSingleValue() {
    float target = source?.Invoke() ?? 0f;
    if (responseRateUp <= 0f && responseRateDown <= 0f) return target;
    float current = values[0];
    if (current == target) return target;
    float rate = target > current ? responseRateUp : responseRateDown;
    if (rate <= 0f) return target;
    return Mathf.MoveTowards(current, target, rate * TimeWarp.deltaTime);
  }

  public override ConfigNode Save() {
    var node = new ConfigNode("NOVAWATERFALLCONTROLLER");
    node.AddValue("name", name);
    return node;
  }
}
