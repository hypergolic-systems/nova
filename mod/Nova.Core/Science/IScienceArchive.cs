using Nova.Core.Persistence.Protos;

namespace Nova.Core.Science;

// KSC-side receiver for completed science files. Implemented mod-side
// by NovaScienceArchive; injected into ScienceTransmissionSystem so the
// engine doesn't reach back into Nova.dll.
public interface IScienceArchive {
  void Receive(ScienceFile file, uint sourceVesselPersistentId, double ut);
}
