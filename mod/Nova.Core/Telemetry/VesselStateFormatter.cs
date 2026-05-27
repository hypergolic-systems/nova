using System.Text;

namespace Nova.Core.Telemetry;

// Per-vessel dynamic-state wire frame. Used by NovaVesselState/<guid>.
//
// Wire format (positional):
//   [vesselId, vesselName,
//    situation, bodyName,
//    totalMassKg, partCount,
//    crewCount, crewCapacity]
//
// `situation` is the integer value of KSP's Vessel.Situations enum
// (0..7: Landed, Splashed, Prelaunch, Flying, Sub_Orbital, Orbiting,
// Escaping, Docked). The UI maps it to label + status colour; we
// never resolve the label at emit time so historical/archived
// records carry their own meaning.
//
// `totalMassKg` is SI base (kg), not KSP tonnes — wire is always SI.
// `bodyName` overlaps with NovaOrbitTopic by design: the rack consumes
// NovaVesselState exclusively; the top strip's Orbit cell continues
// to read its bodyName from NovaOrbitTopic. Joining two topics in the
// same frame is more fragile than carrying the field twice.
public static class VesselStateFormatter {
  public static void Write(StringBuilder sb,
      string vesselGuid, string vesselName,
      int situation, string bodyName,
      double totalMassKg, int partCount,
      int crewCount, int crewCapacity) {
    JsonWriter.Begin(sb, '[');
    bool first = true;

    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteString(sb, vesselGuid);
    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteString(sb, vesselName ?? "");

    JsonWriter.Sep(sb, ref first);
    sb.Append(situation);
    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteString(sb, bodyName ?? "");

    JsonWriter.Sep(sb, ref first);
    JsonWriter.WriteDouble(sb, totalMassKg);
    JsonWriter.Sep(sb, ref first);
    sb.Append(partCount);

    JsonWriter.Sep(sb, ref first);
    sb.Append(crewCount);
    JsonWriter.Sep(sb, ref first);
    sb.Append(crewCapacity);

    JsonWriter.End(sb, ']');
  }
}
