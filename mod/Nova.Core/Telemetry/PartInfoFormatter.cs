using System.Collections.Generic;
using System.Text;
using Nova.Core.Components;
using Nova.Core.Components.Communications;
using Nova.Core.Components.Control;
using Nova.Core.Components.Crew;
using Nova.Core.Components.Electrical;
using Nova.Core.Components.Propulsion;
using Nova.Core.Components.Science;
using Nova.Core.Components.Structural;
using Nova.Core.Components.Thermal;

namespace Nova.Core.Telemetry;

// Static-spec wire frame for the editor parts-list hover popup
// (see `NovaPartInfoTopic` and the `PartInfoPopup` Svelte component).
//
// Distinct schema from `PartFormatter`: every numeric here is a *design*
// value (BoL thrust, capacity at full intensity, half-life, ...). The
// runtime / LP-throttled values that `PartFormatter` carries don't
// exist before the part is placed in the editor.
//
// Wire format (positional array). Empty array clears the popup:
//   []
//   | [internalName, displayTitle, manufacturer, description,
//      dryMassKg, costFunds, anchorX, anchorY,
//      [ [kind, ...], ... ]]
//
// Single-char kind prefix per Nova component family. Same letter as
// `PartFormatter` where the kind already exists there (so the kind
// stays a stable identifier across topics); each frame's *fields* are
// the design specs for that kind:
//
//   "E" Engine        — thrustKn, ispS, gimbalDeg, [[resource, ratio], ...]
//   "N" NuclearEngine — thrustKn, ispS, idleTempK, opTempK,
//                       idlePowerW, maxPowerW, warmupSec, slewPerSec,
//                       [[resource, ratio], ...]
//   "M" Rcs           — thrusterPowerKn, thrusterCount, ispS,
//                       [[resource, ratio], ...]
//   "T" TankVolume    — volumeL, maxRateLps,
//                       [[resource, capacityL, tier:int], ...]
//   "B" Battery       — capacityJ, maxRateW
//   "F" FuelCell      — maxOutputW, lh2RateKgs, loxRateKgs
//   "S" SolarPanel    — chargeRateW, isTracking, isDeployable
//   "R" Rtg           — referencePowerW, halfLifeDays, thermalOutputW,
//                       maxOpTempC, vacuumRejectionW, atmRejectionW
//   "W" ReactionWheel — pitchTorqueKnm, yawTorqueKnm, rollTorqueKnm,
//                       electricRateW
//   "X" Radiator      — vacuumCoolingW, atmCoolingW,
//                       ecPerWattCooling, isDeployable
//   "L" Light         — drawW
//   "C" Command       — idleDrawW, testLoadRateW
//   "P" Probe         — idleDrawW, testLoadRateW, sasLevel,
//                       commandCapBytes, commandDecayBps,
//                       commandReceiveBps, inputCostBps
//   "A" Antenna       — txPowerW, gain, maxRateBps, refDistanceM
//   "D" Decoupler     — ejectionForceKn, canFullSeparate,
//                       [allowedResources]
//   "K" DockingPort   — nodeType (string, e.g. "size1")
//   "Y" Crew          — crewCapacity
//   "Z" DataStorage   — capacityBytes
//   "H" Thermometer   — instrumentName, ecRateW
//
// New kind = new case in `TryWriteComponent` + matching tuple in
// `ui/.../nova-topics.ts`. The kind catalogue is kept in lock-step
// across the two files; adding here without the TS side decodes
// silently as a skipped component.
public static class PartInfoFormatter {
  public static void WriteEmpty(StringBuilder sb) {
    sb.Append("[]");
  }

