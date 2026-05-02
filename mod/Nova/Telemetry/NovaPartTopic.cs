using System.Collections.Generic;
using System.Linq;
using System.Text;
using Dragonglass.Telemetry.Topics;
using Nova.Components;
using Nova.Core.Components;
using Nova.Core.Components.Control;
using Nova.Core.Components.Electrical;
using Nova.Core.Components.Propulsion;
using Nova.Core.Components.Science;
using Nova.Core.Persistence.Protos;
using Nova.Core.Resources;
using Nova.Core.Science;
using UnityEngine;
// Disambiguate against UnityEngine.Light.
using NovaLight = Nova.Core.Components.Electrical.Light;

namespace Nova.Telemetry;

// Per-part state topic: current rates and saturation for whatever
// Nova virtual components live on the part. Marked dirty after each
// solver tick by NovaVesselModule; the broadcaster's 10 Hz cadence
// gates the actual emission rate.
//
// MonoBehaviour attached to the Part's GameObject by
// NovaSubscriptionManager. Lifetime tied to the Part — destroy on
// stage-off, decouple, or unload happens automatically.
//
// Wire format (positional array, single-char component kind prefix
// matching the convention used by stock Dragonglass PartTopic):
//   [partId,
//    [ [resourceId, rate, satisfaction], ... ],
//    [ [kind, ...], ... ]
//   ]
//
// Resource frames (one per Buffer on a part — TankVolume tanks and
// Battery cells both contribute):
//   [resourceName, amount, capacity, currentRate]
//
// Component frames. Every numeric is a physical observable —
// rates in W, fractions in 0..1 (for fills/SoC), absolutes in their
// natural unit. No solver-internal "activity" leaks into the wire;
// callers that want "current draw" get it directly.
//   ["S", currentEcRate, maxEcRate, deployed, sunlit, retractable]  SolarPanel
//   ["B", soc(0..1), capacity, currentRate]                         Battery
//   ["W", motorRate, busRate, bufferFraction, refillActive]          ReactionWheel
//   ["L", currentRate]                                              Light
//   ["E", alternatorMaxRate, alternatorRate]                        Engine
//   ["T", volume]                                                   TankVolume
//   ["F", currentEcOutput, maxEcOutput, isActive, validUntilSec,
//          lh2ManifoldFraction, loxManifoldFraction, refillActive]   FuelCell
//   ["C", idleRate, testLoadRate, testLoadMaxRate, testLoadActive]   Command
//
// `deployed` / `sunlit` / `retractable` are encoded as `0`/`1`
// rather than literal JSON booleans (consistent with the rest of
// the positional payload).
//
// Inbound ops (UI → mod, dispatched via Topic.HandleOp):
//   "setSolarDeployed" [bool]  — extend or retract the part's
//                                deployable solar panel. No-op if
//                                the part has no NovaDeployableSolar
//                                module, or if the requested state
//                                isn't reachable (already animating,
//                                already in the requested state, or
//                                trying to retract a non-retractable
//                                panel in flight). Per-panel only —
//                                symmetry counterparts are NOT walked,
//                                so the UI sends one op per panel it
//                                wants to deploy.
//   "setTankConfig" [string]   — replace the part's tank loadout with
//                                the named TankPresets preset, built
//                                fresh against the current Volume.
//                                Editor-only — rejected outside
//                                GameScenes.EDITOR. No-op if the part
//                                has no NovaTankModule or the preset
//                                id is unknown.
public sealed class NovaPartTopic : Topic {
  private const string LogPrefix = "[Nova/Telemetry] ";

  private Part _part;
  private string _name;

  // Index keyed by part id. NovaVesselModule pushes dirty marks into
  // the index post-tick instead of holding component references.
  private static readonly Dictionary<uint, NovaPartTopic> _byPart
      = new Dictionary<uint, NovaPartTopic>();

  public override string Name => _name;

  protected override void OnEnable() {
    _part = GetComponent<Part>();
    if (_part == null) {
      Debug.LogWarning(LogPrefix + "NovaPartTopic attached to non-Part GameObject; disabling");
      enabled = false;
      return;
    }
    _name = "NovaPart/" + _part.persistentId;
    _byPart[_part.persistentId] = this;
    base.OnEnable();
    MarkDirty();
  }

