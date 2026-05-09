using System.Collections.Generic;
using Dragonglass.Telemetry.Topics;
using Nova.Components;
using Nova.Core.Components.Propulsion;
using Nova.Core.Resources;
using UnityEngine;

namespace Nova.Telemetry;

// Nova's stage-topic override. Subclass of DG's StageTopic — wire
// shape, parts grouping, op handlers and lifecycle are inherited;
// the only override is the per-stage Δv/TWR scalar source.
//
// Stock StageTopic reads off `VesselDeltaV.OperatingStageInfo` —
// KSP's own dV simulator, which sees zero/garbage for vessels whose
// engines are NovaEngineModule (no `ModuleEngines.maxThrust` to
// drive its calc). This subclass runs `DeltaVSimulation` against
// Nova's `VirtualVessel` instead, building stage definitions from
// live `Part.inverseStage` values.
//
// The simulation is iterative and not free, so results are cached
// for a short window and recomputed on cache expiry. The stock
// Update path runs every frame, but the base class only emits a
// fresh wire frame when scalars cross epsilons — a stale cached
// number that hasn't moved produces zero wire traffic. The base
// also force-emits on staging transitions and structure events, so
// step responses still reach the UI promptly even if our cache is
// fresh.
public sealed class NovaStageTopic : StageTopic {

  // Recompute interval. 1 s gives ~1 Hz Δv updates, well below the
  // base topic's 10 Hz wire cadence — the throttle for change-driven
  // emit is HasMaterialChange, not this cache.
  private const float CacheTtlSeconds = 1.0f;

  private readonly List<StageScalar> _cached = new();
  private float _cachedAt = float.NegativeInfinity;
  private Vessel _cachedVessel;

  protected override void CollectStageScalars(Vessel v, VesselDeltaV vdv, List<StageScalar> scratch) {
    if (v == null) return;

    float now = Time.realtimeSinceStartup;
    if (v != _cachedVessel || now - _cachedAt >= CacheTtlSeconds) {
      Recompute(v);
      _cachedVessel = v;
      _cachedAt = now;
    }

    for (int i = 0; i < _cached.Count; i++) scratch.Add(_cached[i]);
  }

  private void Recompute(Vessel v) {
    _cached.Clear();

    var vesselModule = v.FindVesselModuleImplementing<NovaVesselModule>();
    var virt = vesselModule?.Virtual;
    if (virt == null) return;

    var stages = BuildStageDefinitions(v);
    if (stages.Count == 0) return;

    var results = DeltaVSimulation.Run(virt, stages, time: Planetarium.GetUniversalTime())
                  ?? new List<DeltaVSimulation.StageResult>();

    // Local gravity at the active vessel — converts thrust + mass
    // into an actual TWR rather than a sea-level approximation.
    // Falls back to standard g₀ in the rare case the field isn't
    // populated yet (vessel still settling on rails).
    double g = v.gravityForPos.magnitude;
    if (g <= 0.0) g = 9.80665;

    var resultByStage = new Dictionary<int, DeltaVSimulation.StageResult>(results.Count);
    foreach (var r in results) resultByStage[r.InverseStageIndex] = r;

    // Emit one scalar per defined stage. Decoupler-only stages don't
    // burn any propellant so DeltaVSimulation returns no result for
    // them; we still need a scalar so the base topic produces a
    // StageFrame and the UI can render the decoupler in its own row.
    foreach (var stage in stages) {
      if (resultByStage.TryGetValue(stage.InverseStageIndex, out var r)) {
        double weightN = r.StartMass * g;
        float twr = weightN > 0
          ? (float)(r.Thrust * 1000.0 / weightN)
          : 0f;
        _cached.Add(new StageScalar {
          Stage = r.InverseStageIndex,
          DeltaVActual = r.DeltaV,
          TwrActual = twr,
        });
      } else {
        _cached.Add(new StageScalar {
          Stage = stage.InverseStageIndex,
          DeltaVActual = 0,
          TwrActual = 0,
        });
      }
    }
  }

  // Build the firing-order list of stages from the vessel's current
  // part assignments. Engines fire and decouplers separate by
  // `Part.inverseStage`, with the highest-numbered stage going first
  // (KSP's convention — space-bar activates the top of the list).
  // We sort descending so DeltaVSimulation processes them in that
  // order; the inverseStage value rides through as the stage label
  // so the scalars line up with the parts list the base topic
  // builds (which also keys on inverseStage).
  private static List<DeltaVSimulation.StageDefinition> BuildStageDefinitions(Vessel v) {
    var byStage = new Dictionary<int, DeltaVSimulation.StageDefinition>();
    for (int i = 0; i < v.parts.Count; i++) {
      Part p = v.parts[i];
      if (p == null) continue;
      int stage = p.inverseStage;
      if (stage < 0) continue;

      if (!byStage.TryGetValue(stage, out var def)) {
        def = new DeltaVSimulation.StageDefinition {
          InverseStageIndex = stage,
        };
        byStage[stage] = def;
      }

      if (p.FindModuleImplementing<NovaEngineModule>() != null) {
        def.EnginePartIds.Add(p.persistentId);
      }
      if (p.FindModuleImplementing<NovaDecouplerModule>() != null) {
        def.DecouplerPartIds.Add(p.persistentId);
      }
    }

    var ordered = new List<DeltaVSimulation.StageDefinition>(byStage.Values);
    ordered.Sort((a, b) => b.InverseStageIndex.CompareTo(a.InverseStageIndex));
    return ordered;
  }
}
