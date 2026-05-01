using System.Collections.Generic;

namespace Nova.Core.Science;

// "Profile the atmosphere by transit." A vessel inside an atmospheric
// layer can record the layer; on layer crossings a new file is emitted.
// Subject = (body, layer name). Always fidelity 1.0 (instantaneous
// observation, not time-integrated).
//
// Layer tables are stock-body-specific and live here directly — KSP 1
// is dead, the body roster is frozen, and per-mod-pack atmosphere
// tuning isn't a goal yet.
public static class AtmosphericProfileExperiment {
  public const string ExperimentId   = "atm-profile";
  public const long   FileSizeBytes  = 1_000;

  // Top altitudes (meters) per layer, bottom-to-top. Above the topmost
  // layer the experiment is not applicable.
  private static readonly Dictionary<string, (string name, double top)[]> Layers = new() {
    ["Kerbin"] = new[] {
      ("troposphere",  18_000.0),
      ("stratosphere", 45_000.0),
      ("mesosphere",   70_000.0),
    },
    ["Eve"] = new[] {
      ("troposphere",  30_000.0),
      ("stratosphere", 60_000.0),
      ("mesosphere",   90_000.0),
    },
    ["Duna"] = new[] {
      ("troposphere",  15_000.0),
      ("stratosphere", 35_000.0),
      ("mesosphere",   50_000.0),
    },
    ["Jool"] = new[] {
      ("troposphere",   50_000.0),
      ("stratosphere", 120_000.0),
      ("mesosphere",   200_000.0),
    },
    ["Laythe"] = new[] {
      ("troposphere",  15_000.0),
      ("stratosphere", 35_000.0),
      ("mesosphere",   50_000.0),
    },
  };

  // Returns the layer name for the given (body, altitude), or null if
  // the body has no atmosphere or the vessel is above it.
  public static string LayerAt(string bodyName, double altitude) {
    if (!Layers.TryGetValue(bodyName, out var table)) return null;
    foreach (var (name, top) in table)
      if (altitude < top) return name;
    return null;
  }

  public static SubjectKey? SubjectAt(string bodyName, double altitude) {
    var layer = LayerAt(bodyName, altitude);
    return layer != null ? new SubjectKey(ExperimentId, bodyName, layer) : (SubjectKey?)null;
  }
}
