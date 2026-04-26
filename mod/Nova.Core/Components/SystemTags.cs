using System.Collections.Generic;
using Nova.Core.Components.Electrical;
using Nova.Core.Components.Propulsion;

namespace Nova.Core.Components;

// Maps Nova virtual components → coarse "system" tags that the UI
// uses as a filter for which parts populate which view (Power view
// pulls "power-gen" / "power-consume" / "power-store"; future views
// add more tags). Lives in Core because the categorization is a
// property of the components themselves, not of the telemetry layer
// — and Core hosting it keeps Nova.Tests' assembly graph intact.
public static class SystemTags {
  public const string PowerGen     = "power-gen";
  public const string PowerConsume = "power-consume";
  public const string PowerStore   = "power-store";
  public const string Propulsion   = "propulsion";
  public const string Rcs          = "rcs";
  public const string Attitude     = "attitude";

  /// <summary>
  /// Compute the deterministic, deduplicated tag list for the given
  /// component list. Order follows <see cref="Order"/> so consumers
  /// can compare two tag lists structurally without sorting.
  /// </summary>
  public static List<string> For(IEnumerable<VirtualComponent> components) {
    var set = new HashSet<string>();
    foreach (var c in components) {
      switch (c) {
        case SolarPanel _:
          set.Add(PowerGen);
          break;
        case Battery _:
          set.Add(PowerStore);
          break;
        case Light _:
          set.Add(PowerConsume);
          break;
        case ReactionWheel _:
          set.Add(PowerConsume);
          set.Add(Attitude);
          break;
        case Engine e:
          set.Add(Propulsion);
          if (e.AlternatorRate > 0) set.Add(PowerGen);
          break;
        case Rcs _:
          set.Add(Rcs);
          break;
      }
    }
    var result = new List<string>(set.Count);
    foreach (var tag in Order) {
      if (set.Contains(tag)) result.Add(tag);
    }
    return result;
  }

  // Canonical ordering used by `For` so list equality is meaningful.
  private static readonly string[] Order = new[] {
    PowerGen, PowerConsume, PowerStore, Propulsion, Rcs, Attitude,
  };
}
