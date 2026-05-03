using System.Collections.Generic;
using System.Linq;
using Nova.Core.Resources;

namespace Nova.Core.Components.Propulsion;

// Presets for the editor's "Set Tank Config" right-click action.
// Each Build callback turns a target tank Volume (litres) into a
// fresh Buffer list, default-full. Per-buffer rate caps are derived
// at solver-build time from the parent TankVolume.MaxRate proportioned
// by capacity share — these preset buffers carry only Resource +
// Capacity + Contents.
//
// Mix ratios: chosen volumetrically so that with Nova's resource
// densities (Resource.cs: LH2=0.07, LOx=1.2, RP-1=0.8 kg/L) the
// resulting mass ratios match engine-typical operating points,
// not stoichiometric. This mirrors the existing kerolox tanks
// (set in 0299f1a) and keeps O/F consistent with the engine's
// PROPELLANT ratios.
//
//   kerolox  60% LOx + 40% RP-1 by volume → 2.25:1 LOx:RP-1 by mass
//   hydrolox 26% LOx + 74% LH2 by volume → ~6:1   LOx:LH2 by mass
//
// Keep TANK_PRESETS in ui/apps/nova/src/editor/tank-presets.ts in
// sync — adding a preset requires both files.
public static class TankPresets {
  public sealed class Preset {
    public string Id;
    public string Label;
    public System.Func<double, List<Buffer>> Build;
  }

  public static IReadOnlyList<Preset> All { get; } = new[] {
    new Preset { Id = "n2h4",     Label = "Hydrazine",       Build = v => Pure(v, Resource.Hydrazine) },
    new Preset { Id = "rp1",      Label = "RP-1",            Build = v => Pure(v, Resource.RP1) },
    new Preset { Id = "lox",      Label = "Liquid Oxygen",   Build = v => Pure(v, Resource.LiquidOxygen) },
    new Preset { Id = "lh2",      Label = "Liquid Hydrogen", Build = v => Pure(v, Resource.LiquidHydrogen) },
    new Preset { Id = "kerolox",  Label = "RP-1 + LOx",      Build = v => Mix(v, (Resource.RP1, 0.40), (Resource.LiquidOxygen, 0.60)) },
    new Preset { Id = "hydrolox", Label = "LH2 + LOx",       Build = v => Mix(v, (Resource.LiquidHydrogen, 0.74), (Resource.LiquidOxygen, 0.26)) },
  };

  public static Preset GetById(string id) => All.FirstOrDefault(p => p.Id == id);

  private static List<Buffer> Pure(double volume, Resource resource) =>
    new List<Buffer> { MakeBuffer(resource, volume) };

  private static List<Buffer> Mix(double volume, params (Resource resource, double fraction)[] parts) =>
    parts.Select(p => MakeBuffer(p.resource, volume * p.fraction)).ToList();

  private static Buffer MakeBuffer(Resource resource, double capacity) =>
    new Buffer {
      Resource = resource,
      Capacity = capacity,
      Contents = capacity,
    };
}
