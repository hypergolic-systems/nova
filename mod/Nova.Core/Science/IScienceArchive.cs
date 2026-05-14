using Nova.Core.Persistence.Protos;

namespace Nova.Core.Science;

// KSC-side receiver for completed science files. Implemented mod-side
// by NovaScienceArchive; injected into ScienceTransmissionSystem so the
// engine doesn't reach back into Nova.dll.
//
// `sourceVesselName` is captured at receive-time and persisted on the
// archive record so historical attribution survives the source vessel
// being destroyed, unloaded, or renamed.
public interface IScienceArchive {
  void Receive(ScienceFile file,
               uint sourceVesselPersistentId,
               string sourceVesselName,
               double ut);

  // Read-side: look up a previously-received record by subject id.
  // Used by the archive telemetry formatter to render per-(body,
  // experiment, subject) cells with their saved fidelity, receive
  // time, and source vessel name. Returns false if the subject hasn't
  // been archived yet.
  bool TryGet(string subjectId, out ArchivedScienceRecord record);
}
