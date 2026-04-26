using System.Collections.Generic;
using Dragonglass.Telemetry.Topics;
using Nova.Components;
using Nova.Core.Components.Propulsion;
using UnityEngine;

namespace Nova.Telemetry;

// Nova's engine-topic override. Subclass of DG's EngineTopic — wire
// shape and Name are inherited unchanged, the only override is the
// data source. Where the stock topic samples ModuleEngines, this
// reads from Nova's virtual `Engine` component (live throttle, Isp,
// thrust, ignition, flameout) so the rosette reflects Nova-managed
// vessels rather than the empty/garbage frames stock would emit.
//
// Crossfeed pruning and propellant aggregation use stock KSP's
// `Part.crossfeedPartSet` machinery, which works on Part flags +
// vessel topology and is independent of which engine module is
// attached. The propellant resource ids round-trip through
// `PartResourceLibrary` by name.
public sealed class NovaEngineTopic : EngineTopic {

  // Empty list reused for engines whose crossfeed/propellant info we
  // can't resolve (e.g. an unloaded crossfeedPartSet, or an
  // unregistered propellant resource).
  private static readonly List<string> EmptyCrossfeed = new();
  private static readonly List<PropellantFrame> EmptyPropellants = new();

  protected override bool SampleEngines(Vessel v, Transform refT, List<EngineFrame> scratch) {
    if (v == null || v.parts == null) return true;
    Vector3 vesselPos = v.transform.position;

    for (int i = 0; i < v.parts.Count; i++) {
      Part p = v.parts[i];
      if (p == null) continue;

      var module = p.FindModuleImplementing<NovaEngineModule>();
      if (module == null) continue;

      Engine engine = null;
      foreach (var c in module.Components) {
        if (c is Engine e) { engine = e; break; }
      }
      if (engine == null) continue;

      Vector3 rel = p.transform.position - vesselPos;
      Vector3 local = refT.InverseTransformDirection(rel);

      byte status = Classify(engine);
      // Match the base contract: only a burning engine has a
      // meaningful throttle on the wire. NormalizedOutput already
      // folds in propellant satisfaction, so we ship the actual
      // realized output rather than the commanded throttle when
      // status is burning.
      float throttle = status == 0
        ? Mathf.Clamp01((float)(engine.Throttle * engine.NormalizedOutput))
        : 0f;

      scratch.Add(new EngineFrame {
        Id = p.flightID.ToString(),
        MapX = local.x,
        MapY = local.z,
        Status = status,
        Throttle = throttle,
        MaxThrust = (float)engine.Thrust,
        Isp = (float)engine.Isp,
        // V1: empty crossfeed + propellants. The rosette renders
        // dot positions / throttle / status from the fields above;
        // fuel-group bars and crossfeed-aware grouping land in a
        // follow-up that mirrors the stock impl's PartSet walk.
        CrossfeedPartIds = EmptyCrossfeed,
        Propellants = EmptyPropellants,
      });
    }
    return true;
  }

  private static byte Classify(Engine e) {
    if (e.Ignited && e.Flameout) return 1;                    // flameout
    if (e.Ignited && e.NormalizedOutput > 0) return 0;        // burning
    if (e.Ignited) return 4;                                  // idle (armed, throttle 0 / starved-but-not-flagged)
    return 3;                                                 // shutdown (not yet staged or shut down)
  }
}
