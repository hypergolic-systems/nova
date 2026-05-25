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
using Nova.Core.Telemetry;
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
//          isRetractable, currentEcW, maxEcW]                                      Radiator
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
//                                panels (IsDeployable=false), and no-op
//                                on retract requests against one-shot
//                                deployables (IsRetractable=false) in
//                                flight (editor still allows it for
//                                build-time staging). State round-trips
//                                on save via RadiatorState.
//   "setAntennaDeployed" [bool] — toggle a deployable antenna's
//                                IsDeployed flag. No-op on fixed
//                                antennas (cfg without animationName,
//                                integrated antennas on probe cores).
//                                State round-trips on save via AntennaState.
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
//   "setReactorActive" [bool]   — toggle a nuclear engine's reactor.
//                                Flight-only. True from Cold starts
//                                warmup; false from Idle starts
//                                cooldown; false from Throttled
//                                latches `ShutdownRequested` so the
//                                state machine auto-sequences down
//                                through Idle to Cooling once the
//                                throttle slews to 0. No-op on parts
//                                without a NovaNuclearEngineModule
//                                and on already-terminal states.
//   "setIonResetTrip"           — clear an ion engine's trip latch.
//                                Flight-only. No-op if not tripped.
//                                Leaves `Active` alone — player must
//                                re-stage or fire setEngineActive(true)
//                                to relight the engine.
//   "setEngineActive" [bool]    — toggle a chemical or ion engine's
//                                Active flag. Lets the player shut a
//                                staged engine down (and re-light it)
//                                without un-staging. Rejected on
//                                nuclear engines (use setReactorActive)
//                                and on tripped ion engines (call
//                                setIonResetTrip first).
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
      case "setAntennaDeployed": {
        if (args == null || args.Count < 1 || !(args[0] is bool deployed)) {
          Debug.LogWarning(LogPrefix + Name + " setAntennaDeployed: expected [bool]");
          return;
        }
        var module = _part?.FindModuleImplementing<NovaAntennaModule>();
        if (module == null) return;
        if (deployed) module.Extend();
        else module.Retract();
        return;
      }
      case "setReactorActive": {
        if (args == null || args.Count < 1 || !(args[0] is bool active)) {
          Debug.LogWarning(LogPrefix + Name + " setReactorActive: expected [bool]");
          return;
        }
        if (HighLogic.LoadedScene != GameScenes.FLIGHT) {
          Debug.Log(LogPrefix + Name + " setReactorActive rejected outside flight");
          return;
        }
        var module = _part?.FindModuleImplementing<NovaNuclearEngineModule>();
        var reactor = module?.Reactor;
        if (reactor == null) return;
        if (!reactor.SetReactorActive(active)) return;
        var vesselModule = _part?.vessel?.GetComponent<NovaVesselModule>();
        vesselModule?.Virtual?.Invalidate();
        MarkDirty();
        return;
      }
      case "setIonResetTrip": {
        if (HighLogic.LoadedScene != GameScenes.FLIGHT) {
          Debug.Log(LogPrefix + Name + " setIonResetTrip rejected outside flight");
          return;
        }
        var module = _part?.FindModuleImplementing<NovaIonEngineModule>();
        var ion = module?.Ion;
        if (ion == null || !ion.Tripped) return;
        ion.Tripped = false;
        ion.TripReason = Nova.Core.Components.Propulsion.IonTripReason.None;
        var vesselModule = _part?.vessel?.GetComponent<NovaVesselModule>();
        vesselModule?.Virtual?.Invalidate();
        MarkDirty();
        return;
      }
      case "setEngineActive": {
        // Toggle a chemical or ion engine's player-facing on/off bit.
        // Stock KSP's staging system flips Active=true via OnActive when
        // a stage fires; this op gives the player an in-flight UI to
        // shut a staged engine down (and re-light it) without un-staging
        // the whole stack. Nuclear engines have their own
        // setReactorActive state-machine op and are not affected here.
        if (args == null || args.Count < 1 || !(args[0] is bool active)) {
          Debug.LogWarning(LogPrefix + Name + " setEngineActive: expected [bool]");
          return;
        }
        if (HighLogic.LoadedScene != GameScenes.FLIGHT) {
          Debug.Log(LogPrefix + Name + " setEngineActive rejected outside flight");
          return;
        }
        // Lookup any Engine that isn't a NuclearEngine subclass — chemical
        // engines and ion engines both have a plain Active toggle.
        var engineModule = _part?.FindModuleImplementing<NovaEngineModule>();
        if (engineModule is NovaNuclearEngineModule) return;
        var engine = engineModule?.Engine;
        if (engine == null) return;
        // Ion engines refuse re-light while tripped — the player must
        // setIonResetTrip first to acknowledge the fault.
        if (active && engineModule is NovaIonEngineModule ionMod
            && ionMod.Ion is { Tripped: true }) return;
        if (engine.Active == active) return;
        engine.Active = active;
        if (!active) engine.Throttle = 0;
        var vesselModule = _part?.vessel?.GetComponent<NovaVesselModule>();
        vesselModule?.Virtual?.Invalidate();
        MarkDirty();
        return;
      }
      case "setReactorPlayerThrottle": {
        // UI-drag input to the reactor throttle. In flight, the
        // vessel's mainThrottle (keyboard) is the canonical input;
        // NovaNuclearEngineModule.FixedUpdate rewrites PlayerThrottle
        // every physics tick from `vessel.ctrlState.mainThrottle`, so
        // a UI op set here would last only until the next tick and be
        // visually jittery. Out-of-flight (e.g., the sim's headless
        // runner has no FixedUpdate), the op latches and the reactor
        // chases it. Acceptable behaviour: live drag controls in the
        // sim, no-op in real flight.
        if (args == null || args.Count < 1 || !(args[0] is double throttle)) {
          Debug.LogWarning(LogPrefix + Name + " setReactorPlayerThrottle: expected [double]");
          return;
        }
        var module = _part?.FindModuleImplementing<NovaNuclearEngineModule>();
        var reactor = module?.Reactor;
        if (reactor == null) return;
        reactor.PlayerThrottle = System.Math.Max(0, System.Math.Min(1, throttle));
        var vesselModule = _part?.vessel?.GetComponent<NovaVesselModule>();
        vesselModule?.Virtual?.Invalidate();
        MarkDirty();
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
    PartFormatter.Write(sb, _part.persistentId, ResolveComponents());
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

}
