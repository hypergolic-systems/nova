using System;
using System.Collections.Generic;

namespace Nova.Core.Resources;

// Resource domain controls which solver owns this resource's flow.
//
//   Topological — flows along vessel topology (pipes, decouplers, up-only
//                 edges). Stored in tanks, drained by drain priority,
//                 staged-aware. Examples: RP-1, LOX, LH2, Hydrazine.
//                 Solved by StagingFlowSystem (water-fill).
//
//   Uniform     — single vessel-wide pool, no topology distinctions.
//                 May be cyclic (closed-loop life support, ISRU recycle).
//                 Examples: ElectricCharge today; O2/CO2/H2O/heat
//                 tomorrow. Solved by ProcessFlowSystem (LP).
//
// Resources don't switch domains. The Accumulator pattern bridges
// when a component needs to consume one domain's resource but produce
// in the other (e.g. fuel cell consumes Topological LH2/LOX, produces
// Uniform EC).
public enum ResourceDomain {
  Topological,
  Uniform,
}

public class Resource {
  public string Name;
  public string Abbreviation;
  public UnitDefinition Unit;
  public double Density; // kg/U
  public ResourceDomain Domain;

  // Cryogenic boiloff baseline — fraction of slice capacity lost per
  // Earth day under stock MLI insulation. Anchored to real spacecraft
  // cryo data (LH₂ ~3%/day, LOX ~1%/day, CH₄ ~0.5%/day at typical
  // tank surface-to-volume ratios). Storable / non-cryogenic resources
  // are 0 — no boiloff, no tier hardware needed. Used by TankVolume
  // together with InsulationTierTable to compute net boiloff per slice.
  public double MliBoiloffFractionPerDay;

  // Operating temperature (Kelvin) of the fluid in a tank — its boiling
  // point at the storage pressure. Drives the cryocooler's required
  // ΔT (against AmbientK) and Carnot COP, so LH₂ at 20 K costs
  // dramatically more EC per watt of cooling than LOX at 90 K under
  // the same insulation tier. 0 for storables / non-cryogenic.
  public double BoilingPointK;

  // Latent heat of vaporization in J/kg at the storage temperature.
  // Closes the loop between "fraction of tank lost per day" and the
  // actual heat-leak wattage the cooler must remove:
  //   Q_leak (W) = capacity × frac/day × density × Lv / 86400
  // 0 for storables / non-cryogenic.
  public double LatentHeatJPerKg;

  private static Dictionary<string, Resource> registry = new();

  public class UnitDefinition {
    public string Name;
    public string Symbol;

    public static UnitDefinition Watt = new() {
      Name = "Watt",
      Symbol = "W",
    };

    public static UnitDefinition Liter = new() {
      Name = "Liter",
      Symbol = "L",
    };
  }

  static Resource() {
    // Convention: EC quantity is Joules (1 EC = 1 J), EC rate is Watts
    // (1 EC/s = 1 W). The "Watt" label below is the canonical *rate*
    // unit; Buffer.Capacity / Buffer.Contents store energy in J, and
    // Buffer.Rate × dt = ΔContents lands in W·s = J as expected.
    registry["Electric Charge"] = new Resource {
      Name = "Electric Charge",
      Abbreviation = "EC",
      Unit = UnitDefinition.Watt,
      Density = 0,
      Domain = ResourceDomain.Uniform,
    };

    // Thermal energy. Storage in Joules, rate in Watts — same J/W
    // convention as EC. Heat buffers are per-device (private; never
    // added to ProcessFlowSystem) so heat from one producer can't
    // re-route into another producer's buffer through the LP's
    // proportional fill distribution.
    registry["Heat"] = new Resource {
      Name = "Heat",
      Abbreviation = "Heat",
      Unit = UnitDefinition.Watt,
      Density = 0,
      Domain = ResourceDomain.Uniform,
    };

    registry["Liquid Hydrogen"] = new Resource {
      Name = "Liquid Hydrogen",
      Abbreviation = "LH2",
      Unit = UnitDefinition.Liter,
      Density = 0.07,
      Domain = ResourceDomain.Topological,
      MliBoiloffFractionPerDay = 0.03,
      BoilingPointK = 20.3,
      LatentHeatJPerKg = 446_000,
    };

    registry["Liquid Oxygen"] = new Resource {
      Name = "Liquid Oxygen",
      Abbreviation = "LOX",
      Unit = UnitDefinition.Liter,
      Density = 1.2,
      Domain = ResourceDomain.Topological,
      MliBoiloffFractionPerDay = 0.01,
      BoilingPointK = 90.2,
      LatentHeatJPerKg = 213_000,
    };

    registry["RP-1"] = new Resource {
      Name = "RP-1",
      Abbreviation = "RP-1",
      Unit = UnitDefinition.Liter,
      Density = 0.8,
      Domain = ResourceDomain.Topological,
    };

    registry["Hydrazine"] = new Resource {
      Name = "Hydrazine",
      Abbreviation = "N2H4",
      Unit = UnitDefinition.Liter,
      Density = 1.0,
      Domain = ResourceDomain.Topological,
    };

    registry["Xenon"] = new Resource {
      Name = "Xenon",
      Abbreviation = "Xe",
      Unit = UnitDefinition.Liter,
      // KSP uses a density of around ~1 kg/L for Xenon. However, Dawn used supercritical Xenon at a
      // density of ~2 kg/L, so we use the denser value.
      Density = 2,
      Domain = ResourceDomain.Topological,
    };

    registry["Methane"] = new Resource {
      Name = "Methane",
      Abbreviation = "CH4",
      Unit = UnitDefinition.Liter,
      Density = 0.42,
      Domain = ResourceDomain.Topological,
      MliBoiloffFractionPerDay = 0.005,
      BoilingPointK = 111.7,
      LatentHeatJPerKg = 510_000,
    };

    // Nitrogen tetroxide. Hypergolic oxidizer for Hydrazine.
    registry["NTO"] = new Resource {
      Name = "NTO",
      Abbreviation = "NTO",
      Unit = UnitDefinition.Liter,
      Density = 1.45,
      Domain = ResourceDomain.Topological,
    };

    // Pre-mixed APCP-like solid grain. SRBs cast it as a single propellant
    // (no separate fuel/oxidizer plumbing), so a single resource is the
    // physically accurate model.
    registry["Solid Propellant"] = new Resource {
      Name = "Solid Propellant",
      Abbreviation = "APCP",
      Unit = UnitDefinition.Liter,
      Density = 1.8,
      Domain = ResourceDomain.Topological,
    };
  }

  public static Resource ElectricCharge => registry["Electric Charge"];
  public static Resource Heat => registry["Heat"];
  public static Resource LiquidHydrogen => registry["Liquid Hydrogen"];
  public static Resource LiquidOxygen => registry["Liquid Oxygen"];
  public static Resource RP1 => registry["RP-1"];
  public static Resource Hydrazine => registry["Hydrazine"];
  public static Resource Xenon => registry["Xenon"];
  public static Resource Methane => registry["Methane"];
  public static Resource NTO => registry["NTO"];
  public static Resource SolidPropellant => registry["Solid Propellant"];

  private Resource() {}


  public static Resource Get(string name) {
    return registry[name];
  }

  public static bool TryGet(string name, out Resource resource) {
    return registry.TryGetValue(name, out resource);
  }

  public static IEnumerable<Resource> All => registry.Values;
}