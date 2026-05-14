using System.Collections.Generic;
using System.Linq;
using System.Text;
using Dragonglass.Telemetry.Topics;
using Nova.Components;
using Nova.Core.Components;
using Nova.Core.Components.Control;
using Nova.Core.Components.Electrical;
using Nova.Core.Components.Propulsion;
using Nova.Core.Components.Structural;
using Nova.Core.Components.Thermal;
using Nova.Core.Resources;
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
// This topic carries the resource + virtual-component view ONLY.
// Science instruments and data storage publish on dedicated
// per-part topics (`NovaScienceTopic`, `NovaStorageTopic`) so a
// `science-instrument`-tagged part can subscribe to its science
// payload without pulling resource/component noise.
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
//
// Each generator/consumer kind carries TWO rate fields so both the
// flight HUD (live, LP-throttled) and the editor HUD (design-rated,
// solver-independent) read off the same wire:
//   currentRate  — LP-throttled actual flow last tick. Always 0 in
//                  the editor (no VirtualVessel / solver).
//   ratedRate    — BoL design spec, independent of orientation /
//                  vessel state. Useful in editor for the load /
//                  source balance, and in flight for "what could
//                  this deliver in nominal conditions".
//
//   ["S", currentEcRate, maxEcRate, deployed, sunlit, retractable, ratedEcRate]   SolarPanel
//   ["B", soc(0..1), capacity, currentRate]                                       Battery
//   ["W", motorRate, busRate, bufferFraction, refillActive, motorRated, busRated] ReactionWheel
//   ["L", currentRate, ratedRate]                                                 Light
//   ["T", volume,
//          [[resource, capacity, contents,
//            tier:int, stage:int, maxStage:int,
//            boiloffFracPerDay, coolerEcW, coolerHeatW], ...]]              TankVolume
//   ["F", currentEcOutput, maxEcOutput, isActive, validUntilSec,
//          manifoldFraction, refillActive]                                         FuelCell
//   ["C", idleRate, testLoadRate, testLoadMaxRate, testLoadActive, idleRated]    Command
//   ["P", idleRate, testLoadRate, testLoadMaxRate, testLoadActive, idleRated,
//          sasLevel,
//          commandBytes, commandCapacityBytes,
//          commandRefillBps, commandDecayBps, commandConsumeBps]                   Probe
//   ["R", currentRate, currentPower, referencePower,
//          declineWattsPerKerbinYear,
//          wasteHeatW, exportW, rejectionW,
//          currentTempC, maxOperatingTempC, dTdtCps]                               Rtg
//   ["X", currentCoolingW, maxCoolingW, isDeployed, isDeployable,
//          currentEcW, maxEcW]                                                     Radiator
//   ["D", fullSeparation, canFullSeparate, ejectionForce]                           Decoupler
//
// Solar's `maxEcRate` stays orientation-and-sunlit gated (live "what
// this panel could give right now"); `ratedEcRate` is the always-on
// design value (what it'd give at full sun in a healthy orbit). The
// flight UI uses `current/max`; the editor UI uses `rated`.
// FuelCell and RTG already expose their design specs in the existing
// slots (`maxOutput` / `referencePower`), so no new field there.
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
//   "setRadiatorDeployed" [bool] — toggle a deployable radiator's
//                                IsDeployed flag. No-op on fixed
//                                panels (IsDeployable=false). State
//                                round-trips on save via RadiatorState.
//   "setTankCustom" [[[name, capacity, contents], ...]]
//                              — replace the part's tank loadout with
//                                the supplied resource/capacity/contents
//                                triples. Editor-only — rejected outside
//                                GameScenes.EDITOR. Rejected (no-op)
//                                when the summed capacity exceeds the
//                                part's TankVolume.Volume, when a name
//                                doesn't resolve, or when contents fall
//                                outside [0, capacity]. Resets every
//                                slice's insulation tier to MLI; the UI
//                                follows with setTankInsulation when a
//                                cryo preset wants HeavyMLI/BAC/ZBO.
//                                Presets live UI-side (editor/tank-
//                                presets.ts) and resolve to this op
//                                before dispatch.
//   "setTankInsulation" [tier:int, tier:int, ...]
//                              — replace the per-slice insulation tier
//                                vector (0=MLI, 1=HeavyMLI, 2=BAC,
//                                3=ZBO), one entry per existing slice
//                                in slice order. Editor-only. Rejected
//                                when length mismatches the slice
//                                count, an entry is out of range, or
//                                the resulting Σ capacity × (1 +
//                                tierVolumePenalty) exceeds the part's
//                                TankVolume.Volume.
//   "setFullSeparation" [bool]  — toggle the per-decoupler "release every
//                                neighbour" mode (stock separator
//                                semantics). Editor-only. Rejected on
//                                radial decouplers (CanFullSeparate=false)
//                                — the wire frame reports that bit so
//                                the UI greys the toggle.
//   "setTankCooler" [stage:int, stage:int, ...]
//                              — replace the per-slice runtime cooler-
//                                stage vector (0=off, 1=stage 1 (BAC-
//                                class), 2=stage 2 (ZBO-class — only
//                                valid on ZBO tier)). Length must
//                                match slice count; each entry must
//                                be in [0, MaxStage(slice.tier)] or
//                                the op is rejected wholesale. Stage
//                                changes are rare (player click), so
//                                we Invalidate the vessel on change
//                                rather than over-engineer dynamic
//                                Demand scaling — the LP rebuilds with
//                                the new max EC / heat rates.
public sealed class NovaPartTopic : Topic {
  private const string LogPrefix = "[Nova/Telemetry] ";

