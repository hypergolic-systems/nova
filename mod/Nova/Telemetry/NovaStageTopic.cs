using System.Collections.Generic;
using System.Text;
using Dragonglass.Telemetry.Topics;
using KSP.UI.Screens;
using Nova.Components;
using Nova.Core.Components;
using Nova.Core.Components.Propulsion;
using Nova.Core.Resources;
using UnityEngine;
using NovaStageFrame = Nova.Core.Telemetry.StageFrame;
using NovaStagePartFrame = Nova.Core.Telemetry.StagePartFrame;
using NovaStageFormatter = Nova.Core.Telemetry.StageFrameFormatter;

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
// Scope. Both flight and editor scenes are handled here. In flight
// we read NovaVesselModule.Virtual; in the editor we build a
// transient VirtualVessel from EditorLogic.fetch.ship via
// EditorVirtualVesselBuilder. The wire output is identical in both
// scenes — DG's StagingStack is scene-agnostic.
//
// The simulation is iterative and not free, so results are cached
// for a short window and recomputed on cache expiry. The stock
// Update path runs every frame, but the base class only emits a
// fresh wire frame when scalars cross epsilons — a stale cached
// number that hasn't moved produces zero wire traffic. The base
// also force-emits on staging transitions and structure events, so
// step responses still reach the UI promptly even if our cache is
// fresh. In the editor, onEditorShipModified additionally invalidates
// the cached transient vessel so the next sample rebuilds it from
// the modified ship.
public sealed class NovaStageTopic : StageTopic {

  // Recompute interval. 1 s gives ~1 Hz Δv updates, well below the
  // base topic's 10 Hz wire cadence — the throttle for change-driven
  // emit is HasMaterialChange, not this cache.
  private const float CacheTtlSeconds = 1.0f;

  private readonly List<StageScalar> _cached = new();
  private float _cachedAt = float.NegativeInfinity;
  private Vessel _cachedVessel;
  private bool _editorDirty = true;

  // Nova-owned wire cache. CollectStageScalars rebuilds these alongside
  // the DG-typed `_cached` scalars; WriteData reads from them.
  private readonly List<NovaStageFrame> _novaFrames = new();
  private string _novaVesselId = "";
  private int _novaCurrentStage = -1;

  protected override void OnEnable() {
    base.OnEnable();
    GameEvents.onEditorShipModified.Add(OnEditorShipModified);
    GameEvents.onEditorLoad.Add(OnEditorLoad);
    GameEvents.onEditorRestart.Add(OnEditorReset);
    GameEvents.onEditorStarted.Add(OnEditorReset);
  }

  protected override void OnDisable() {
    base.OnDisable();
    GameEvents.onEditorShipModified.Remove(OnEditorShipModified);
    GameEvents.onEditorLoad.Remove(OnEditorLoad);
    GameEvents.onEditorRestart.Remove(OnEditorReset);
    GameEvents.onEditorStarted.Remove(OnEditorReset);
  }

  private void OnEditorShipModified(ShipConstruct _) => _editorDirty = true;
  private void OnEditorLoad(ShipConstruct _, CraftBrowserDialog.LoadType __) => _editorDirty = true;
  private void OnEditorReset() => _editorDirty = true;

  protected override void CollectStageScalars(Vessel v, VesselDeltaV vdv, List<StageScalar> scratch) {
    if (HighLogic.LoadedScene == GameScenes.EDITOR) {
      CollectEditorStageScalars(scratch);
      return;
    }
    if (v == null) return;

    float now = Time.realtimeSinceStartup;
    if (v != _cachedVessel || now - _cachedAt >= CacheTtlSeconds) {
      RecomputeFlight(v);
      _cachedVessel = v;
      _cachedAt = now;
    }

    _novaVesselId = v.id.ToString("D");
    _novaCurrentStage = v.currentStage;
    RebuildNovaFrames(v.Parts);

    for (int i = 0; i < _cached.Count; i++) scratch.Add(_cached[i]);
  }

