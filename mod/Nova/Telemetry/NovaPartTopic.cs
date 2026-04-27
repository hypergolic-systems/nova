using System.Collections.Generic;
using System.Text;
using Dragonglass.Telemetry.Topics;
using Nova.Components;
using Nova.Core.Components;
using Nova.Core.Components.Electrical;
using Nova.Core.Components.Propulsion;
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
// Component frames:
//   ["S", currentEcRate, maxEcRate, deployed, sunlit, retractable]  SolarPanel
//   ["B", soc(0..1), capacity, currentRate]                         Battery
//   ["W", maxEcRate, activity(0..1)]                                ReactionWheel
//   ["L", maxEcRate, activity(0..1)]                                Light
//   ["E", alternatorMaxRate, alternatorRateEc]                      Engine
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

    var virt = ResolveVirtualVessel();

    JsonWriter.Sep(sb, ref first);
    WriteResources(sb, virt);

    JsonWriter.Sep(sb, ref first);
    WriteComponents(sb, virt);

    JsonWriter.End(sb, ']');
  }

  private VirtualVessel ResolveVirtualVessel() {
    if (_part == null || _part.vessel == null) return null;
    var vm = _part.vessel.GetComponent<NovaVesselModule>();
    return vm != null ? vm.Virtual : null;
  }

  private void WriteResources(StringBuilder sb, VirtualVessel virt) {
    JsonWriter.Begin(sb, '[');
    bool first = true;
    if (virt != null) {
      foreach (var c in virt.GetComponents(_part.persistentId)) {
        foreach (var buf in EnumerateBuffers(c)) {
          if (buf == null || buf.Capacity <= 0) continue;
          JsonWriter.Sep(sb, ref first);
          WriteResource(sb, buf);
        }
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

  private void WriteComponents(StringBuilder sb, VirtualVessel virt) {
    JsonWriter.Begin(sb, '[');
    bool first = true;
    if (virt != null) {
      foreach (var c in virt.GetComponents(_part.persistentId)) {
        if (!TryWriteComponent(sb, c, ref first)) {
          // Unhandled kind — silently skip rather than emit an
          // un-decodable frame. New kinds get a case here + a TS
          // tuple in nova-topics.ts.
        }
      }
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
        WriteNum(sb, wheel.ElectricRate, ref f);
        WriteNum(sb, wheel.Activity, ref f);
        JsonWriter.End(sb, ']');
        return true;
      }
      case NovaLight light: {
        JsonWriter.Sep(sb, ref first);
        JsonWriter.Begin(sb, '[');
        bool f = true;
        WriteKind(sb, "L", ref f);
        WriteNum(sb, light.Rate, ref f);
        WriteNum(sb, light.Activity, ref f);
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
