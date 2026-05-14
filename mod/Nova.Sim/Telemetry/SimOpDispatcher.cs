using System;
using System.Collections.Generic;
using Nova.Core.Components;
using Nova.Core.Components.Control;
using Nova.Core.Components.Electrical;
using Nova.Core.Components.Propulsion;
using Nova.Core.Components.Structural;
using Nova.Core.Components.Thermal;
using Nova.Core.Resources;
using Nova.Sim.Runtime;
using Buffer = Nova.Core.Resources.Buffer;

namespace Nova.Sim.Telemetry;

// Inbound op dispatcher — the sim-side analogue of NovaPartTopic.HandleOp.
// Routes ops to direct mutations on the matching VirtualComponent,
// skipping the PartModule layer that the in-game dispatcher goes through
// (no `_part`, no animations, no GameEvents, no MarkDirty plumbing —
// the 10 Hz publisher picks up state on its next tick).
//
// Scene gates (`HighLogic.LoadedScene != EDITOR`) from the mod are
// dropped: the sim has no scene concept and is always running a flight
// save. Editor-only ops (setFullSeparation, setTankCustom,
// setTankInsulation) work the same way as flight ops here; the in-game
// UI hides them in flight, so the running sim only sees them when a
// developer fires them directly via the WS for testing.
//
// Threading: HandleClientMessage delivers ops on Fleck's socket thread.
// All component access here takes _runner.Lock for the mutation window;
// the tick loop also holds the lock while calling Vessel.Tick(), so an
// in-flight op + tick never interleave. After mutating, we don't need
// to publish — SimTelemetryServer.PublishOnce will re-emit within 100 ms.
public static class SimOpDispatcher {
  public static void Handle(string topic, string op, List<object> args, SimRunner runner) {
    if (string.IsNullOrEmpty(topic) || string.IsNullOrEmpty(op)) return;

    if (topic.StartsWith("NovaPart/", StringComparison.Ordinal)) {
      var suffix = topic.Substring("NovaPart/".Length);
      if (!uint.TryParse(suffix, out var partId)) {
        Warn(topic + " op " + op + ": malformed partId '" + suffix + "'");
        return;
      }
      HandlePartOp(partId, op, args, runner);
      return;
    }

    // Future: NovaScience/<id> ops (setExperimentEnabled) — same shape,
    // separate handler. Held off until the sim has an atmosphere/UT
    // model that lets DiscardFile produce meaningful subject keys.
  }

  // ---- NovaPart/<id> --------------------------------------------------

  private static void HandlePartOp(uint partId, string op, List<object> args, SimRunner runner) {
    switch (op) {
      case "setSolarDeployed":    SetSolarDeployed(partId, args, runner); return;
      case "setRadiatorDeployed": SetRadiatorDeployed(partId, args, runner); return;
      case "setCommandTestLoad":  SetCommandTestLoad(partId, args, runner); return;
      case "setFullSeparation":   SetFullSeparation(partId, args, runner); return;
      case "setTankCustom":       SetTankCustom(partId, args, runner); return;
      case "setTankCooler":       SetTankCooler(partId, args, runner); return;
      case "setTankInsulation":   SetTankInsulation(partId, args, runner); return;
      default:
        Warn("NovaPart/" + partId + " unknown op '" + op + "'");
        return;
    }
  }

  private static void SetSolarDeployed(uint partId, List<object> args, SimRunner runner) {
    if (args == null || args.Count < 1 || !(args[0] is bool deployed)) {
      Warn("NovaPart/" + partId + " setSolarDeployed: expected [bool]");
      return;
    }
    lock (runner.Lock) {
      bool toggled = false;
      foreach (var c in runner.Vessel.GetComponents(partId)) {
        if (!(c is SolarPanel panel)) continue;
        // Mirror NovaDeployableSolarModule guards: a fixed (non-
        // retractable) panel can't be retracted in flight. Extending
        // always succeeds (a fixed panel is already deployed; the
        // mutation is a no-op in that case).
        if (!deployed && !panel.IsRetractable) continue;
        if (panel.IsDeployed == deployed) continue;
        panel.IsDeployed = deployed;
        toggled = true;
      }
      if (toggled) runner.Vessel.Invalidate();
    }
  }

  private static void SetRadiatorDeployed(uint partId, List<object> args, SimRunner runner) {
    if (args == null || args.Count < 1 || !(args[0] is bool deployed)) {
      Warn("NovaPart/" + partId + " setRadiatorDeployed: expected [bool]");
      return;
    }
    lock (runner.Lock) {
      bool toggled = false;
      foreach (var c in runner.Vessel.GetComponents(partId)) {
        if (!(c is Radiator radiator)) continue;
        if (!radiator.IsDeployable) continue;
        if (radiator.IsDeployed == deployed) continue;
        radiator.IsDeployed = deployed;
        toggled = true;
      }
      if (toggled) runner.Vessel.Invalidate();
    }
  }

  private static void SetCommandTestLoad(uint partId, List<object> args, SimRunner runner) {
    if (args == null || args.Count < 1 || !(args[0] is bool active)) {
      Warn("NovaPart/" + partId + " setCommandTestLoad: expected [bool]");
      return;
    }
    lock (runner.Lock) {
      bool toggled = false;
      foreach (var c in runner.Vessel.GetComponents(partId)) {
        if (c is Command command) { command.TestLoadActive = active; toggled = true; }
        else if (c is Probe probe) { probe.TestLoadActive = active; toggled = true; }
      }
      if (toggled) runner.Vessel.Invalidate();
    }
  }

