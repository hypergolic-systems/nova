namespace Nova.Core.Science;

// Per-experiment static facts shared between the producing components,
// the storage layer, and the transmission system. Today just the byte
// cost of a file; the table grows as experiments do.
public static class ExperimentSpecs {

  // Wire size of one file produced by the given experiment, in bytes.
  // Storage uses this for capacity reservation; the transmission system
  // uses it as the Packet.TotalBytes when shipping a complete file home.
  public static long FileSizeFor(string experimentId) {
    return experimentId switch {
      AtmosphericProfileExperiment.ExperimentId => AtmosphericProfileExperiment.FileSizeBytes,
      LongTermStudyExperiment.ExperimentId      => LongTermStudyExperiment.FileSizeBytes,
      _                                          => 1_000,
    };
  }
}