  private void CollectEditorStageScalars(List<StageScalar> scratch) {
    float now = Time.realtimeSinceStartup;
    if (_editorDirty || now - _cachedAt >= CacheTtlSeconds) {
      RecomputeEditor();
      _editorDirty = false;
      _cachedVessel = null;
      _cachedAt = now;
    }
    _novaVesselId = "editor";
    _novaCurrentStage = -1;
    var ship = EditorLogic.fetch?.ship;
    RebuildNovaFrames(ship?.parts);
    for (int i = 0; i < _cached.Count; i++) scratch.Add(_cached[i]);
  }

  // Nova-owned wire emission. The DG base's WriteData reads its own
  // private `_stages`; we override here to emit from our own cache
  // through the shared Nova.Core formatter. HandleOp is *not*
  // overridden — DG owns the inbound stage-edit ops (movePart,
  // moveStage, setHighlightParts, …) and those keep working.
  public override void WriteData(StringBuilder sb) {
    NovaStageFormatter.Write(sb, _novaVesselId, _novaCurrentStage, _novaFrames);
  }

  // Combine the cached scalars with a parts-by-stage bucketing of the
  // live parts list to produce the Nova-owned StageFrame[]. The
  // bucketing logic mirrors stock DG StageTopic.BuildPartsByStage so
  // the wire shape stays identical to what DG would emit. Sim-side
  // bucketing is different (no KSP modules), but the resulting frame
  // shape is the same.
  private void RebuildNovaFrames(IList<Part> parts) {
    _novaFrames.Clear();
    var partsByStage = parts != null ? BuildPartsByStage(parts) : null;

    for (int i = 0; i < _cached.Count; i++) {
      var sc = _cached[i];
      var sf = new NovaStageFrame {
        Stage = sc.Stage,
        DeltaVActual = sc.DeltaVActual,
        TwrActual = sc.TwrActual,
      };
      if (partsByStage != null && partsByStage.TryGetValue(sc.Stage, out var bucket)) {
        sf.Parts = bucket;
      }
      _novaFrames.Add(sf);
    }
  }

  // Build a stage→parts dictionary for the vessel's icon-worthy parts,
  // pre-sorted into presentation order (kind priority, then
  // persistentId ascending). Lifted from
  // `Dragonglass.Telemetry.Topics.StageTopic.BuildPartsByStage`;
  // duplicated here so Nova owns the bytes on the wire end-to-end.
  // Symmetry dedupe: representative is the cousin with the smallest
  // persistentId among cousins sharing the same stage; the others ride
  // in `CousinsInStage`.
  private static Dictionary<int, List<NovaStagePartFrame>> BuildPartsByStage(IList<Part> parts) {
    var byStage = new Dictionary<int, List<NovaStagePartFrame>>();
    for (int i = 0; i < parts.Count; i++) {
      Part p = parts[i];
      if (p == null) continue;
      if (!p.hasStagingIcon) continue;
      if (!p.stagingOn) continue;
      if (!IsRepresentativeForStage(p)) continue;
      List<string> cousins = BuildCousinsInStage(p);

      if (!byStage.TryGetValue(p.inverseStage, out var bucket)) {
        bucket = new List<NovaStagePartFrame>();
        byStage[p.inverseStage] = bucket;
      }
      bucket.Add(new NovaStagePartFrame {
        Kind = ClassifyKind(p),
        PersistentId = p.persistentId.ToString(),
        IconName = p.stagingIcon,
        CousinsInStage = cousins,
      });
    }
    foreach (var bucket in byStage.Values) bucket.Sort(ComparePresentation);
    return byStage;
  }

  private static bool IsRepresentativeForStage(Part p) {
    var cousins = p.symmetryCounterparts;
    if (cousins == null || cousins.Count == 0) return true;
    uint minId = p.persistentId;
    int stage = p.inverseStage;
    for (int i = 0; i < cousins.Count; i++) {
      var c = cousins[i];
      if (c == null) continue;
      if (c.inverseStage != stage) continue;
      if (c.persistentId < minId) return false;
    }
    return true;
  }

  private static List<string> BuildCousinsInStage(Part p) {
    var cousins = p.symmetryCounterparts;
    if (cousins == null || cousins.Count == 0) return _emptyCousins;
    int stage = p.inverseStage;
    var ids = new List<uint>();
    for (int i = 0; i < cousins.Count; i++) {
      var c = cousins[i];
      if (c == null) continue;
      if (c.inverseStage != stage) continue;
      ids.Add(c.persistentId);
    }
    if (ids.Count == 0) return _emptyCousins;
    ids.Sort();
    var result = new List<string>(ids.Count);
    for (int i = 0; i < ids.Count; i++) result.Add(ids[i].ToString());
    return result;
  }

