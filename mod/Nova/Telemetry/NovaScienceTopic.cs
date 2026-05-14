using System.Collections.Generic;
using System.Linq;
using System.Text;
using Dragonglass.Telemetry.Topics;
using Nova.Components;
using Nova.Core.Components.Science;
using Nova.Core.Persistence.Protos;
using Nova.Core.Science;
using Nova.Core.Telemetry;
using UnityEngine;

namespace Nova.Telemetry;

// Per-part science topic. Lives on parts that host one or more
// science-instrument virtual components (Thermometer today, more
// kinds later). Publishes one inner tuple per instrument on the
// part — multi-instrument parts are first-class.
//
// Wire format:
//   [partId,
//    [
//      [instrumentName, [ ['EXA', ...], ['EXL', ...] ]],   // one per instrument
//      ...
//    ]
//   ]
//
// Each experiment frame's `experimentId` field at position 1
// doubles as the capability descriptor — the UI derives the
// instrument's supported-experiment id list by reading it off the
// frames, so the wire stays single-source-of-truth.
//
// Inbound ops (UI → mod):
//   "setExperimentEnabled" [instrumentIndex: int, experimentId: string, enabled: bool]
//      Toggle a specific experiment on/off on the instrument at the
//      given index in the part's instrument list (matches the wire
//      emit order). Rising-edge enable discards any in-progress file
//      for the current subject so the fresh observation starts clean
//      (files = unbroken periods).
public sealed class NovaScienceTopic : Topic {
  private const string LogPrefix = "[Nova/Telemetry] ";

  private Part _part;
  private string _name;

  private static readonly Dictionary<uint, NovaScienceTopic> _byPart
      = new Dictionary<uint, NovaScienceTopic>();

  public override string Name => _name;

  protected override void OnEnable() {
    _part = GetComponent<Part>();
    if (_part == null) {
      Debug.LogWarning(LogPrefix + "NovaScienceTopic attached to non-Part GameObject; disabling");
      enabled = false;
      return;
    }
    _name = "NovaScience/" + _part.persistentId;
    _byPart[_part.persistentId] = this;
    base.OnEnable();
    MarkDirty();
  }

  protected override void OnDisable() {
    base.OnDisable();
    if (_part != null) _byPart.Remove(_part.persistentId);
  }

  public static void MarkPartDirty(uint partPersistentId) {
    if (_byPart.TryGetValue(partPersistentId, out var topic) && topic != null) {
      topic.MarkDirty();
    }
  }

  public override void HandleOp(string op, List<object> args) {
    switch (op) {
      case "setExperimentEnabled": {
        if (args == null || args.Count < 3
            || !TryAsInt(args[0], out int instrumentIndex)
            || !(args[1] is string experimentId)
            || !(args[2] is bool enabled)) {
          Debug.LogWarning(LogPrefix + Name + " setExperimentEnabled: expected [int, string, bool]");
          return;
        }
        var vesselModule = _part?.vessel?.GetComponent<NovaVesselModule>();
        var thermometers = vesselModule?.Virtual?.GetComponents(_part.persistentId)
            .OfType<Thermometer>()
            .ToList();
        if (thermometers == null
            || instrumentIndex < 0
            || instrumentIndex >= thermometers.Count) return;
        var thermometer = thermometers[instrumentIndex];
        if (experimentId == AtmosphericProfileExperiment.ExperimentId) {
          // Rising edge: discard any prior in-progress file for the
          // current layer so the fresh observation starts clean. Files
          // represent unbroken observation periods.
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
      default:
        base.HandleOp(op, args);
        return;
    }
  }

  public override void WriteData(StringBuilder sb) {
    double atmTempK = _part?.vessel?.atmosphericTemperature ?? 0;
    var kspVessel = _part?.vessel;
    ScienceFormatter.Write(sb, _part.persistentId, ResolveThermometers(),
        Planetarium.GetUniversalTime(), atmTempK,
        partId => ResolvePartTitle(kspVessel, partId));
  }

  private static string ResolvePartTitle(Vessel kspVessel, uint partId) {
    if (kspVessel == null) return null;
    var hostPart = kspVessel.parts.FirstOrDefault(p => p.persistentId == partId);
    return hostPart?.partInfo?.title ?? hostPart?.partName;
  }

  // In flight, components live on NovaVesselModule.Virtual. In editor
  // there's no vessel — read from the live NovaPartModule.Components
  // list. Returns every Thermometer hosted by the part (multi-instrument
  // parts are first-class on the wire).
  private IEnumerable<Thermometer> ResolveThermometers() {
    if (_part == null) return Enumerable.Empty<Thermometer>();
    if (_part.vessel != null) {
      var vm = _part.vessel.GetComponent<NovaVesselModule>();
      var c = vm?.Virtual?.GetComponents(_part.persistentId);
      if (c != null) return c.OfType<Thermometer>();
    }
    var modules = _part.Modules?.OfType<NovaPartModule>();
    if (modules == null) return Enumerable.Empty<Thermometer>();
    return modules
      .Where(m => m.Components != null)
      .SelectMany(m => m.Components)
      .OfType<Thermometer>();
  }

  // Op args arrive as boxed primitives — JSON numbers may land as long
  // / double / int depending on serializer settings. Accept any of
  // them and coerce to int so callers don't have to know.
  private static bool TryAsInt(object o, out int value) {
    switch (o) {
      case int i:    value = i; return true;
      case long l:   value = (int)l; return true;
      case double d: value = (int)d; return true;
      default:       value = 0; return false;
    }
  }
}
