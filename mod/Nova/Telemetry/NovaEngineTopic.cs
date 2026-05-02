using System.Collections.Generic;
using Dragonglass.Telemetry.Topics;
using Nova.Components;
using Nova.Core.Components.Propulsion;
using Nova.Core.Resources;
using UnityEngine;

namespace Nova.Telemetry;

// Nova's engine-topic override. Subclass of DG's EngineTopic — wire
// shape and Name are inherited unchanged, the only override is the
// data source. Where the stock topic samples ModuleEngines + walks
// `Part.crossfeedPartSet`, this reads from Nova's own LP solver:
//
//   - Per-engine state (throttle, Isp, thrust, ignition, flameout)
//     comes from the `Engine` virtual component.
//   - Fuel-pool reach + propellant aggregation use
//     `ResourceSolver.ReachableNodes` / `ReachableBuffers` from the
//     engine's solver node — same connectivity the LP enforces, so
//     the HUD's fuel-group readout matches what the DV simulation
//     would compute if you isolated this engine and let it drain.
//
// Stock `crossfeedPartSet` is dead in Nova (we patch out
// `Vessel.BuildCrossfeedPartSets`). The signature on the wire uses
// solver node IDs rather than part flightIDs — opaque strings the
// HUD's `groupEngines` only consumes as a grouping key.
//
// Like the base topic we maintain a structural cache rebuilt on KSP
// `GameEvents` so the base class's `HasMaterialChange` reference-
// equality check on `CrossfeedPartIds` doesn't fire every frame.
public sealed class NovaEngineTopic : EngineTopic {

  private bool _structureDirty = true;
  private Vessel _cachedVessel;
  private readonly List<EngineEntry> _structure = new();

  private sealed class EngineEntry {
    public Part Part;
    public Engine Engine;
    public List<string> CrossfeedPartIds = new();
    public List<PropellantEntry> Propellants = new();
  }

  private sealed class PropellantEntry {
    public int ResourceId;
    public string Name;
    public string Abbreviation;
    public List<Buffer> Buffers = new();
  }

  protected override void OnEnable() {
    base.OnEnable();
    GameEvents.onVesselWasModified.Add(OnNovaVesselModified);
    GameEvents.onStageActivate.Add(OnNovaStageActivated);
    GameEvents.onDockingComplete.Add(OnNovaDocking);
    GameEvents.onVesselsUndocking.Add(OnNovaUndocking);
    GameEvents.onVesselChange.Add(OnNovaVesselChanged);
  }

  protected override void OnDisable() {
    base.OnDisable();
    GameEvents.onVesselWasModified.Remove(OnNovaVesselModified);
    GameEvents.onStageActivate.Remove(OnNovaStageActivated);
    GameEvents.onDockingComplete.Remove(OnNovaDocking);
    GameEvents.onVesselsUndocking.Remove(OnNovaUndocking);
    GameEvents.onVesselChange.Remove(OnNovaVesselChanged);
  }

  private void OnNovaVesselModified(Vessel v) {
    if (v == FlightGlobals.ActiveVessel) _structureDirty = true;
  }
  private void OnNovaStageActivated(int _) { _structureDirty = true; }
  private void OnNovaDocking(GameEvents.FromToAction<Part, Part> _) { _structureDirty = true; }
  private void OnNovaUndocking(Vessel _, Vessel __) { _structureDirty = true; }
  private void OnNovaVesselChanged(Vessel _) { _structureDirty = true; }

