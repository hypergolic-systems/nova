using System.Collections.Generic;
using Nova.Core.Components.Control;
using Nova.Core.Components.Electrical;
using Nova.Core.Components.Propulsion;
using Nova.Core.Components.Science;
using Nova.Core.Components.Thermal;

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
  // Any part holding a non-zero-capacity Buffer — batteries, tanks,
  // monoprop pods. Drives the Resource view, where every storage-bearing
  // part shows up regardless of which subsystem it serves.
  public const string Storage      = "storage";
  // Volumetric propellant tank — parts carrying a TankVolume component.
  // Narrower than `Storage` (excludes batteries) so the editor's Tanks
  // view has a clean filter for parts whose mix is editable.
  public const string Tank         = "tank";
  // Science. Instrument = thermometer-class part; Storage = data drive
  // (separate from the resource Storage tag — different subsystem).
  public const string ScienceInstrument = "science-instrument";
  public const string ScienceStorage    = "science-storage";
  // Thermal subsystem — RTGs (heat producers) and radiators (consumers).
  // Distinct from PowerGen so the THERMAL tab can filter independently;
  // RTGs carry both tags.
  public const string Thermal           = "thermal";
  // Vessel control sources — Command (crewed pods) and Probe (probe
  // cores). The SYS tab's Communications accordion filters on this to
  // surface the StoredCommands ledger / control-authority view without
  // pulling every Power-consuming part.
  public const string CommandSource     = "command-source";

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
        case FuelCell _:
          set.Add(PowerGen);
          break;
        case Rtg _:
          set.Add(PowerGen);
          set.Add(Thermal);
          break;
        case Radiator _:
          set.Add(Thermal);
          break;
        case Battery _:
          set.Add(PowerStore);
          set.Add(Storage);
          break;
        case Light _:
          set.Add(PowerConsume);
          break;
        case ReactionWheel _:
          set.Add(PowerConsume);
          set.Add(Attitude);
          break;
        case Command cmd:
          if (cmd.IdleDraw > 0 || cmd.TestLoadRate > 0) set.Add(PowerConsume);
          set.Add(CommandSource);
          break;
        case Probe probe:
          if (probe.IdleDraw > 0 || probe.TestLoadRate > 0) set.Add(PowerConsume);
          set.Add(CommandSource);
          break;
        case Engine _:
          set.Add(Propulsion);
          break;
        case Rcs _:
          set.Add(Rcs);
          break;
        case TankVolume _:
          set.Add(Storage);
          set.Add(Tank);
          break;
        case Thermometer _:
          set.Add(ScienceInstrument);
          set.Add(PowerConsume);
          break;
        case DataStorage _:
          set.Add(ScienceStorage);
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
    PowerGen, PowerConsume, PowerStore, Propulsion, Rcs, Attitude, Storage, Tank,
    ScienceInstrument, ScienceStorage, Thermal, CommandSource,
  };
}
