using System.Collections.Generic;
using System.Text;
using Nova.Core.Components;
using Nova.Core.Components.Control;
using Nova.Core.Components.Electrical;
using Nova.Core.Components.Propulsion;
using Nova.Core.Components.Structural;
using Nova.Core.Components.Thermal;
using Nova.Core.Resources;

namespace Nova.Core.Telemetry;

// Per-part component-state wire frame.
//
// Wire format (positional array, single-char component kind prefix
// matching the convention used by stock Dragonglass PartTopic):
//   [partId,
//    [ [resourceName, amount, capacity, currentRate], ... ],
//    [ [kind, ...], ... ]
//   ]
//
// Resource frames are emitted once per Buffer-bearing component
// (TankVolume tanks + Battery cells).
//
// Component frames carry physical observables — rates in W (or the
// natural unit), fractions in 0..1. Each generator/consumer kind
// carries TWO rate fields where applicable: `currentRate` (LP-throttled
// actual flow from last solve — 0 in editor / no-vessel contexts)
// and `ratedRate` (BoL design spec, solver-independent).
//
// Component kind prefixes (single chars to keep payload tight; the
// TS decoder switches on these):
//   "S" SolarPanel        — currentEcRate, maxEcRate, deployed, sunlit, retractable, ratedEcRate
//   "B" Battery           — soc(0..1), capacity, currentRate
//   "W" ReactionWheel     — motorRate, busRate, bufferFraction, refillActive, motorRated, busRated
//   "L" Light             — currentRate, ratedRate
//   "T" TankVolume        — volume, [[resource, capacity, contents], ...]
//   "F" FuelCell          — currentEcOutput, maxEcOutput, isActive, validUntilSec, manifoldFraction, refillActive
//   "C" Command           — idleRate, testLoadRate, testLoadMaxRate, testLoadActive, idleRated
//   "P" Probe             — idleRate, testLoadRate, testLoadMaxRate, testLoadActive, idleRated,
//                           sasLevel, commandBytes, commandCapacityBytes,
//                           commandRefillBps, commandDecayBps, commandConsumeBps
//   "R" Rtg               — currentRate, currentPower, referencePower,
//                           declineWattsPerKy,
//                           wasteHeatW, exportW, rejectionW,
//                           currentTempC, maxOperatingTempC, dTdtCps
//   "X" Radiator          — currentCoolingW, maxCoolingW, isDeployed, isDeployable
//   "D" Decoupler         — fullSeparation, canFullSeparate, ejectionForce
public static class PartFormatter {
  // Stock Kerbin year in seconds; presentation unit for the RTG
  // decline rate (real math is in seconds, this is a UI conversion).
  private const double KerbinYearSeconds = 9_203_545.0;

  public static void Write(StringBuilder sb, uint partId, IEnumerable<VirtualComponent> components) {
    JsonWriter.Begin(sb, '[');
    bool first = true;

    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteUintAsString(sb, partId);

    var materialized = MaterializeOnce(components);

    JsonWriter.Sep(sb, ref first);
    WriteResources(sb, materialized);

    JsonWriter.Sep(sb, ref first);
    WriteComponents(sb, materialized);

    JsonWriter.End(sb, ']');
  }

  // Callers may pass deferred enumerables (LINQ). We iterate twice
  // (once for resources, once for components) — materialize to avoid
  // re-running source-side queries.
  private static List<VirtualComponent> MaterializeOnce(IEnumerable<VirtualComponent> components) {
    if (components is List<VirtualComponent> list) return list;
    var result = new List<VirtualComponent>();
    if (components != null) foreach (var c in components) result.Add(c);
    return result;
  }

  private static void WriteResources(StringBuilder sb, List<VirtualComponent> components) {
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

  private static void WriteComponents(StringBuilder sb, List<VirtualComponent> components) {
    JsonWriter.Begin(sb, '[');
    bool first = true;
    foreach (var c in components) {
      if (TryWriteComponent(sb, c, ref first)) continue;
      // Unhandled kind — silently skip rather than emit an
      // un-decodable frame. New kinds get a case here + a TS
      // tuple in nova-topics.ts.
    }
    JsonWriter.End(sb, ']');
  }

  private static bool TryWriteComponent(StringBuilder sb, VirtualComponent c, ref bool first) {
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
      case Light light: {
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
        // No vessel guard — Rtg's accessors all handle Vessel == null
        // by falling back to BoL / 0, which is what the editor view
        // wants too.
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
        JsonWriter.End(sb, ']');
        return true;
      }
      case Decoupler decoupler: {
        JsonWriter.Sep(sb, ref first);
        JsonWriter.Begin(sb, '[');
        bool f = true;
        WriteKind(sb, "D", ref f);
        WriteBit(sb, decoupler.FullSeparation, ref f);
        WriteBit(sb, decoupler.CanFullSeparate, ref f);
        WriteNum(sb, decoupler.EjectionForce, ref f);
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