  protected override void OnDisable() {
    base.OnDisable();
    if (_part != null) _byPart.Remove(_part.persistentId);
  }

  /// <summary>
  /// Mark dirty for a specific part. Called by NovaVesselModule after
  /// each solver tick for every part it owns.
  /// </summary>
  public static void MarkPartDirty(uint partPersistentId) {
    if (_byPart.TryGetValue(partPersistentId, out var topic) && topic != null) {
      topic.MarkDirty();
    }
  }

  /// <summary>
  /// Mark every attached part topic dirty. Cheaper than touching the
  /// whole vessel's part list when the vessel module knows it just
  /// solved everything.
  /// </summary>
  public static void MarkAllDirty() {
    foreach (var t in _byPart.Values) {
      if (t != null) t.MarkDirty();
    }
  }

  // Inbound op router — see file header for the supported envelope.
  // Runs on Unity's main thread via OpDispatcher, so direct calls
  // into KSP PartModule state are safe.
  public override void HandleOp(string op, List<object> args) {
    switch (op) {
      case "setSolarDeployed": {
        if (args == null || args.Count < 1 || !(args[0] is bool deployed)) {
          Debug.LogWarning(LogPrefix + Name + " setSolarDeployed: expected [bool]");
          return;
        }
        var module = _part?.FindModuleImplementing<NovaDeployableSolarModule>();
        if (module == null) return;
        if (deployed) module.Extend();
        else module.Retract();
        return;
      }
      case "setExperimentEnabled": {
        // args = [experimentId: string, enabled: bool]
        if (args == null || args.Count < 2
            || !(args[0] is string experimentId)
            || !(args[1] is bool enabled)) {
          Debug.LogWarning(LogPrefix + Name + " setExperimentEnabled: expected [string, bool]");
          return;
        }
        var vesselModule = _part?.vessel?.GetComponent<NovaVesselModule>();
        var thermometer = vesselModule?.Virtual?.GetComponents(_part.persistentId)
            .OfType<Thermometer>()
            .FirstOrDefault();
        if (thermometer == null) return;
        if (experimentId == AtmosphericProfileExperiment.ExperimentId) {
          // Rising edge: discard any prior in-progress file for the
          // current layer so the fresh observation starts from a clean
          // slate. Files represent unbroken periods.
          if (enabled && !thermometer.AtmEnabled) {
            var ctx = thermometer.Vessel?.Context;
            if (ctx != null) {
              var layer = AtmosphericProfileExperiment.LayerAt(ctx.BodyName, ctx.Altitude);
              if (layer != null) {
                var subject = new SubjectKey(
                    AtmosphericProfileExperiment.ExperimentId, ctx.BodyName, layer);
                thermometer.DiscardFile(subject.ToString());
              }
            }
          }
          thermometer.AtmEnabled = enabled;
        } else if (experimentId == LongTermStudyExperiment.ExperimentId) {
          if (enabled && !thermometer.LtsEnabled) {
            var ctx = thermometer.Vessel?.Context;
            if (ctx != null && ctx.Situation != Situation.None && ctx.BodyYearSeconds > 0) {
              double ut = Planetarium.GetUniversalTime();
              var subject = LongTermStudyExperiment.SubjectFor(
                  ctx.BodyName, ctx.Situation, ut, ctx.BodyYearSeconds);
              thermometer.DiscardFile(subject.ToString());
            }
          }
          thermometer.LtsEnabled = enabled;
        } else {
          Debug.LogWarning(LogPrefix + Name + " setExperimentEnabled: unknown experiment '" + experimentId + "'");
          return;
        }
        vesselModule.Virtual.Invalidate();
        MarkDirty();
        return;
      }
      case "setCommandTestLoad": {
        if (args == null || args.Count < 1 || !(args[0] is bool active)) {
          Debug.LogWarning(LogPrefix + Name + " setCommandTestLoad: expected [bool]");
          return;
        }
        // Reach through the live VirtualVessel to find the part's
        // Command component. Editor scope has no Virtual; rejected.
        var vesselModule = _part?.vessel?.GetComponent<NovaVesselModule>();
        var cmp = vesselModule?.Virtual?.GetComponents(_part.persistentId)
            .OfType<Command>()
            .FirstOrDefault();
        if (cmp == null) return;
        cmp.TestLoadActive = active;
        vesselModule.Virtual.Invalidate();
        MarkDirty();
        return;
      }
      case "setTankConfig": {
        if (args == null || args.Count < 1 || !(args[0] is string presetId)) {
          Debug.LogWarning(LogPrefix + Name + " setTankConfig: expected [string]");
          return;
        }
        if (HighLogic.LoadedScene != GameScenes.EDITOR) {
          Debug.Log(LogPrefix + Name + " setTankConfig rejected outside editor");
          return;
        }
        var module = _part?.FindModuleImplementing<NovaTankModule>();
        if (module?.TankVolume == null) return;
        var preset = TankPresets.GetById(presetId);
        if (preset == null) {
          Debug.LogWarning(LogPrefix + Name + " setTankConfig: unknown preset '" + presetId + "'");
          return;
        }
        module.TankVolume.Reconfigure(preset.Build(module.TankVolume.Volume));
        MarkDirty();
        return;
      }
      default:
        base.HandleOp(op, args);
        return;
    }
  }

