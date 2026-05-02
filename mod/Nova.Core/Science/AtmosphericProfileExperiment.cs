using System;
using System.Collections.Generic;

namespace Nova.Core.Science;

// "Profile the atmosphere by transit." A vessel inside an atmospheric
// layer can record the layer; on layer exit a file is emitted with
// fidelity = (captured altitude range) / (layer's full altitude span).
// To get fidelity 1.0 the vessel must traverse the full altitude span
// of the layer (top to bottom or vice versa). Subject = (body, layer
// name).
//
// Layer tables are stock-body-specific and live here directly — KSP 1
// is dead, the body roster is frozen, and per-mod-pack atmosphere
// tuning isn't a goal yet.
public static class AtmosphericProfileExperiment {
  public const string ExperimentId   = "atm-profile";
  public const long   FileSizeBytes  = 1_000;

  // Below this altitude (m) the experiment doesn't gather data —
  // ground-level operations should read as "Surface", not as a tiny
  // sliver of the troposphere. Applied uniformly across atmosphere
  // bodies; troposphere fidelity-span uses this as its effective
  // bottom.
  public const double SurfaceFloorMeters = 1_000;

  public readonly struct Layer {
    public readonly string name;
    public readonly double topAltMeters;

    public Layer(string name, double topAltMeters) {
      this.name         = name;
      this.topAltMeters = topAltMeters;
    }
  }

  // Per-body layer table, bottom→top.
  private static Dictionary<string, Layer[]> Layers = new() {
    ["Kerbin"] = new[] {
      new Layer("troposphere",  18_000),
      new Layer("stratosphere", 45_000),
      new Layer("mesosphere",   70_000),
    },
    ["Eve"] = new[] {
      new Layer("troposphere",  30_000),
      new Layer("stratosphere", 60_000),
      new Layer("mesosphere",   90_000),
    },
    ["Duna"] = new[] {
      new Layer("troposphere",  15_000),
      new Layer("stratosphere", 35_000),
      new Layer("mesosphere",   50_000),
    },
    ["Jool"] = new[] {
      new Layer("troposphere",   50_000),
      new Layer("stratosphere", 120_000),
      new Layer("mesosphere",   200_000),
    },
    ["Laythe"] = new[] {
      new Layer("troposphere",  15_000),
      new Layer("stratosphere", 35_000),
      new Layer("mesosphere",   50_000),
    },
  };

  // Returns the layer name for the given (body, altitude), or null if
  // the body has no atmosphere, the vessel is above it, OR the vessel
  // is below the surface floor (so callers stop gathering data).
  public static string LayerAt(string bodyName, double altitude) {
    if (altitude < SurfaceFloorMeters) return null;
    if (!Layers.TryGetValue(bodyName, out var table)) return null;
    foreach (var l in table)
      if (altitude < l.topAltMeters) return l.name;
    return null;
  }

  // Returns the body's layer table (bottom→top), or null if the body
  // has no atmosphere.
  public static Layer[] LayersFor(string bodyName) =>
      Layers.TryGetValue(bodyName, out var table) ? table : null;

  // Body names with a known layer table.
  public static IEnumerable<string> KnownBodies => Layers.Keys;

  // Returns the layer record for the given body+layer name, or null
  // when the layer isn't known.
  public static Layer? LayerByName(string bodyName, string layerName) {
    if (!Layers.TryGetValue(bodyName, out var table)) return null;
    foreach (var l in table) if (l.name == layerName) return l;
    return null;
  }

  // Effective bottom altitude (m) for the given layer — the previous
  // layer's top, or `SurfaceFloorMeters` for the first layer (since
  // data isn't collected below the floor, the floor IS the layer's
  // collectible bottom). Returns null if the layer isn't known.
  public static double? LayerBottomAlt(string bodyName, string layerName) {
    if (!Layers.TryGetValue(bodyName, out var table)) return null;
    double prevTop = SurfaceFloorMeters;
    foreach (var l in table) {
      if (l.name == layerName) return prevTop;
      prevTop = l.topAltMeters;
    }
    return null;
  }

  // Altitude-coverage fidelity: how much of the layer's altitude span
  // has been captured between [recordedMinAltM, recordedMaxAltM]. All
  // arguments in meters. Returns [0,1] with bounds-checking.
  public static double FidelityFromAltCoverage(
      double recordedMinAltM, double recordedMaxAltM,
      double layerBottomAltM, double layerTopAltM) {
    double layerSpan = Math.Abs(layerTopAltM - layerBottomAltM);
    if (layerSpan <= 1e-9) return 0;
    double recorded = Math.Max(0, recordedMaxAltM - recordedMinAltM);
    return Math.Min(1.0, Math.Max(0.0, recorded / layerSpan));
  }

  public static SubjectKey? SubjectAt(string bodyName, double altitude) {
    var layer = LayerAt(bodyName, altitude);
    return layer != null ? new SubjectKey(ExperimentId, bodyName, layer) : (SubjectKey?)null;
  }
}