  protected override bool SampleEngines(Vessel v, Transform refT, List<EngineFrame> scratch) {
    if (v == null || v.parts == null) return true;

    if (v != _cachedVessel) {
      _cachedVessel = v;
      _structureDirty = true;
    }
    if (_structureDirty) {
      RebuildStructure(v);
      _structureDirty = false;
    }

    Vector3 vesselPos = v.transform.position;

    for (int i = 0; i < _structure.Count; i++) {
      var es = _structure[i];
      if (es.Part == null || es.Engine == null) {
        _structureDirty = true;
        return false;
      }

      Vector3 rel = es.Part.transform.position - vesselPos;
      Vector3 local = refT.InverseTransformDirection(rel);

      byte status = Classify(es.Engine);
      // Only burning engines have a meaningful throttle.
      // NormalizedOutput already folds in propellant satisfaction.
      float throttle = status == 0
        ? Mathf.Clamp01((float)(es.Engine.Throttle * es.Engine.NormalizedOutput))
        : 0f;

      var propellants = new List<PropellantFrame>(es.Propellants.Count);
      bool stale = false;
      for (int pi = 0; pi < es.Propellants.Count; pi++) {
        var pe = es.Propellants[pi];
        double amt = 0, cap = 0;
        for (int bi = 0; bi < pe.Buffers.Count; bi++) {
          var buf = pe.Buffers[bi];
          if (buf == null) { stale = true; break; }
          amt += buf.Contents;
          cap += buf.Capacity;
        }
        if (stale) break;
        propellants.Add(new PropellantFrame {
          ResourceId = pe.ResourceId,
          Name = pe.Name,
          Abbreviation = pe.Abbreviation,
          Amount = amt,
          Capacity = cap,
        });
      }
      if (stale) {
        _structureDirty = true;
        return false;
      }

      scratch.Add(new EngineFrame {
        Id = es.Part.flightID.ToString(),
        MapX = local.x,
        MapY = local.z,
        Status = status,
        Throttle = throttle,
        MaxThrust = (float)es.Engine.Thrust,
        Isp = (float)es.Engine.Isp,
        CrossfeedPartIds = es.CrossfeedPartIds,
        Propellants = propellants,
      });
    }
    return true;
  }

  private void RebuildStructure(Vessel v) {
    _structure.Clear();
    if (v == null || v.parts == null) return;

    var vm = v.FindVesselModuleImplementing<NovaVesselModule>();
    var solver = vm?.Virtual?.Solver;
    if (solver == null) {
      // VirtualVessel hasn't been built yet (early in vessel load).
      // Mark dirty so we retry next frame.
      _structureDirty = true;
      return;
    }

    for (int i = 0; i < v.parts.Count; i++) {
      Part p = v.parts[i];
      if (p == null) continue;

      var module = p.FindModuleImplementing<NovaEngineModule>();
      if (module == null) continue;

      var engine = module.Engine;
      if (engine == null || engine.Node == null) {
        // Engine module hasn't finished OnStart yet, or solver was
        // rebuilt without re-running OnBuildSolver. Retry next frame.
        _structureDirty = true;
        continue;
      }

      var es = new EngineEntry { Part = p, Engine = engine };

      // Fuel-pool reach: union of reachable solver nodes across this
      // engine's propellants. Using node IDs (rather than part IDs)
      // sidesteps the node↔parts mapping — the HUD only consumes this
      // list as an opaque grouping signature, and node identity is
      // exactly what defines the LP-visible fuel pool.
      var reachedNodes = new HashSet<ResourceSolver.Node>();
      foreach (var prop in engine.Propellants) {
        foreach (var n in solver.ReachableNodes(engine.Node, prop.Resource))
          reachedNodes.Add(n);
      }
      var nodeIds = new List<long>(reachedNodes.Count);
      foreach (var n in reachedNodes) nodeIds.Add(n.Id);
      nodeIds.Sort();
      foreach (var id in nodeIds) es.CrossfeedPartIds.Add(id.ToString());

      // Per-propellant buffer collection. Resolve once; per-frame we
      // just sum live Contents/Capacity off the cached refs.
      foreach (var prop in engine.Propellants) {
        var pe = new PropellantEntry {
          ResourceId = ResolveResourceId(prop.Resource.Name),
          Name = prop.Resource.Name,
          Abbreviation = prop.Resource.Abbreviation,
        };
        pe.Buffers.AddRange(solver.ReachableBuffers(engine.Node, prop.Resource));
        es.Propellants.Add(pe);
      }

      _structure.Add(es);
    }
  }

  private static int ResolveResourceId(string name) {
    var lib = PartResourceLibrary.Instance;
    if (lib == null) return 0;
    var def = lib.GetDefinition(name);
    return def != null ? def.id : 0;
  }

  private static byte Classify(Engine e) {
    if (e.Ignited && e.Flameout) return 1;                    // flameout
    if (e.Ignited && e.NormalizedOutput > 0) return 0;        // burning
    if (e.Ignited) return 4;                                  // idle (armed, throttle 0 / starved-but-not-flagged)
    return 3;                                                 // shutdown (not yet staged or shut down)
  }
}