  public override void WriteData(StringBuilder sb) {
    JsonWriter.Begin(sb, '[');
    bool first = true;

    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteUintAsString(sb, _part.persistentId);

    var components = ResolveComponents();

    JsonWriter.Sep(sb, ref first);
    WriteResources(sb, components);

    JsonWriter.Sep(sb, ref first);
    WriteComponents(sb, components);

    JsonWriter.End(sb, ']');
  }

  // In flight, components live on NovaVesselModule.Virtual (the solver
  // graph). In editor there's no vessel and no Virtual — read directly
  // from the live NovaPartModule.Components list, populated by
  // OnStartEditor from the prefab MODULE config (and mutated by
  // setTankConfig). Returns an empty enumerable for parts with no
  // Nova modules.
  private IEnumerable<VirtualComponent> ResolveComponents() {
    if (_part == null) return Enumerable.Empty<VirtualComponent>();
    if (_part.vessel != null) {
      var vm = _part.vessel.GetComponent<NovaVesselModule>();
      if (vm?.Virtual != null) return vm.Virtual.GetComponents(_part.persistentId);
    }
    var modules = _part.Modules?.OfType<NovaPartModule>();
    if (modules == null) return Enumerable.Empty<VirtualComponent>();
    return modules
      .Where(m => m.Components != null)
      .SelectMany(m => m.Components);
  }

  private void WriteResources(StringBuilder sb, IEnumerable<VirtualComponent> components) {
    JsonWriter.Begin(sb, '[');
    bool first = true;
    foreach (var c in components) {
      foreach (var buf in EnumerateBuffers(c)) {
        if (buf == null || buf.Capacity <= 0) continue;
        JsonWriter.Sep(sb, ref first);
        WriteResource(sb, buf);
      }
    }
    JsonWriter.End(sb, ']');
  }

  // Buffer-bearing components contribute their buffers to the part's
  // resource list. Add new cases here when new component kinds gain
  // buffers; the Resource view will pick them up for free.
  private static IEnumerable<Buffer> EnumerateBuffers(VirtualComponent c) {
    switch (c) {
      case Battery b:
        yield return b.Buffer;
        break;
      case TankVolume t:
        foreach (var tank in t.Tanks) yield return tank;
        break;
    }
  }

  private static void WriteResource(StringBuilder sb, Buffer buf) {
    JsonWriter.Begin(sb, '[');
    bool first = true;
    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteString(sb, buf.Resource?.Name ?? "");
    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteDouble(sb, buf.Contents);
    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteDouble(sb, buf.Capacity);
    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteDouble(sb, buf.Rate);
    JsonWriter.End(sb, ']');
  }

  private void WriteComponents(StringBuilder sb, IEnumerable<VirtualComponent> components) {
    JsonWriter.Begin(sb, '[');
    bool first = true;
    var kspVessel = _part?.vessel;
    foreach (var c in components) {
      if (!TryWriteComponent(sb, c, kspVessel, ref first)) {
        // Unhandled kind — silently skip rather than emit an
        // un-decodable frame. New kinds get a case here + a TS
        // tuple in nova-topics.ts.
      }
    }
    JsonWriter.End(sb, ']');
  }

