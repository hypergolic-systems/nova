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
}