  // Stock Kerbin year length, used purely as a UI display unit for
  // the RTG decline rate. RTG decay is computed in real seconds; the
  // Kerbin-year conversion is a presentation concern, not physics.
  private const double KerbinYearSeconds = 9_203_545.0;

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
      case "setCommandTestLoad": {
        if (args == null || args.Count < 1 || !(args[0] is bool active)) {
          Debug.LogWarning(LogPrefix + Name + " setCommandTestLoad: expected [bool]");
          return;
        }
        // Reach through the live VirtualVessel to find the part's
        // Command/Probe component. Editor scope has no Virtual;
        // rejected. The op covers both control-source kinds because
        // the test-load toggle is identical between them — only the
        // host component type differs.
        var vesselModule = _part?.vessel?.GetComponent<NovaVesselModule>();
        var components = vesselModule?.Virtual?.GetComponents(_part.persistentId);
        if (components == null) return;
        bool toggled = false;
        foreach (var c in components) {
          if (c is Command command) {
            command.TestLoadActive = active;
            toggled = true;
          } else if (c is Probe probe) {
            probe.TestLoadActive = active;
            toggled = true;
          }
        }
        if (!toggled) return;
        vesselModule.Virtual.Invalidate();
        MarkDirty();
        return;
      }
      case "setRadiatorDeployed": {
        if (args == null || args.Count < 1 || !(args[0] is bool deployed)) {
          Debug.LogWarning(LogPrefix + Name + " setRadiatorDeployed: expected [bool]");
          return;
        }
        var module = _part?.FindModuleImplementing<NovaRadiatorModule>();
        if (module == null) return;
        if (deployed) module.Extend();
        else module.Retract();
        return;
      }
      case "setFullSeparation": {
        if (args == null || args.Count < 1 || !(args[0] is bool fullSep)) {
          Debug.LogWarning(LogPrefix + Name + " setFullSeparation: expected [bool]");
          return;
        }
        if (HighLogic.LoadedScene != GameScenes.EDITOR) {
          Debug.Log(LogPrefix + Name + " setFullSeparation rejected outside editor");
          return;
        }
        var module = _part?.FindModuleImplementing<NovaDecouplerModule>();
        if (module == null) return;
        if (fullSep && !module.CanFullSeparate) {
          // Radial decoupler — toggle is meaningless. Don't silently
          // accept; surface so the UI can be fixed if it's offering the
          // checkbox at all.
          Debug.LogWarning(LogPrefix + Name + " setFullSeparation rejected: radial decoupler");
          return;
        }
        module.FullSeparation = fullSep;
        MarkDirty();
        return;
      }
      case "setTankCustom": {
        if (args == null || args.Count < 1 || !(args[0] is List<object> rawTanks)) {
          Debug.LogWarning(LogPrefix + Name + " setTankCustom: expected [[[name, capacity, contents], ...]]");
          return;
        }
        if (HighLogic.LoadedScene != GameScenes.EDITOR) {
          Debug.Log(LogPrefix + Name + " setTankCustom rejected outside editor");
          return;
        }
        var module = _part?.FindModuleImplementing<NovaTankModule>();
        if (module?.TankVolume == null) return;

        var buffers = new List<Buffer>(rawTanks.Count);
        double sumCap = 0;
        foreach (var raw in rawTanks) {
          if (!(raw is List<object> entry) || entry.Count < 3
              || !(entry[0] is string resourceName)
              || !(entry[1] is double capacity)
              || !(entry[2] is double contents)) {
            Debug.LogWarning(LogPrefix + Name + " setTankCustom: malformed entry");
            return;
          }
          if (!Resource.TryGet(resourceName, out var resource)) {
            Debug.LogWarning(LogPrefix + Name + " setTankCustom: unknown resource '" + resourceName + "'");
            return;
          }
          if (capacity < 0) {
            Debug.LogWarning(LogPrefix + Name + " setTankCustom: negative capacity for '" + resourceName + "'");
            return;
          }
          if (contents < 0 || contents > capacity + 1e-6) {
            Debug.LogWarning(LogPrefix + Name + " setTankCustom: contents out of range for '" + resourceName + "'");
            return;
          }
          sumCap += capacity;
          buffers.Add(new Buffer { Resource = resource, Capacity = capacity, Contents = contents });
        }
        if (sumCap > module.TankVolume.Volume + 1e-6) {
          Debug.LogWarning(LogPrefix + Name + " setTankCustom: total capacity " + sumCap
              + " exceeds TankVolume.Volume " + module.TankVolume.Volume);
          return;
        }
        module.TankVolume.Reconfigure(buffers);
        MarkDirty();
        return;
      }
      case "setTankCooler": {
        if (args == null || args.Count < 1 || !(args[0] is List<object> rawStages)) {
          Debug.LogWarning(LogPrefix + Name + " setTankCooler: expected [[stage:int, ...]]");
          return;
        }
        var module = _part?.FindModuleImplementing<NovaTankModule>();
        if (module?.TankVolume == null) return;
        if (rawStages.Count != module.TankVolume.Tanks.Count) {
          Debug.LogWarning(LogPrefix + Name + " setTankCooler: stage count "
              + rawStages.Count + " != slice count " + module.TankVolume.Tanks.Count);
          return;
        }
        var stages = new List<int>(rawStages.Count);
        for (int i = 0; i < rawStages.Count; i++) {
          if (!(rawStages[i] is double d)) {
            Debug.LogWarning(LogPrefix + Name + " setTankCooler: entry " + i + " not a number");
            return;
          }
          var stage = (int)d;
          var max = InsulationTierTable.MaxStage(module.TankVolume.SliceTier(i));
          if (stage < 0 || stage > max) {
            Debug.LogWarning(LogPrefix + Name + " setTankCooler: slice " + i
                + " stage " + stage + " out of range [0," + max + "] for tier "
                + module.TankVolume.SliceTier(i));
            return;
          }
          stages.Add(stage);
        }
        if (!module.TankVolume.SetCoolerStages(stages)) return;
        // No topology rebuild needed — the cooler device is already
        // registered with the tier's max-stage envelope; OnPreSolve
        // picks up the new stage's Demand on the next tick. Just nudge
        // the solver to re-run.
        if (HighLogic.LoadedScene != GameScenes.EDITOR) {
          var vesselModule = _part?.vessel?.GetComponent<NovaVesselModule>();
          vesselModule?.Virtual?.Invalidate();
        }
        MarkDirty();
        return;
      }
      case "setTankInsulation": {
        if (args == null || args.Count < 1 || !(args[0] is List<object> rawTiers)) {
          Debug.LogWarning(LogPrefix + Name + " setTankInsulation: expected [[tier:int, ...]]");
          return;
        }
        if (HighLogic.LoadedScene != GameScenes.EDITOR) {
          Debug.Log(LogPrefix + Name + " setTankInsulation rejected outside editor");
          return;
        }
        var module = _part?.FindModuleImplementing<NovaTankModule>();
        if (module?.TankVolume == null) return;
        if (rawTiers.Count != module.TankVolume.Tanks.Count) {
          Debug.LogWarning(LogPrefix + Name + " setTankInsulation: tier count "
              + rawTiers.Count + " != slice count " + module.TankVolume.Tanks.Count);
          return;
        }
        var tiers = new List<InsulationTier>(rawTiers.Count);
        double footprint = 0;
        for (int i = 0; i < rawTiers.Count; i++) {
          // Decoded JSON numbers land as double; cast to int and range-check.
          if (!(rawTiers[i] is double d)) {
            Debug.LogWarning(LogPrefix + Name + " setTankInsulation: entry " + i + " not a number");
            return;
          }
          var tierInt = (int)d;
          if (tierInt < 0 || tierInt > (int)InsulationTier.ZBO) {
            Debug.LogWarning(LogPrefix + Name + " setTankInsulation: tier " + tierInt + " out of range");
            return;
          }
          var tier = (InsulationTier)tierInt;
          tiers.Add(tier);
          footprint += module.TankVolume.Tanks[i].Capacity
                     * (1.0 + InsulationTierTable.VolumePenalty(tier));
        }
        if (footprint > module.TankVolume.Volume + 1e-6) {
          Debug.LogWarning(LogPrefix + Name + " setTankInsulation: tier penalties push footprint "
              + footprint.ToString("F2") + " over Volume " + module.TankVolume.Volume.ToString("F2"));
          return;
        }
        module.TankVolume.SetTiers(tiers);
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
      if (!TryWriteComponent(sb, c, _part, kspVessel, ref first)) {
        // Unhandled kind — silently skip rather than emit an
        // un-decodable frame. New kinds get a case here + a TS
        // tuple in nova-topics.ts.
      }
    }
    JsonWriter.End(sb, ']');
  }

  private static bool TryWriteComponent(StringBuilder sb, VirtualComponent c, Part part, Vessel kspVessel, ref bool first) {
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
        WriteNum(sb, solar.ChargeRate, ref f);
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
        // internals. motorRated/busRated are the design specs (peak
        // single-axis-full motor draw and the buffer refill ceiling)
        // — invariant of solver state, used by the editor view.
        WriteNum(sb, wheel.CurrentDrain, ref f);
        WriteNum(sb, wheel.CurrentRefill, ref f);
        WriteNum(sb, wheel.Buffer?.FillFraction ?? 1.0, ref f);
        WriteBit(sb, wheel.RefillActive, ref f);
        WriteNum(sb, wheel.ElectricRate, ref f);
        WriteNum(sb, wheel.RefillRateWatts, ref f);
        JsonWriter.End(sb, ']');
        return true;
      }
      case NovaLight light: {
        JsonWriter.Sep(sb, ref first);
        JsonWriter.Begin(sb, '[');
        bool f = true;
        WriteKind(sb, "L", ref f);
        WriteNum(sb, light.Rate * light.Activity, ref f);
        WriteNum(sb, light.Rate, ref f);
        JsonWriter.End(sb, ']');
        return true;
      }
      case TankVolume tank: {
        JsonWriter.Sep(sb, ref first);
        JsonWriter.Begin(sb, '[');
        bool f = true;
        WriteKind(sb, "T", ref f);
        WriteNum(sb, tank.Volume, ref f);
        // Per-tank slices:
        //   [resource, capacity, contents, tier, stage, maxStage,
        //    boiloffFracPerDay, coolerEcW, coolerHeatW]
        // tier/stage/maxStage are structural + runtime; boiloff/ec/heat
        // are physical observables — boiloff is pre-blended with cooler
        // Activity, ec/heat are realised draws (Activity × max).
        // Stage-0 slices (and passive tiers / non-cryo resources) emit
        // 0 for ec/heat. `maxStage` lets the UI shape the toggle (0 =
        // hidden, 1 = on/off, 2 = off/s1/s2) without hard-coding tier
        // semantics client-side.
        JsonWriter.Sep(sb, ref f);
        JsonWriter.Begin(sb, '[');
        bool fb = true;
        for (int i = 0; i < tank.Tanks.Count; i++) {
          var buf = tank.Tanks[i];
          var tier = tank.SliceTier(i);
          JsonWriter.Sep(sb, ref fb);
          JsonWriter.Begin(sb, '[');
          bool fbi = true;
          JsonWriter.Sep(sb, ref fbi);
          JsonWriter.WriteString(sb, buf.Resource?.Name ?? "");
          JsonWriter.Sep(sb, ref fbi);
          JsonWriter.WriteDouble(sb, buf.Capacity);
          JsonWriter.Sep(sb, ref fbi);
          JsonWriter.WriteDouble(sb, buf.Contents);
          JsonWriter.Sep(sb, ref fbi);
          JsonWriter.WriteDouble(sb, (int)tier);
          JsonWriter.Sep(sb, ref fbi);
          JsonWriter.WriteDouble(sb, tank.SliceStage(i));
          JsonWriter.Sep(sb, ref fbi);
          JsonWriter.WriteDouble(sb, InsulationTierTable.MaxStage(tier));
          JsonWriter.Sep(sb, ref fbi);
          JsonWriter.WriteDouble(sb, tank.SliceNetBoiloffFractionPerDay(i));
          JsonWriter.Sep(sb, ref fbi);
          JsonWriter.WriteDouble(sb, tank.SliceCurrentEcW(i));
          JsonWriter.Sep(sb, ref fbi);
          JsonWriter.WriteDouble(sb, tank.SliceCurrentHeatW(i));
          JsonWriter.End(sb, ']');
        }
        JsonWriter.End(sb, ']');
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
        WriteNum(sb, command.IdleDraw, ref f);
        JsonWriter.End(sb, ']');
        return true;
      }
      case Probe probe: {
        if (probe.IdleDraw <= 0 && probe.TestLoadRate <= 0) return false;
        JsonWriter.Sep(sb, ref first);
        JsonWriter.Begin(sb, '[');
        bool f = true;
        WriteKind(sb, "P", ref f);
        WriteNum(sb, probe.IdleDraw * probe.IdleActivity, ref f);
        WriteNum(sb, probe.TestLoadRate * probe.TestLoadActivity, ref f);
        WriteNum(sb, probe.TestLoadRate, ref f);
        WriteBit(sb, probe.TestLoadActive, ref f);
        WriteNum(sb, probe.IdleDraw, ref f);
        WriteNum(sb, probe.SasLevel, ref f);
        WriteNum(sb, probe.CommandBytes, ref f);
        WriteNum(sb, probe.CommandCapacityBytes, ref f);
        WriteNum(sb, probe.CommandRefillBps, ref f);
        WriteNum(sb, probe.CommandDecayBps, ref f);
        WriteNum(sb, probe.CommandConsumeBps, ref f);
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
        WriteNum(sb, fuelCell.Manifold.FillFraction, ref f);
        WriteBit(sb, fuelCell.RefillActive, ref f);
        JsonWriter.End(sb, ']');
        return true;
      }
      case Rtg rtg: {
        // No vessel guard — Rtg's accessors (CurrentPower, CurrentRate,
        // CurrentWasteHeatW, CurrentRejectionW, CurrentExportW,
        // CurrentTempC, DTdtCps) all handle Vessel == null by falling
        // back to BoL / 0, which is exactly what the editor view wants.
        double currentPower = rtg.CurrentPower;
        double halfLifeSec = rtg.HalfLifeDays * 86400.0;
        // P(t+Ky) = P(t) × 2^(-Ky / halfLife) → decline = P × (1 - …).
        double decayFracOverKy = 1.0 - System.Math.Pow(2.0, -KerbinYearSeconds / halfLifeSec);
        double declineWattsPerKy = currentPower * decayFracOverKy;

        JsonWriter.Sep(sb, ref first);
        JsonWriter.Begin(sb, '[');
        bool f = true;
        WriteKind(sb, "R", ref f);
        WriteNum(sb, rtg.CurrentRate, ref f);
        WriteNum(sb, currentPower, ref f);
        WriteNum(sb, rtg.ReferencePower, ref f);
        WriteNum(sb, declineWattsPerKy, ref f);
        WriteNum(sb, rtg.CurrentWasteHeatW, ref f);
        WriteNum(sb, rtg.CurrentExportW, ref f);
        WriteNum(sb, rtg.CurrentRejectionW, ref f);
        WriteNum(sb, rtg.CurrentTempC, ref f);
        WriteNum(sb, rtg.MaxOperatingTempC, ref f);
        WriteNum(sb, rtg.DTdtCps, ref f);
        JsonWriter.End(sb, ']');
        return true;
      }
      case Radiator radiator: {
        JsonWriter.Sep(sb, ref first);
        JsonWriter.Begin(sb, '[');
        bool f = true;
        WriteKind(sb, "X", ref f);
        WriteNum(sb, radiator.CurrentCoolingW, ref f);
        WriteNum(sb, radiator.CurrentMaxCoolingW, ref f);
        WriteBit(sb, radiator.IsDeployed, ref f);
        WriteBit(sb, radiator.IsDeployable, ref f);
        WriteNum(sb, radiator.CurrentEcW, ref f);
        WriteNum(sb, radiator.MaxEcW, ref f);
        JsonWriter.End(sb, ']');
        return true;
      }
      case Decoupler decoupler: {
        // CanFullSeparate is a property of the KSP-side PartModule
        // (derived from explosiveNodeID) rather than the virtual
        // component, so reach through to the module here. ejectionForce
        // is design-rated and lives on the module too.
        var module = part?.FindModuleImplementing<NovaDecouplerModule>();
        if (module == null) return false;
        JsonWriter.Sep(sb, ref first);
        JsonWriter.Begin(sb, '[');
        bool f = true;
        WriteKind(sb, "D", ref f);
        WriteBit(sb, decoupler.FullSeparation, ref f);
        WriteBit(sb, module.CanFullSeparate, ref f);
        WriteNum(sb, module.ejectionForce, ref f);
        JsonWriter.End(sb, ']');
        return true;
      }
    }
    return false;
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