  private static bool TryWriteComponent(StringBuilder sb, VirtualComponent c, Vessel kspVessel, ref bool first) {
    switch (c) {
      case SolarPanel solar: {
        JsonWriter.Sep(sb, ref first);
        JsonWriter.Begin(sb, '[');
        bool f = true;
        WriteKind(sb, "S", ref f);
        WriteNum(sb, solar.CurrentRate, ref f);
        WriteNum(sb, solar.IsSunlit ? solar.EffectiveRate : 0.0, ref f);
        WriteBit(sb, solar.IsDeployed, ref f);
        WriteBit(sb, solar.IsSunlit, ref f);
        WriteBit(sb, solar.IsRetractable, ref f);
        JsonWriter.End(sb, ']');
        return true;
      }
      case Battery battery: {
        JsonWriter.Sep(sb, ref first);
        JsonWriter.Begin(sb, '[');
        bool f = true;
        WriteKind(sb, "B", ref f);
        var soc = battery.Buffer.Capacity > 0
            ? battery.Buffer.Contents / battery.Buffer.Capacity
            : 0.0;
        WriteNum(sb, soc, ref f);
        WriteNum(sb, battery.Buffer.Capacity, ref f);
        WriteNum(sb, battery.Buffer.Rate, ref f);
        JsonWriter.End(sb, ']');
        return true;
      }
      case ReactionWheel wheel: {
        JsonWriter.Sep(sb, ref first);
        JsonWriter.Begin(sb, '[');
        bool f = true;
        WriteKind(sb, "W", ref f);
        // motorRate / busRate frame the wheel's energy balance
        // honestly: motor draw can be supplied from the buffer, the
        // bus, or both, and the UI needs both halves to show the
        // signed buffer flow (= busRate − motorRate) without solver
        // internals.
        WriteNum(sb, wheel.CurrentDrain, ref f);
        WriteNum(sb, wheel.CurrentRefill, ref f);
        WriteNum(sb, wheel.Buffer?.FillFraction ?? 1.0, ref f);
        WriteBit(sb, wheel.RefillActive, ref f);
        JsonWriter.End(sb, ']');
        return true;
      }
      case NovaLight light: {
        JsonWriter.Sep(sb, ref first);
        JsonWriter.Begin(sb, '[');
        bool f = true;
        WriteKind(sb, "L", ref f);
        WriteNum(sb, light.Rate * light.Activity, ref f);
        JsonWriter.End(sb, ']');
        return true;
      }
      case Engine engine: {
        JsonWriter.Sep(sb, ref first);
        JsonWriter.Begin(sb, '[');
        bool f = true;
        WriteKind(sb, "E", ref f);
        WriteNum(sb, engine.AlternatorRate, ref f);
        WriteNum(sb, engine.AlternatorOutput, ref f);
        JsonWriter.End(sb, ']');
        return true;
      }
      case TankVolume tank: {
        JsonWriter.Sep(sb, ref first);
        JsonWriter.Begin(sb, '[');
        bool f = true;
        WriteKind(sb, "T", ref f);
        WriteNum(sb, tank.Volume, ref f);
        JsonWriter.End(sb, ']');
        return true;
      }
      case Command command: {
        if (command.IdleDraw <= 0 && command.TestLoadRate <= 0) return false;
        JsonWriter.Sep(sb, ref first);
        JsonWriter.Begin(sb, '[');
        bool f = true;
        WriteKind(sb, "C", ref f);
        WriteNum(sb, command.IdleDraw * command.IdleActivity, ref f);
        WriteNum(sb, command.TestLoadRate * command.TestLoadActivity, ref f);
        WriteNum(sb, command.TestLoadRate, ref f);
        WriteBit(sb, command.TestLoadActive, ref f);
        JsonWriter.End(sb, ']');
        return true;
      }
      case FuelCell fuelCell: {
        JsonWriter.Sep(sb, ref first);
        JsonWriter.Begin(sb, '[');
        bool f = true;
        WriteKind(sb, "F", ref f);
        WriteNum(sb, fuelCell.CurrentOutput, ref f);
        WriteNum(sb, fuelCell.EcOutput, ref f);
        WriteBit(sb, fuelCell.IsActive, ref f);
        WriteNum(sb, fuelCell.ValidUntilSeconds, ref f);
        WriteNum(sb, fuelCell.Lh2Manifold.FillFraction, ref f);
        WriteNum(sb, fuelCell.LoxManifold.FillFraction, ref f);
        WriteBit(sb, fuelCell.RefillActive, ref f);
        JsonWriter.End(sb, ']');
        return true;
      }
      case Thermometer thermometer: {
        // IN — capability descriptor.
        JsonWriter.Sep(sb, ref first);
        JsonWriter.Begin(sb, '[');
        bool f = true;
        WriteKind(sb, "IN", ref f);
        JsonWriter.Sep(sb, ref f);
        JsonWriter.WriteString(sb, thermometer.InstrumentName ?? "");
        JsonWriter.Sep(sb, ref f);
        JsonWriter.Begin(sb, '[');
        bool firstExp = true;
        foreach (var expId in Thermometer.SupportedExperiments) {
          JsonWriter.Sep(sb, ref firstExp);
          JsonWriter.WriteString(sb, expId);
        }
        JsonWriter.End(sb, ']');
        JsonWriter.End(sb, ']');

        // Build a subjectId → max-fidelity dict once across every
        // DataStorage on the vessel; reused by both EXA and EXL.
        var savedLocal = BuildLocalFidelityIndex(thermometer);

        WriteAtmExperimentFrame(sb, thermometer, kspVessel, savedLocal, ref first);
        WriteLtsExperimentFrame(sb, thermometer, kspVessel, savedLocal, ref first);
        return true;
      }
      case DataStorage storage: {
        JsonWriter.Sep(sb, ref first);
        JsonWriter.Begin(sb, '[');
        bool f = true;
        WriteKind(sb, "DS", ref f);
        WriteNum(sb, storage.UsedBytes, ref f);
        WriteNum(sb, storage.CapacityBytes, ref f);
        WriteNum(sb, storage.Files.Count, ref f);
        // Inline file list — typical storages hold tens of files; the
        // 10 Hz cadence keeps bandwidth modest. Wire shape mirrors
        // NovaScienceFileFrame: leading common fields, then direct-
        // measurement fields, then interpolated-measurement fields.
        // Interpolated files recompute fidelity from `now` against
        // start/end UT so the UI sees a live-climbing value.
        double simNow = Planetarium.GetUniversalTime();
        JsonWriter.Sep(sb, ref f);
        JsonWriter.Begin(sb, '[');
        bool firstFile = true;
        foreach (var file in storage.Files) {
          JsonWriter.Sep(sb, ref firstFile);
          JsonWriter.Begin(sb, '[');
          bool ff = true;
          JsonWriter.Sep(sb, ref ff);
          JsonWriter.WriteString(sb, file.SubjectId ?? "");
          JsonWriter.Sep(sb, ref ff);
          JsonWriter.WriteString(sb, file.ExperimentId ?? "");
          double liveFidelity = ComputeLiveFidelity(file, simNow);
          WriteNum(sb, liveFidelity, ref ff);
          WriteNum(sb, file.ProducedAt, ref ff);
          JsonWriter.Sep(sb, ref ff);
          JsonWriter.WriteString(sb, file.Instrument ?? "");
          WriteNum(sb, file.RecordedMinPressureAtm, ref ff);
          WriteNum(sb, file.RecordedMaxPressureAtm, ref ff);
          WriteNum(sb, file.LayerBottomPressureAtm, ref ff);
          WriteNum(sb, file.LayerTopPressureAtm, ref ff);
          WriteNum(sb, file.RecordedMinAltM, ref ff);
          WriteNum(sb, file.RecordedMaxAltM, ref ff);
          WriteNum(sb, file.StartUt, ref ff);
          WriteNum(sb, file.EndUt, ref ff);
          WriteNum(sb, file.SliceDurationSeconds, ref ff);
          JsonWriter.End(sb, ']');
        }
        JsonWriter.End(sb, ']');
        JsonWriter.End(sb, ']');
        return true;
      }
    }
    return false;
  }