  public static void Write(StringBuilder sb,
                            string internalName,
                            string title,
                            string manufacturer,
                            string description,
                            double dryMassKg,
                            double costFunds,
                            double anchorX,
                            double anchorY,
                            IEnumerable<VirtualComponent> components,
                            DockingPortInfo dockingInfo) {
    JsonWriter.Begin(sb, '[');
    bool first = true;

    JsonWriter.Sep(sb, ref first); JsonWriter.WriteString(sb, internalName ?? "");
    JsonWriter.Sep(sb, ref first); JsonWriter.WriteString(sb, title ?? "");
    JsonWriter.Sep(sb, ref first); JsonWriter.WriteString(sb, manufacturer ?? "");
    JsonWriter.Sep(sb, ref first); JsonWriter.WriteString(sb, description ?? "");
    JsonWriter.Sep(sb, ref first); JsonWriter.WriteDouble(sb, dryMassKg);
    JsonWriter.Sep(sb, ref first); JsonWriter.WriteDouble(sb, costFunds);
    JsonWriter.Sep(sb, ref first); JsonWriter.WriteDouble(sb, anchorX);
    JsonWriter.Sep(sb, ref first); JsonWriter.WriteDouble(sb, anchorY);

    JsonWriter.Sep(sb, ref first);
    WriteComponents(sb, components, dockingInfo);

    JsonWriter.End(sb, ']');
  }

  // Side-channel data that doesn't live on the `VirtualComponent` itself.
  // `DockingPort` is a `VirtualComponent` but its `nodeType` comes off the
  // KSP-side `NovaDockingModule` config (Nova.Core has no notion of KSP
  // attachment sizes). The caller scans modules and supplies the value.
  public readonly struct DockingPortInfo {
    public readonly string NodeType;
    public readonly int RcsThrusterCount;
    public DockingPortInfo(string nodeType, int rcsThrusterCount) {
      NodeType = nodeType;
      RcsThrusterCount = rcsThrusterCount;
    }
  }

  private static void WriteComponents(StringBuilder sb,
                                       IEnumerable<VirtualComponent> components,
                                       DockingPortInfo dockingInfo) {
    JsonWriter.Begin(sb, '[');
    bool first = true;
    if (components != null) {
      foreach (var c in components) {
        if (c == null) continue;
        TryWriteComponent(sb, c, dockingInfo, ref first);
        // Unhandled kinds silently skip — new kinds get a case here
        // and a matching TS tuple in nova-topics.ts.
      }
    }
    JsonWriter.End(sb, ']');
  }

