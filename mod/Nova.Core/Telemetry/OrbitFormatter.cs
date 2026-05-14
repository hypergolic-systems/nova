using System.Text;

namespace Nova.Core.Telemetry;

// Per-vessel orbit + mission-time wire frame.
//
// Wire format (positional):
//   [vesselId, bodyName,
//    apA, peA, eccentricity, inclination, period,
//    missionTime, launchTime]
//
// `inclination` is in degrees. `period` is 0 for sub-orbital /
// hyperbolic trajectories (eccentricity ≥ 1) — callers pass 0
// directly. `apA` / `peA` are 0 only when no orbit is available.
public static class OrbitFormatter {
  public static void Write(StringBuilder sb, string vesselGuid, string bodyName,
      double apA, double peA, double eccentricity, double inclination, double period,
      double missionTime, double launchTime) {
    JsonWriter.Begin(sb, '[');
    bool first = true;

    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteString(sb, vesselGuid);
    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteString(sb, bodyName ?? "");

    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteDouble(sb, apA);
    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteDouble(sb, peA);
    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteDouble(sb, eccentricity);
    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteDouble(sb, inclination);
    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteDouble(sb, period);

    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteDouble(sb, missionTime);
    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteDouble(sb, launchTime);

    JsonWriter.End(sb, ']');
  }
}