  // --- Science experiment frames -----------------------------------

  // Walks the vessel for the first DataStorage with capacity for `fileSizeBytes`
  // and returns the host part's player-facing title. Empty string when none.
  // Mirrors `Thermometer.Deposit`'s `FirstOrDefault` selection so the UI's
  // "→ X" hint matches where files actually land.
  private static string ResolveDestinationStorage(Thermometer t, Vessel kspVessel, long fileSizeBytes) {
    if (t.Vessel == null || kspVessel == null) return "";
    foreach (var partId in t.Vessel.AllPartIds()) {
      foreach (var c in t.Vessel.GetComponents(partId)) {
        if (c is DataStorage storage && storage.CanDeposit(fileSizeBytes)) {
          var hostPart = kspVessel.parts.FirstOrDefault(p => p.persistentId == partId);
          return hostPart?.partInfo?.title ?? hostPart?.partName ?? "";
        }
      }
    }
    return "";
  }

  // Compute the live fidelity for a file. Direct-measurement files
  // store their fidelity directly (updated every tick by the experiment).
  // Interpolated files derive fidelity from start/end UT against now,
  // so save-cli / unloaded vessels / closed-tab UIs see the same value.
  private static double ComputeLiveFidelity(ScienceFile file, double nowUT) {
    if (file.SliceDurationSeconds > 0) {
      double covered = System.Math.Min(nowUT, file.EndUt) - file.StartUt;
      return System.Math.Min(1.0, System.Math.Max(0.0, covered / file.SliceDurationSeconds));
    }
    return file.Fidelity;
  }