  private static readonly List<string> _emptyCousins = new(0);

  private static int KindPriority(string kind) {
    if (kind == NovaStageFormatter.KindDecoupler) return 0;
    if (kind == NovaStageFormatter.KindEngine)    return 1;
    if (kind == NovaStageFormatter.KindParachute) return 2;
    if (kind == NovaStageFormatter.KindClamp)     return 3;
    return 4;
  }

  private static int ComparePresentation(NovaStagePartFrame a, NovaStagePartFrame b) {
    int pa = KindPriority(a.Kind);
    int pb = KindPriority(b.Kind);
    if (pa != pb) return pa - pb;
    ulong.TryParse(a.PersistentId, out var ia);
    ulong.TryParse(b.PersistentId, out var ib);
    return ia < ib ? -1 : (ia > ib ? 1 : 0);
  }

  private static string ClassifyKind(Part p) {
    if (p.FindModuleImplementing<ModuleEngines>() != null) return NovaStageFormatter.KindEngine;
    if (p.FindModuleImplementing<ModuleDecouplerBase>() != null) return NovaStageFormatter.KindDecoupler;
    if (p.FindModuleImplementing<ModuleParachute>() != null) return NovaStageFormatter.KindParachute;
    if (p.FindModuleImplementing<LaunchClamp>() != null) return NovaStageFormatter.KindClamp;
    var dock = p.FindModuleImplementing<ModuleDockingNode>();
    if (dock != null && dock.StagingEnabled()) return NovaStageFormatter.KindDecoupler;
    return NovaStageFormatter.KindOther;
  }

  private void RecomputeFlight(Vessel v) {
    _cached.Clear();
    var vesselModule = v.FindVesselModuleImplementing<NovaVesselModule>();
    var virt = vesselModule?.Virtual;
    if (virt == null) return;

    var stages = BuildStageDefinitions(v.parts);
    if (stages.Count == 0) return;

    // Local gravity at the active vessel — converts thrust + mass
    // into an actual TWR rather than a sea-level approximation.
    // Falls back to standard g₀ in the rare case the field isn't
    // populated yet (vessel still settling on rails).
    double g = v.gravityForPos.magnitude;
    if (g <= 0.0) g = 9.80665;

    Simulate(virt, stages, Planetarium.GetUniversalTime(), g);
  }

  private void RecomputeEditor() {
    _cached.Clear();
    var ship = EditorLogic.fetch?.ship;
    if (ship == null || ship.parts == null || ship.parts.Count == 0) return;

    var stages = BuildStageDefinitions(ship.parts);
    if (stages.Count == 0) return;

    double ut = Planetarium.fetch != null ? Planetarium.GetUniversalTime() : 0.0;
    var virt = EditorVirtualVesselBuilder.Build(ship, ut);

    // Editor TWR is reported against Kerbin sea-level g₀. There's no
    // "where will this fly?" hint in the editor; KSP's stock VAB does
    // the same.
    Simulate(virt, stages, ut, 9.80665);
  }

  private void Simulate(VirtualVessel virt, List<DeltaVSimulation.StageDefinition> stages, double ut, double g) {
    var results = DeltaVSimulation.Run(virt, stages, time: ut)
                  ?? new List<DeltaVSimulation.StageResult>();

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

  // Build the firing-order list of stages from the parts' current
  // assignments. Engines fire and decouplers separate by
  // `Part.inverseStage`, with the highest-numbered stage going first
  // (KSP's convention — space-bar activates the top of the list).
  // We sort descending so DeltaVSimulation processes them in that
  // order; the inverseStage value rides through as the stage label
  // so the scalars line up with the parts list the base topic
  // builds (which also keys on inverseStage).
  private static List<DeltaVSimulation.StageDefinition> BuildStageDefinitions(IList<Part> parts) {
    var byStage = new Dictionary<int, DeltaVSimulation.StageDefinition>();
    for (int i = 0; i < parts.Count; i++) {
      Part p = parts[i];
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
