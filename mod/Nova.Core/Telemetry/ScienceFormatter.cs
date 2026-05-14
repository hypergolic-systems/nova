using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Nova.Core.Components.Science;
using Nova.Core.Persistence.Protos;
using Nova.Core.Science;

namespace Nova.Core.Telemetry;

// Per-part science wire frame. Lives on parts hosting one or more
// science instruments (Thermometer today; more kinds later).
//
// Wire format:
//   [partId,
//    [
//      [instrumentName, [ ['EXA', ...], ['EXL', ...] ]],   // one per instrument
//      ...
//    ]
//   ]
//
// Caller responsibilities (mod ↔ sim divergence):
//   - simNowUt: live UT for slice-progress math.
//   - atmTempK: live atmospheric temperature in Kelvin. Mod reads
//     `vessel.atmosphericTemperature`; sim passes 0 (no atmosphere
//     model in v1).
//   - titleResolver: maps a partId to the player-facing display title
//     for "where this science will land" callout. Mod resolves via
//     KSP's AvailablePart; sim via the PartDatabase's `.cfg` title.
//     Returning null/"" is fine — frame just emits no destination.
public static class ScienceFormatter {
  public static void Write(StringBuilder sb, uint partId,
      IEnumerable<Thermometer> thermometers,
      double simNowUt, double atmTempK,
      Func<uint, string> titleResolver) {
    JsonWriter.Begin(sb, '[');
    bool first = true;

    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteUintAsString(sb, partId);

    JsonWriter.Sep(sb, ref first);
    JsonWriter.Begin(sb, '[');
    bool firstInstr = true;
    if (thermometers != null) {
      foreach (var t in thermometers) {
        JsonWriter.Sep(sb, ref firstInstr);
        WriteInstrument(sb, t, simNowUt, atmTempK, titleResolver);
      }
    }
    JsonWriter.End(sb, ']');

    JsonWriter.End(sb, ']');
  }

  private static void WriteInstrument(StringBuilder sb, Thermometer thermometer,
      double simNowUt, double atmTempK, Func<uint, string> titleResolver) {
    JsonWriter.Begin(sb, '[');
    bool fi = true;
    JsonWriter.Sep(sb, ref fi);
    JsonWriter.WriteString(sb, thermometer.InstrumentName ?? "");

    JsonWriter.Sep(sb, ref fi);
    JsonWriter.Begin(sb, '[');
    bool firstFrame = true;
    var savedLocal = BuildLocalFidelityIndex(thermometer);
    WriteAtmExperimentFrame(sb, thermometer, simNowUt, atmTempK, titleResolver, savedLocal, ref firstFrame);
    WriteLtsExperimentFrame(sb, thermometer, simNowUt, titleResolver, savedLocal, ref firstFrame);
    JsonWriter.End(sb, ']');

    JsonWriter.End(sb, ']');
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

  private static string ResolveDestinationStorage(Thermometer t, long fileSizeBytes,
      Func<uint, string> titleResolver) {
    if (t.Vessel == null || titleResolver == null) return "";
    foreach (var partId in t.Vessel.AllPartIds()) {
      foreach (var c in t.Vessel.GetComponents(partId)) {
        if (c is DataStorage storage && storage.CanDeposit(fileSizeBytes)) {
          return titleResolver(partId) ?? "";
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

  private static void WriteAtmExperimentFrame(StringBuilder sb, Thermometer t,
      double simNowUt, double atmTempK, Func<uint, string> titleResolver,
      Dictionary<string, double> savedLocal, ref bool first) {
    string body = t.Vessel?.Context?.BodyName ?? "";
    double altitude = t.Vessel?.Context?.Altitude ?? 0;
    var layers = AtmosphericProfileExperiment.LayersFor(body);
    string currentLayer = AtmosphericProfileExperiment.LayerAt(body, altitude);
    if (currentLayer == null && layers != null && altitude >= 0
        && altitude < AtmosphericProfileExperiment.SurfaceFloorMeters) {
      currentLayer = "surface";
    }
    currentLayer ??= "";
    string destination = ResolveDestinationStorage(t,
        AtmosphericProfileExperiment.FileSizeBytes, titleResolver);

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
    // seal on layer exit); always 1.
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
    WriteNum(sb, atmTempK, ref f);

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

  private static void WriteLtsExperimentFrame(StringBuilder sb, Thermometer t,
      double simNowUt, Func<uint, string> titleResolver,
      Dictionary<string, double> savedLocal, ref bool first) {
    var ctx       = t.Vessel?.Context;
    string body   = ctx?.BodyName ?? "";
    string parent = ctx?.SolarParentName ?? body;
    var situation = ctx?.Situation ?? Situation.SrfLanded;
    double bodyYearSeconds = ctx?.BodyYearSeconds ?? 0;

    int slicesPerYear = LongTermStudyExperiment.SlicesPerYear;
    int currentSliceIndex = bodyYearSeconds > 0
        ? LongTermStudyExperiment.SliceIndexAt(simNowUt, bodyYearSeconds)
        : 0;
    double phase = bodyYearSeconds > 0
        ? (simNowUt - System.Math.Floor(simNowUt / bodyYearSeconds) * bodyYearSeconds) / bodyYearSeconds
        : 0;

    string ltsSubjectId = bodyYearSeconds > 0 && situation != Situation.None
        ? LongTermStudyExperiment.SubjectFor(body, situation, simNowUt, bodyYearSeconds).ToString()
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
      double covered = System.Math.Min(simNowUt, ltsFile.EndUt) - ltsFile.StartUt;
      activeFidelity = System.Math.Min(1.0, System.Math.Max(0.0, covered / ltsFile.SliceDurationSeconds));
      recordedMin = (ltsFile.StartUt
                    - System.Math.Floor(ltsFile.StartUt / bodyYearSeconds) * bodyYearSeconds)
                    / bodyYearSeconds;
      double cappedNow = System.Math.Min(simNowUt, ltsFile.EndUt);
      recordedMax = (cappedNow
                    - System.Math.Floor(cappedNow / bodyYearSeconds) * bodyYearSeconds)
                    / bodyYearSeconds;
      willComplete = (span / ltsFile.SliceDurationSeconds) >= 0.99;
    }

    string destination = ResolveDestinationStorage(t,
        LongTermStudyExperiment.FileSizeBytes, titleResolver);

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