  private static void SetFullSeparation(uint partId, List<object> args, SimRunner runner) {
    if (args == null || args.Count < 1 || !(args[0] is bool fullSep)) {
      Warn("NovaPart/" + partId + " setFullSeparation: expected [bool]");
      return;
    }
    lock (runner.Lock) {
      foreach (var c in runner.Vessel.GetComponents(partId)) {
        if (!(c is Decoupler decoupler)) continue;
        if (fullSep && !decoupler.CanFullSeparate) {
          Warn("NovaPart/" + partId + " setFullSeparation rejected: radial decoupler");
          continue;
        }
        decoupler.FullSeparation = fullSep;
      }
    }
  }

  private static void SetTankCustom(uint partId, List<object> args, SimRunner runner) {
    if (args == null || args.Count < 1 || !(args[0] is List<object> rawTanks)) {
      Warn("NovaPart/" + partId + " setTankCustom: expected [[[name, capacity, contents], ...]]");
      return;
    }
    lock (runner.Lock) {
      TankVolume tank = FindTank(runner, partId);
      if (tank == null) return;

      var buffers = new List<Buffer>(rawTanks.Count);
      double sumCap = 0;
      foreach (var raw in rawTanks) {
        if (!(raw is List<object> entry) || entry.Count < 3
            || !(entry[0] is string resourceName)
            || !(entry[1] is double capacity)
            || !(entry[2] is double contents)) {
          Warn("NovaPart/" + partId + " setTankCustom: malformed entry");
          return;
        }
        if (!Resource.TryGet(resourceName, out var resource)) {
          Warn("NovaPart/" + partId + " setTankCustom: unknown resource '" + resourceName + "'");
          return;
        }
        if (capacity < 0) {
          Warn("NovaPart/" + partId + " setTankCustom: negative capacity for '" + resourceName + "'");
          return;
        }
        if (contents < 0 || contents > capacity + 1e-6) {
          Warn("NovaPart/" + partId + " setTankCustom: contents out of range for '" + resourceName + "'");
          return;
        }
        sumCap += capacity;
        buffers.Add(new Buffer { Resource = resource, Capacity = capacity, Contents = contents });
      }
      if (sumCap > tank.Volume + 1e-6) {
        Warn("NovaPart/" + partId + " setTankCustom: total capacity " + sumCap
            + " exceeds TankVolume.Volume " + tank.Volume);
        return;
      }
      tank.Reconfigure(buffers);
      runner.Vessel.Invalidate();
    }
  }

  private static void SetTankCooler(uint partId, List<object> args, SimRunner runner) {
    if (args == null || args.Count < 1 || !(args[0] is List<object> rawStages)) {
      Warn("NovaPart/" + partId + " setTankCooler: expected [[stage:int, ...]]");
      return;
    }
    lock (runner.Lock) {
      TankVolume tank = FindTank(runner, partId);
      if (tank == null) return;
      if (rawStages.Count != tank.Tanks.Count) {
        Warn("NovaPart/" + partId + " setTankCooler: stage count "
            + rawStages.Count + " != slice count " + tank.Tanks.Count);
        return;
      }
      var stages = new List<int>(rawStages.Count);
      for (int i = 0; i < rawStages.Count; i++) {
        if (!(rawStages[i] is double d)) {
          Warn("NovaPart/" + partId + " setTankCooler: entry " + i + " not a number");
          return;
        }
        var stage = (int)d;
        var max = InsulationTierTable.MaxStage(tank.SliceTier(i));
        if (stage < 0 || stage > max) {
          Warn("NovaPart/" + partId + " setTankCooler: slice " + i
              + " stage " + stage + " out of range [0," + max + "] for tier "
              + tank.SliceTier(i));
          return;
        }
        stages.Add(stage);
      }
      if (!tank.SetCoolerStages(stages)) return;
      runner.Vessel.Invalidate();
    }
  }

  private static void SetTankInsulation(uint partId, List<object> args, SimRunner runner) {
    if (args == null || args.Count < 1 || !(args[0] is List<object> rawTiers)) {
      Warn("NovaPart/" + partId + " setTankInsulation: expected [[tier:int, ...]]");
      return;
    }
    lock (runner.Lock) {
      TankVolume tank = FindTank(runner, partId);
      if (tank == null) return;
      if (rawTiers.Count != tank.Tanks.Count) {
        Warn("NovaPart/" + partId + " setTankInsulation: tier count "
            + rawTiers.Count + " != slice count " + tank.Tanks.Count);
        return;
      }
      var tiers = new List<InsulationTier>(rawTiers.Count);
      double footprint = 0;
      for (int i = 0; i < rawTiers.Count; i++) {
        if (!(rawTiers[i] is double d)) {
          Warn("NovaPart/" + partId + " setTankInsulation: entry " + i + " not a number");
          return;
        }
        var tierInt = (int)d;
        if (tierInt < 0 || tierInt > (int)InsulationTier.ZBO) {
          Warn("NovaPart/" + partId + " setTankInsulation: tier " + tierInt + " out of range");
          return;
        }
        var tier = (InsulationTier)tierInt;
        tiers.Add(tier);
        footprint += tank.Tanks[i].Capacity
                   * (1.0 + InsulationTierTable.VolumePenalty(tier));
      }
      if (footprint > tank.Volume + 1e-6) {
        Warn("NovaPart/" + partId + " setTankInsulation: tier penalties push footprint "
            + footprint.ToString("F2") + " over Volume " + tank.Volume.ToString("F2"));
        return;
      }
      tank.SetTiers(tiers);
      runner.Vessel.Invalidate();
    }
  }

  private static TankVolume FindTank(SimRunner runner, uint partId) {
    foreach (var c in runner.Vessel.GetComponents(partId)) {
      if (c is TankVolume tank) return tank;
    }
    return null;
  }

  private static void Warn(string msg) {
    Console.WriteLine("[op] " + msg);
  }
}
