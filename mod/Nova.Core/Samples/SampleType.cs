namespace Nova.Core.Samples;

// A kind of sample a container can hold. Static descriptor — fields
// are set once at SampleRegistry bootstrap and never mutate. Multiple
// instances of the same type may live in one container; each carries
// its own runtime state via Sample.
public sealed class SampleType {
  public string Id;
  public string DisplayName;
  public double MassKg;
  public double ExposureDurationSec;
  public string ExperimentId;
}