  // Walks the vessel's storages for a file with the given subject id.
  // Returns the first match, or null. Used by the EXA / EXL emit
  // paths to read live recorded bounds (atm) or interpolation
  // endpoints (lts) directly from the canonical file record.
  private static ScienceFile FindFileForSubject(Thermometer t, string subjectId) {
    if (t.Vessel == null) return null;
    foreach (var s in t.Vessel.AllComponents().OfType<DataStorage>()) {
      var f = s.FindBySubject(subjectId);
      if (f != null) return f;
    }
    return null;
  }

  private static Dictionary<string, double> BuildLocalFidelityIndex(Thermometer t) {
    var idx = new Dictionary<string, double>();
    if (t.Vessel == null) return idx;
    foreach (var ds in t.Vessel.AllComponents().OfType<DataStorage>()) {
      foreach (var file in ds.Files) {
        if (string.IsNullOrEmpty(file.SubjectId)) continue;
        if (!idx.TryGetValue(file.SubjectId, out var existing) || file.Fidelity > existing) {
          idx[file.SubjectId] = file.Fidelity;
        }
      }
    }
    return idx;
  }

  private static void WriteAtmExperimentFrame(
      StringBuilder sb, Thermometer t, Vessel kspVessel,
      Dictionary<string, double> savedLocal, ref bool first) {
    string body = t.Vessel?.Context?.BodyName ?? "";
    double altitude = t.Vessel?.Context?.Altitude ?? 0;
    double currentPressure = t.Vessel?.Context?.StaticPressureAtm ?? 0;
    var layers = AtmosphericProfileExperiment.LayersFor(body);
    string currentLayer = AtmosphericProfileExperiment.LayerAt(body, altitude) ?? "";
    string destination = ResolveDestinationStorage(t, kspVessel, AtmosphericProfileExperiment.FileSizeBytes);

    // Recorded bounds come from the live file in storage for the
    // current layer. No file ⇒ no observation captured yet ⇒ zero
    // sentinels. Files persist across disable so the bounds remain
    // visible even when the experiment is currently OFF.
    var activeFile = !string.IsNullOrEmpty(currentLayer)
        ? FindFileForSubject(t, AtmosphericProfileExperiment.ExperimentId + "@" + body + ":" + currentLayer)
        : null;
    bool hasBracket = activeFile != null;
    double transitMinAlt      = hasBracket ? activeFile.RecordedMinAltM : 0;
    double transitMaxAlt      = hasBracket ? activeFile.RecordedMaxAltM : 0;
    double transitMinPressure = hasBracket ? activeFile.RecordedMinPressureAtm : 0;
    double transitMaxPressure = hasBracket ? activeFile.RecordedMaxPressureAtm : 0;

    JsonWriter.Sep(sb, ref first);
    JsonWriter.Begin(sb, '[');
    bool f = true;
    WriteKind(sb, "EXA", ref f);
    JsonWriter.Sep(sb, ref f);
    JsonWriter.WriteString(sb, AtmosphericProfileExperiment.ExperimentId);
    WriteBit(sb, t.AtmActive, ref f);
    // willComplete: atm-profile is a transit-trigger today (binary
    // seal on layer exit); always 1. Slot reserved for future
    // satisfaction-gated atm sealing.
    WriteBit(sb, true, ref f);
    WriteBit(sb, t.AtmEnabled, ref f);
    JsonWriter.Sep(sb, ref f);
    JsonWriter.WriteString(sb, currentLayer);
    WriteNum(sb, transitMinAlt, ref f);
    WriteNum(sb, transitMaxAlt, ref f);
    WriteNum(sb, transitMinPressure, ref f);
    WriteNum(sb, transitMaxPressure, ref f);
    WriteNum(sb, currentPressure, ref f);
    JsonWriter.Sep(sb, ref f);
    JsonWriter.WriteString(sb, destination);
    JsonWriter.Sep(sb, ref f);
    JsonWriter.WriteString(sb, body);
    WriteNum(sb, altitude, ref f);

    // layers: [[name, topAlt, bottomPressureAtm, topPressureAtm]…]
    JsonWriter.Sep(sb, ref f);
    JsonWriter.Begin(sb, '[');
    bool firstLayer = true;
    if (layers != null) {
      foreach (var l in layers) {
        JsonWriter.Sep(sb, ref firstLayer);
        JsonWriter.Begin(sb, '[');
        bool lf = true;
        JsonWriter.Sep(sb, ref lf);
        JsonWriter.WriteString(sb, l.name);
        WriteNum(sb, l.topAltMeters, ref lf);
        WriteNum(sb, l.bottomPressureAtm, ref lf);
        WriteNum(sb, l.topPressureAtm, ref lf);
        JsonWriter.End(sb, ']');
      }
    }
    JsonWriter.End(sb, ']');

    // savedLocal: [[layerName, fidelity]…] — only present layers
    JsonWriter.Sep(sb, ref f);
    JsonWriter.Begin(sb, '[');
    bool firstSaved = true;
    if (layers != null) {
      foreach (var l in layers) {
        var name = l.name;
        var subjectId = AtmosphericProfileExperiment.ExperimentId + "@" + body + ":" + name;
        if (!savedLocal.TryGetValue(subjectId, out var fid)) continue;
        JsonWriter.Sep(sb, ref firstSaved);
        JsonWriter.Begin(sb, '[');
        bool sf = true;
        JsonWriter.Sep(sb, ref sf);
        JsonWriter.WriteString(sb, name);
        WriteNum(sb, fid, ref sf);
        JsonWriter.End(sb, ']');
      }
    }
    JsonWriter.End(sb, ']');

    // savedKsc: [] — no archive producer yet.
    JsonWriter.Sep(sb, ref f);
    JsonWriter.Begin(sb, '[');
    JsonWriter.End(sb, ']');

    JsonWriter.End(sb, ']');
  }