  private static bool TryWriteComponent(StringBuilder sb,
                                         VirtualComponent c,
                                         DockingPortInfo dockingInfo,
                                         ref bool first) {
    switch (c) {
      // Order matters: NuclearEngine is a subclass of Engine, match
      // first. (Engine extras are reactor-specific.)
      case NuclearEngine n: {
        JsonWriter.Sep(sb, ref first);
        JsonWriter.Begin(sb, '[');
        bool f = true;
        WriteKind(sb, "N", ref f);
        WriteNum(sb, n.Thrust, ref f);
        WriteNum(sb, n.Isp, ref f);
        WriteNum(sb, n.IdleTempK, ref f);
        WriteNum(sb, n.OperatingTempK, ref f);
        WriteNum(sb, n.IdlePowerW, ref f);
        WriteNum(sb, n.MaxPowerW, ref f);
        WriteNum(sb, n.WarmupDurationSec, ref f);
        WriteNum(sb, n.SlewRatePerSec, ref f);
        WritePropellants(sb, n.Propellants, ref f);
        JsonWriter.End(sb, ']');
        return true;
      }
      case Engine e: {
        JsonWriter.Sep(sb, ref first);
        JsonWriter.Begin(sb, '[');
        bool f = true;
        WriteKind(sb, "E", ref f);
        WriteNum(sb, e.Thrust, ref f);
        WriteNum(sb, e.Isp, ref f);
        // GimbalRangeRad → degrees for the wire (every UI tooltip /
        // engine spec sheet is authored in degrees; converting at the
        // boundary keeps both consumers honest).
        WriteNum(sb, e.GimbalRangeRad * 180.0 / System.Math.PI, ref f);
        WritePropellants(sb, e.Propellants, ref f);
        JsonWriter.End(sb, ']');
        return true;
      }
      case Rcs rcs: {
        JsonWriter.Sep(sb, ref first);
        JsonWriter.Begin(sb, '[');
        bool f = true;
        WriteKind(sb, "M", ref f);
        WriteNum(sb, rcs.ThrusterPower, ref f);
        // ThrusterCount is set at OnStart for placed parts. For an
        // unplaced parts-list hover the live `ThrusterCount` is 0;
        // the caller scans the prefab's transforms and supplies the
        // count side-channel.
        WriteNum(sb, dockingInfo.RcsThrusterCount > 0
                       ? dockingInfo.RcsThrusterCount
                       : rcs.ThrusterCount, ref f);
        WriteNum(sb, rcs.Isp, ref f);
        WritePropellants(sb, rcs.Propellants, ref f);
        JsonWriter.End(sb, ']');
        return true;
      }
      case TankVolume t: {
        JsonWriter.Sep(sb, ref first);
        JsonWriter.Begin(sb, '[');
        bool f = true;
        WriteKind(sb, "T", ref f);
        WriteNum(sb, t.Volume, ref f);
        WriteNum(sb, t.MaxRate, ref f);
        JsonWriter.Sep(sb, ref f);
        JsonWriter.Begin(sb, '[');
        bool fs = true;
        for (int i = 0; i < t.Tanks.Count; i++) {
          var tank = t.Tanks[i];
          var tier = i < t.Tiers.Count ? t.Tiers[i] : InsulationTier.MLI;
          JsonWriter.Sep(sb, ref fs);
          JsonWriter.Begin(sb, '[');
          bool fi = true;
          JsonWriter.Sep(sb, ref fi);
          JsonWriter.WriteString(sb, tank.Resource?.Name ?? "");
          JsonWriter.Sep(sb, ref fi);
          JsonWriter.WriteDouble(sb, tank.Capacity);
          JsonWriter.Sep(sb, ref fi);
          JsonWriter.WriteDouble(sb, (int)tier);
          JsonWriter.End(sb, ']');
        }
        JsonWriter.End(sb, ']');
        JsonWriter.End(sb, ']');
        return true;
      }
      case Battery b: {
        JsonWriter.Sep(sb, ref first);
        JsonWriter.Begin(sb, '[');
        bool f = true;
        WriteKind(sb, "B", ref f);
        WriteNum(sb, b.Buffer?.Capacity ?? 0, ref f);
        // Battery uses a single MaxRate on the cfg, applied to both
        // MaxRateIn and MaxRateOut. Either side reads the same value.
        WriteNum(sb, b.Buffer?.MaxRateOut ?? 0, ref f);
        JsonWriter.End(sb, ']');
        return true;
      }
      case FuelCell fc: {
        JsonWriter.Sep(sb, ref first);
        JsonWriter.Begin(sb, '[');
        bool f = true;
        WriteKind(sb, "F", ref f);
        WriteNum(sb, fc.EcOutput, ref f);
        WriteNum(sb, fc.Lh2Rate, ref f);
        WriteNum(sb, fc.LoxRate, ref f);
        JsonWriter.End(sb, ']');
        return true;
      }
      case SolarPanel s: {
        JsonWriter.Sep(sb, ref first);
        JsonWriter.Begin(sb, '[');
        bool f = true;
        WriteKind(sb, "S", ref f);
        WriteNum(sb, s.ChargeRate, ref f);
        WriteBit(sb, s.IsTracking, ref f);
        WriteBit(sb, s.IsRetractable, ref f);
        JsonWriter.End(sb, ']');
        return true;
      }
      case Rtg rtg: {
        JsonWriter.Sep(sb, ref first);
        JsonWriter.Begin(sb, '[');
        bool f = true;
        WriteKind(sb, "R", ref f);
        WriteNum(sb, rtg.ReferencePower, ref f);
        WriteNum(sb, rtg.HalfLifeDays, ref f);
        WriteNum(sb, rtg.ThermalOutput, ref f);
        WriteNum(sb, rtg.MaxOperatingTempC, ref f);
        WriteNum(sb, rtg.VacuumRejectionW, ref f);
        WriteNum(sb, rtg.AtmRejectionW, ref f);
        JsonWriter.End(sb, ']');
        return true;
      }
      case ReactionWheel w: {
        JsonWriter.Sep(sb, ref first);
        JsonWriter.Begin(sb, '[');
        bool f = true;
        WriteKind(sb, "W", ref f);
        WriteNum(sb, w.PitchTorque, ref f);
        WriteNum(sb, w.YawTorque, ref f);
        WriteNum(sb, w.RollTorque, ref f);
        WriteNum(sb, w.ElectricRate, ref f);
        JsonWriter.End(sb, ']');
        return true;
      }
      case Radiator r: {
        JsonWriter.Sep(sb, ref first);
        JsonWriter.Begin(sb, '[');
        bool f = true;
        WriteKind(sb, "X", ref f);
        WriteNum(sb, r.VacuumCoolingW, ref f);
        WriteNum(sb, r.AtmCoolingW, ref f);
        WriteNum(sb, r.EcPerWattCooling, ref f);
        WriteBit(sb, r.IsDeployable, ref f);
        JsonWriter.End(sb, ']');
        return true;
      }
      case Light l: {
        JsonWriter.Sep(sb, ref first);
        JsonWriter.Begin(sb, '[');
        bool f = true;
        WriteKind(sb, "L", ref f);
        WriteNum(sb, l.Rate, ref f);
        JsonWriter.End(sb, ']');
        return true;
      }
      case Command cmd: {
        JsonWriter.Sep(sb, ref first);
        JsonWriter.Begin(sb, '[');
        bool f = true;
        WriteKind(sb, "C", ref f);
        WriteNum(sb, cmd.IdleDraw, ref f);
        WriteNum(sb, cmd.TestLoadRate, ref f);
        JsonWriter.End(sb, ']');
        return true;
      }
      case Probe p: {
        JsonWriter.Sep(sb, ref first);
        JsonWriter.Begin(sb, '[');
        bool f = true;
        WriteKind(sb, "P", ref f);
        WriteNum(sb, p.IdleDraw, ref f);
        WriteNum(sb, p.TestLoadRate, ref f);
        WriteNum(sb, p.SasLevel, ref f);
        WriteNum(sb, p.CommandCapacityBytes, ref f);
        WriteNum(sb, p.CommandDecayBps, ref f);
        WriteNum(sb, p.CommandReceiveRateBps, ref f);
        WriteNum(sb, p.InputCostBps, ref f);
        JsonWriter.End(sb, ']');
        return true;
      }
      case Antenna a: {
        JsonWriter.Sep(sb, ref first);
        JsonWriter.Begin(sb, '[');
        bool f = true;
        WriteKind(sb, "A", ref f);
        WriteNum(sb, a.TxPower, ref f);
        WriteNum(sb, a.Gain, ref f);
        WriteNum(sb, a.MaxRate, ref f);
        WriteNum(sb, a.RefDistance, ref f);
        JsonWriter.End(sb, ']');
        return true;
      }
      case Decoupler d: {
        JsonWriter.Sep(sb, ref first);
        JsonWriter.Begin(sb, '[');
        bool f = true;
        WriteKind(sb, "D", ref f);
        WriteNum(sb, d.EjectionForce, ref f);
        WriteBit(sb, d.CanFullSeparate, ref f);
        JsonWriter.Sep(sb, ref f);
        JsonWriter.Begin(sb, '[');
        bool fr = true;
        if (d.AllowedResources != null) {
          foreach (var res in d.AllowedResources) {
            JsonWriter.Sep(sb, ref fr);
            JsonWriter.WriteString(sb, res?.Name ?? "");
          }
        }
        JsonWriter.End(sb, ']');
        JsonWriter.End(sb, ']');
        return true;
      }
      case DockingPort _: {
        JsonWriter.Sep(sb, ref first);
        JsonWriter.Begin(sb, '[');
        bool f = true;
        WriteKind(sb, "K", ref f);
        JsonWriter.Sep(sb, ref f);
        JsonWriter.WriteString(sb, dockingInfo.NodeType ?? "");
        JsonWriter.End(sb, ']');
        return true;
      }
      case Crew y: {
        JsonWriter.Sep(sb, ref first);
        JsonWriter.Begin(sb, '[');
        bool f = true;
        WriteKind(sb, "Y", ref f);
        WriteNum(sb, y.Capacity, ref f);
        JsonWriter.End(sb, ']');
        return true;
      }
      case DataStorage z: {
        JsonWriter.Sep(sb, ref first);
        JsonWriter.Begin(sb, '[');
        bool f = true;
        WriteKind(sb, "Z", ref f);
        WriteNum(sb, z.CapacityBytes, ref f);
        JsonWriter.End(sb, ']');
        return true;
      }
      case Thermometer h: {
        JsonWriter.Sep(sb, ref first);
        JsonWriter.Begin(sb, '[');
        bool f = true;
        WriteKind(sb, "H", ref f);
        JsonWriter.Sep(sb, ref f);
        JsonWriter.WriteString(sb, h.InstrumentName ?? "");
        WriteNum(sb, h.EcRate, ref f);
        JsonWriter.End(sb, ']');
        return true;
      }
    }
    return false;
  }

