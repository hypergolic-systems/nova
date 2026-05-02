using System;
using System.Collections.Generic;

namespace Nova.Core.Science;

// "Profile the atmosphere by transit." A vessel inside an atmospheric
// layer can record the layer; on layer exit a file is emitted with
// fidelity = (captured pressure range) / (layer's full pressure range).
// To get fidelity 1.0 the vessel must traverse the full pressure span
// of the layer (top to bottom or vice versa). Subject = (body, layer
// name).
//
// Layer tables are stock-body-specific and live here directly — KSP 1
// is dead, the body roster is frozen, and per-mod-pack atmosphere
// tuning isn't a goal yet.
public static class AtmosphericProfileExperiment {
  public const string ExperimentId   = "atm-profile";
  public const long   FileSizeBytes  = 1_000;

  public readonly struct Layer {
    public readonly string name;
    public readonly double topAltMeters;
    /// <summary>Static atmospheric pressure (atm) at the layer's bottom — the
    /// higher of the two pressures. Stock-KSP defaults at sea level
    /// (or the previous layer's top).</summary>
    public readonly double bottomPressureAtm;
    /// <summary>Static pressure (atm) at the layer's top — the lower of
    /// the two pressures.</summary>
    public readonly double topPressureAtm;

    public Layer(string name, double topAltMeters, double bottomPressureAtm, double topPressureAtm) {
      this.name              = name;
      this.topAltMeters      = topAltMeters;
      this.bottomPressureAtm = bottomPressureAtm;
      this.topPressureAtm    = topPressureAtm;
    }
  }

  // Per-body layer table, bottom→top. Pressure bounds are stock-KSP
  // approximations from the body's atmosphere curve at the layer's
  // boundary altitudes; not exact, good enough for fidelity scoring.
  private static readonly Dictionary<string, Layer[]> Layers = new() {
    ["Kerbin"] = new[] {
      new Layer("troposphere",  18_000,  1.000, 0.092),
      new Layer("stratosphere", 45_000,  0.092, 0.005),
      new Layer("mesosphere",   70_000,  0.005, 0.000),
    },
    ["Eve"] = new[] {
      new Layer("troposphere",  30_000,  5.000, 0.380),
      new Layer("stratosphere", 60_000,  0.380, 0.020),
      new Layer("mesosphere",   90_000,  0.020, 0.000),
    },
    ["Duna"] = new[] {
      new Layer("troposphere",  15_000,  0.067, 0.013),
      new Layer("stratosphere", 35_000,  0.013, 0.001),
      new Layer("mesosphere",   50_000,  0.001, 0.000),
    },
    ["Jool"] = new[] {
      new Layer("troposphere",   50_000, 15.000, 1.300),
      new Layer("stratosphere", 120_000,  1.300, 0.050),
      new Layer("mesosphere",   200_000,  0.050, 0.000),
    },
    ["Laythe"] = new[] {
      new Layer("troposphere",  15_000,  0.600, 0.090),
      new Layer("stratosphere", 35_000,  0.090, 0.005),
      new Layer("mesosphere",   50_000,  0.005, 0.000),
    },
  };

  // Returns the layer name for the given (body, altitude), or null if
  // the body has no atmosphere or the vessel is above it.
  public static string LayerAt(string bodyName, double altitude) {
    if (!Layers.TryGetValue(bodyName, out var table)) return null;
    foreach (var l in table)
      if (altitude < l.topAltMeters) return l.name;
    return null;
  }

  // Returns the body's layer table (bottom→top), or null if the body
  // has no atmosphere.
  public static Layer[] LayersFor(string bodyName) =>
      Layers.TryGetValue(bodyName, out var table) ? table : null;

  // Returns the layer record for the given body+layer name, or null
  // when the layer isn't known.
  public static Layer? LayerByName(string bodyName, string layerName) {
    if (!Layers.TryGetValue(bodyName, out var table)) return null;
    foreach (var l in table) if (l.name == layerName) return l;
    return null;
  }

  // Pressure-coverage fidelity: how much of the layer's pressure span
  // has been captured between [recordedMinAtm, recordedMaxAtm]. Both
  // arguments must be in atm. Returns [0,1] with bounds-checking.
  public static double FidelityFromPressureCoverage(
      double recordedMinAtm, double recordedMaxAtm,
      double layerBottomAtm, double layerTopAtm) {
    double layerSpan = Math.Abs(layerBottomAtm - layerTopAtm);
    if (layerSpan <= 1e-12) return 0;
    double recorded = Math.Max(0, recordedMaxAtm - recordedMinAtm);
    return Math.Min(1.0, Math.Max(0.0, recorded / layerSpan));
  }

  public static SubjectKey? SubjectAt(string bodyName, double altitude) {
    var layer = LayerAt(bodyName, altitude);
    return layer != null ? new SubjectKey(ExperimentId, bodyName, layer) : (SubjectKey?)null;
  }
}
