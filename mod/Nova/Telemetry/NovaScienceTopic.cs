using System.Collections.Generic;
using System.Linq;
using System.Text;
using Dragonglass.Telemetry.Topics;
using Nova.Components;
using Nova.Core.Components.Science;
using Nova.Core.Persistence.Protos;
using Nova.Core.Science;
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
    JsonWriter.Begin(sb, '[');
    bool first = true;

    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteUintAsString(sb, _part.persistentId);

    JsonWriter.Sep(sb, ref first);
    JsonWriter.Begin(sb, '[');
    bool firstInstr = true;
    foreach (var thermometer in ResolveThermometers()) {
      JsonWriter.Sep(sb, ref firstInstr);
      JsonWriter.Begin(sb, '[');
      bool fi = true;
      JsonWriter.Sep(sb, ref fi);
      JsonWriter.WriteString(sb, thermometer.InstrumentName ?? "");

      JsonWriter.Sep(sb, ref fi);
      JsonWriter.Begin(sb, '[');
      bool firstFrame = true;
      // Build a subjectId → max-fidelity dict once across every
      // DataStorage on the vessel; reused by both EXA and EXL.
      var savedLocal = BuildLocalFidelityIndex(thermometer);
      WriteAtmExperimentFrame(sb, thermometer, _part.vessel, savedLocal, ref firstFrame);
      WriteLtsExperimentFrame(sb, thermometer, _part.vessel, savedLocal, ref firstFrame);
      JsonWriter.End(sb, ']');
      JsonWriter.End(sb, ']');
    }
    JsonWriter.End(sb, ']');

    JsonWriter.End(sb, ']');
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

  // --- Helpers (lifted from NovaPartTopic) ---------------------------

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
    var layers = AtmosphericProfileExperiment.LayersFor(body);
    // currentLayerName: layer name when in an applicable layer, the
    // sentinel "surface" when below the per-body floor (so the UI
    // renders "Surface" without counting fidelity), or "" above
    // atmosphere / no atmosphere.
    string currentLayer = AtmosphericProfileExperiment.LayerAt(body, altitude);
    if (currentLayer == null && layers != null && altitude >= 0
        && altitude < AtmosphericProfileExperiment.SurfaceFloorMeters) {
      currentLayer = "surface";
    }
    currentLayer ??= "";
    string destination = ResolveDestinationStorage(t, kspVessel, AtmosphericProfileExperiment.FileSizeBytes);

    // Recorded bounds come from the live file in storage for the
    // current layer. No file ⇒ no observation captured yet ⇒ zero
    // sentinels. Files persist across disable so the bounds remain
    // visible even when the experiment is currently OFF.
    var activeFile = !string.IsNullOrEmpty(currentLayer) && currentLayer != "surface"
        ? FindFileForSubject(t, AtmosphericProfileExperiment.ExperimentId + "@" + body + ":" + currentLayer)
        : null;
    bool hasBracket = activeFile != null;
    double transitMinAlt = hasBracket ? activeFile.RecordedMinAltM : 0;
    double transitMaxAlt = hasBracket ? activeFile.RecordedMaxAltM : 0;

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
    JsonWriter.Sep(sb, ref f);
    JsonWriter.WriteString(sb, destination);
    JsonWriter.Sep(sb, ref f);
    JsonWriter.WriteString(sb, body);
    WriteNum(sb, altitude, ref f);

    // Live atmospheric temperature at the vessel's position (Kelvin).
    // Sourced from the loaded vessel's flight integrator — same value
    // the in-game stock thermometer would read. The UI accumulates
    // these per-frame samples to draw a temperature-vs-altitude curve;
    // we don't ship sample arrays on the wire.
    double tempK = kspVessel?.atmosphericTemperature ?? 0;
    WriteNum(sb, tempK, ref f);

    // layers: [[name, topAlt]…]
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
        JsonWriter.End(sb, ']');
      }
    }
    JsonWriter.End(sb, ']');

    // savedLocal: [[layerName, fidelity]…] — only present layers.
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