  private static void WritePropellants(StringBuilder sb,
                                        List<Engine.Propellant> propellants,
                                        ref bool first) {
    JsonWriter.Sep(sb, ref first);
    JsonWriter.Begin(sb, '[');
    bool fp = true;
    if (propellants != null) {
      foreach (var p in propellants) {
        JsonWriter.Sep(sb, ref fp);
        JsonWriter.Begin(sb, '[');
        bool fi = true;
        JsonWriter.Sep(sb, ref fi);
        JsonWriter.WriteString(sb, p.Resource?.Name ?? "");
        JsonWriter.Sep(sb, ref fi);
        JsonWriter.WriteDouble(sb, p.Ratio);
        JsonWriter.End(sb, ']');
      }
    }
    JsonWriter.End(sb, ']');
  }

  // Rcs has its own `Rcs.Propellant` nested type — same shape (Resource +
  // Ratio) but a distinct C# type, so it needs its own overload. Keeping
  // the wire bytes identical between Engine and Rcs propellant lists.
  private static void WritePropellants(StringBuilder sb,
                                        List<Rcs.Propellant> propellants,
                                        ref bool first) {
    JsonWriter.Sep(sb, ref first);
    JsonWriter.Begin(sb, '[');
    bool fp = true;
    if (propellants != null) {
      foreach (var p in propellants) {
        JsonWriter.Sep(sb, ref fp);
        JsonWriter.Begin(sb, '[');
        bool fi = true;
        JsonWriter.Sep(sb, ref fi);
        JsonWriter.WriteString(sb, p.Resource?.Name ?? "");
        JsonWriter.Sep(sb, ref fi);
        JsonWriter.WriteDouble(sb, p.Ratio);
        JsonWriter.End(sb, ']');
      }
    }
    JsonWriter.End(sb, ']');
  }

  private static void WriteKind(StringBuilder sb, string kind, ref bool first) {
    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteString(sb, kind);
  }

  private static void WriteNum(StringBuilder sb, double v, ref bool first) {
    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteDouble(sb, v);
  }

  private static void WriteBit(StringBuilder sb, bool v, ref bool first) {
    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteBoolAsBit(sb, v);
  }
}
