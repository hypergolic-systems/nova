using System.Collections.Generic;
using System.Text;
using Dragonglass.Telemetry.Topics;
using Nova.Components;
using Nova.Core.Components.Propulsion;
using Nova.Core.Resources;
using Nova.Core.Systems;
using UnityEngine;
// DG owns one EngineFrame type; Nova.Core owns its own with the same shape.
// Disambiguate so both names resolve cleanly inside this file.
using NovaEngineFrame = Nova.Core.Telemetry.EngineFrame;
using NovaPropellantFrame = Nova.Core.Telemetry.EnginePropellantFrame;

namespace Nova.Telemetry;

// Nova's engine-topic override.
//
// Still a subclass of `Dragonglass.Telemetry.Topics.EngineTopic` so
// the broadcaster registers us under the same wire name and lifecycle
// (`OnEnable` / `OnDisable` / `Update` / change-detection /
// `MarkDirty` all flow through DG's base). The two things we change:
//
//   1. **Data source** — `SampleEngines` reads from Nova's `Engine`
//      virtual components + `StagingFlowSystem` reach data, NOT from
//      stock `ModuleEngines` + `Part.crossfeedPartSet` (which Nova
//      patches out anyway).
//   2. **Wire emission** — `WriteData` calls Nova.Core's
//      `EngineFrameFormatter` against a Nova-owned `List<EngineFrame>`,
//      so the bytes on the wire come from code Nova controls. The
//      simulator emits the same bytes through the same formatter,
//      keeping mod/sim parity byte-for-byte.
//
// `_novaFrames` mirrors DG's private `_engines` cache — populated in
// `SampleEngines` alongside the DG-typed `scratch` list that the base
// class still uses for its `HasMaterialChange` / `MarkDirty` plumbing.
// DG's inherited `WriteData` is never called (overridden here); its
// `_engines` list becomes dead weight but the cost is one extra
// allocation per active engine per change-detection cycle — trivial.
public sealed class NovaEngineTopic : EngineTopic {

  private bool _structureDirty = true;
  private Vessel _cachedVessel;
  private readonly List<EngineEntry> _structure = new();

  // Nova-owned wire cache. Repopulated on every SampleEngines call;
  // read by our WriteData override.
  private readonly List<NovaEngineFrame> _novaFrames = new();
  private string _novaVesselId = "";

  private sealed class EngineEntry {
    public Part Part;
    public Engine Engine;
    public List<string> CrossfeedPartIds = new();
    public List<PropellantEntry> Propellants = new();
  }

  private sealed class PropellantEntry {
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
    _novaFrames.Clear();
    if (v == null || v.parts == null) return true;
    _novaVesselId = v.id.ToString("D");

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

      // Engine.EngineStatus is virtual — NuclearEngine overrides to
      // report shutdown/idle/burning based on reactor state (Cold ->
      // shutdown, Warming/Idle/Cooling -> idle, Throttled -> burning/
      // flameout). Detailed reactor state travels on the per-part
      // NovaPartTopic "N" frame.
      byte status = es.Engine.EngineStatus;
      // Wire throttle is the reactor-gated thrust output fraction.
      // For plain engines, ThrustOutputFraction == NormalizedOutput,
      // so this matches the prior `Throttle * NormalizedOutput` shape
      // (without the squaring quirk): just the achieved thrust fraction.
      float throttle = status == 0
        ? Mathf.Clamp01((float)es.Engine.ThrustOutputFraction)
        : 0f;

      var novaProps = new List<NovaPropellantFrame>(es.Propellants.Count);
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
        novaProps.Add(new NovaPropellantFrame {
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

      _novaFrames.Add(new NovaEngineFrame {
        Id = es.Part.flightID.ToString(),
        MapX = local.x,
        MapY = local.z,
        Status = status,
        Throttle = throttle,
        MaxThrust = (float)es.Engine.Thrust,
        Isp = (float)es.Engine.Isp,
        CrossfeedPartIds = es.CrossfeedPartIds,
        Propellants = novaProps,
      });

      // DG still owns change detection; feed its scratch with the
      // same shape it expects so HasMaterialChange / MarkDirty fire
      // correctly. The DG-side wire emission is overridden by our
      // WriteData below, so this list never reaches the wire — it's
      // pure broadcaster bookkeeping.
      scratch.Add(new EngineFrame {
        Id = es.Part.flightID.ToString(),
        MapX = local.x,
        MapY = local.z,
        Status = status,
        Throttle = throttle,
        MaxThrust = (float)es.Engine.Thrust,
        Isp = (float)es.Engine.Isp,
        CrossfeedPartIds = es.CrossfeedPartIds,
        Propellants = new List<PropellantFrame>(0),
      });
    }
    return true;
  }

  public override void WriteData(StringBuilder sb) {
    Nova.Core.Telemetry.EngineFrameFormatter.Write(sb, _novaVesselId, _novaFrames);
  }

  private void RebuildStructure(Vessel v) {
    _structure.Clear();
    if (v == null || v.parts == null) return;

    var vm = v.FindVesselModuleImplementing<NovaVesselModule>();
    var staging = vm?.Virtual?.Systems?.Staging;
    if (staging == null) {
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

      // Fuel-pool reach: union of reachable staging nodes across this
      // engine's propellants. Using node IDs (rather than part IDs)
      // sidesteps the node↔parts mapping — the HUD only consumes this
      // list as an opaque grouping signature, and node identity is
      // exactly what defines the visible fuel pool.
      var reachedNodes = new HashSet<StagingFlowSystem.Node>();
      foreach (var prop in engine.Propellants) {
        foreach (var n in staging.ReachableNodes(engine.Node, prop.Resource))
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
          Name = prop.Resource.Name,
          Abbreviation = prop.Resource.Abbreviation,
        };
        pe.Buffers.AddRange(staging.ReachableBuffers(engine.Node, prop.Resource));
        es.Propellants.Add(pe);
      }

      _structure.Add(es);
    }
  }

}
