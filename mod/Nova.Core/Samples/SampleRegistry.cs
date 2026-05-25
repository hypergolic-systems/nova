using System.Collections.Generic;

namespace Nova.Core.Samples;

// Global registry of sample types. Mirrors Resource.cs: types are
// declared in this static ctor and accessed by id or via the named
// static getters below. No .cfg-driven registration in v1 — adding
// a new type means a new entry here.
public static class SampleRegistry {
  private static readonly Dictionary<string, SampleType> registry = new();

  static SampleRegistry() {
    // Mystery Goo — stock-equivalent organic compound that reacts in
    // exposure to ambient conditions. Two flavors prove the multi-type
    // contract; both share the same experiment id (single archive
    // entry per body+situation) but differ in mass and exposure time.
    registry["mystery-goo-prime"] = new SampleType {
      Id                  = "mystery-goo-prime",
      DisplayName         = "Mystery Goo (Prime)",
      MassKg              = 0.05,
      ExposureDurationSec = 30,
      ExperimentId        = "mystery-goo",
    };
    registry["mystery-goo-dark"] = new SampleType {
      Id                  = "mystery-goo-dark",
      DisplayName         = "Mystery Goo (Dark)",
      MassKg              = 0.08,
      ExposureDurationSec = 90,
      ExperimentId        = "mystery-goo",
    };
  }

  public static SampleType MysteryGooPrime => registry["mystery-goo-prime"];
  public static SampleType MysteryGooDark  => registry["mystery-goo-dark"];

  public static SampleType Get(string id) => registry[id];

  public static bool TryGet(string id, out SampleType type) =>
      registry.TryGetValue(id, out type);

  public static IEnumerable<SampleType> All => registry.Values;
}