  private static void WriteLtsExperimentFrame(
      StringBuilder sb, Thermometer t, Vessel kspVessel,
      Dictionary<string, double> savedLocal, ref bool first) {
    var ctx       = t.Vessel?.Context;
    string body   = ctx?.BodyName ?? "";
    string parent = ctx?.SolarParentName ?? body;
    var situation = ctx?.Situation ?? Situation.SrfLanded;
    double bodyYearSeconds = ctx?.BodyYearSeconds ?? 0;
    double simNow = Planetarium.GetUniversalTime();

    int slicesPerYear = LongTermStudyExperiment.SlicesPerYear;
    int currentSliceIndex = bodyYearSeconds > 0
        ? LongTermStudyExperiment.SliceIndexAt(simNow, bodyYearSeconds)
        : 0;
    double phase = bodyYearSeconds > 0
        ? (simNow - System.Math.Floor(simNow / bodyYearSeconds) * bodyYearSeconds) / bodyYearSeconds
        : 0;

    double sliceDuration = bodyYearSeconds > 0
        ? LongTermStudyExperiment.SliceDurationFor(bodyYearSeconds)
        : 1;

    // Read fidelity + recorded phase bounds from the live LTS file
    // for the current slice subject. Interpolate fidelity from the
    // file's (start_ut, end_ut) against simNow.
    string ltsSubjectId = bodyYearSeconds > 0 && situation != Situation.None
        ? LongTermStudyExperiment.SubjectFor(body, situation, simNow, bodyYearSeconds).ToString()
        : "";
    var ltsFile = !string.IsNullOrEmpty(ltsSubjectId)
        ? FindFileForSubject(t, ltsSubjectId)
        : null;

    double activeFidelity = 0;
    double recordedMin    = 0;
    double recordedMax    = 0;
    bool   willComplete   = true;
    if (ltsFile != null && ltsFile.SliceDurationSeconds > 0) {
      double span = ltsFile.EndUt - ltsFile.StartUt;
      double covered = System.Math.Min(simNow, ltsFile.EndUt) - ltsFile.StartUt;
      activeFidelity = System.Math.Min(1.0, System.Math.Max(0.0, covered / ltsFile.SliceDurationSeconds));
      // Recorded phase bounds: start_ut and current cap as fractions
      // of the body-year. UI uses these to overlay the recorded arc
      // on the orbit indicator's slice wedge.
      recordedMin = (ltsFile.StartUt
                    - System.Math.Floor(ltsFile.StartUt / bodyYearSeconds) * bodyYearSeconds)
                    / bodyYearSeconds;
      double cappedNow = System.Math.Min(simNow, ltsFile.EndUt);
      recordedMax = (cappedNow
                    - System.Math.Floor(cappedNow / bodyYearSeconds) * bodyYearSeconds)
                    / bodyYearSeconds;
      // willComplete: file's reachable max fidelity = span / sliceDuration.
      // Sub-1 means we started mid-slice; the file will never reach 1.0.
      willComplete = (span / ltsFile.SliceDurationSeconds) >= 0.99;
    }

    string destination = ResolveDestinationStorage(t, kspVessel, LongTermStudyExperiment.FileSizeBytes);

    JsonWriter.Sep(sb, ref first);
    JsonWriter.Begin(sb, '[');
    bool f = true;
    WriteKind(sb, "EXL", ref f);
    JsonWriter.Sep(sb, ref f);
    JsonWriter.WriteString(sb, LongTermStudyExperiment.ExperimentId);
    WriteBit(sb, t.LtsActive, ref f);
    WriteBit(sb, willComplete, ref f);
    WriteBit(sb, t.LtsEnabled, ref f);
    WriteNum(sb, recordedMin, ref f);
    WriteNum(sb, recordedMax, ref f);
    JsonWriter.Sep(sb, ref f);
    JsonWriter.WriteString(sb, destination);
    JsonWriter.Sep(sb, ref f);
    JsonWriter.WriteString(sb, body);
    JsonWriter.Sep(sb, ref f);
    JsonWriter.WriteString(sb, situation.ToString());
    JsonWriter.Sep(sb, ref f);
    JsonWriter.WriteString(sb, parent);
    WriteNum(sb, slicesPerYear, ref f);
    WriteNum(sb, bodyYearSeconds, ref f);
    WriteNum(sb, currentSliceIndex, ref f);
    WriteNum(sb, phase, ref f);
    WriteNum(sb, activeFidelity, ref f);

    // savedLocal: only emit slices that have a saved file.
    JsonWriter.Sep(sb, ref f);
    JsonWriter.Begin(sb, '[');
    bool firstSaved = true;
    for (int i = 0; i < slicesPerYear; i++) {
      var subjectId = LongTermStudyExperiment.ExperimentId + "@" + body + ":"
                    + situation + ":" + i;
      if (!savedLocal.TryGetValue(subjectId, out var fid)) continue;
      JsonWriter.Sep(sb, ref firstSaved);
      JsonWriter.Begin(sb, '[');
      bool sf = true;
      WriteNum(sb, i, ref sf);
      WriteNum(sb, fid, ref sf);
      JsonWriter.End(sb, ']');
    }
    JsonWriter.End(sb, ']');

    // savedKsc: [] — no archive producer yet.
    JsonWriter.Sep(sb, ref f);
    JsonWriter.Begin(sb, '[');
    JsonWriter.End(sb, ']');

    JsonWriter.End(sb, ']');
  }

  private static void WriteKind(StringBuilder sb, string kind, ref bool first) {
    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteString(sb, kind);
  }

  private static void WriteNum(StringBuilder sb, double value, ref bool first) {
    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteDouble(sb, value);
  }

  private static void WriteBit(StringBuilder sb, bool value, ref bool first) {
    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteBoolAsBit(sb, value);
  }
}
