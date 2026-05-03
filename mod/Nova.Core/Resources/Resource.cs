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

    registry["Liquid Hydrogen"] = new Resource {
      Name = "Liquid Hydrogen",
      Abbreviation = "LH2",
      Unit = UnitDefinition.Liter,
      Density = 0.07,
      Domain = ResourceDomain.Topological,
    };

    registry["Liquid Oxygen"] = new Resource {
      Name = "Liquid Oxygen",
      Abbreviation = "LOX",
      Unit = UnitDefinition.Liter,
      Density = 1.2,
      Domain = ResourceDomain.Topological,
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
  }

  public static Resource ElectricCharge => registry["Electric Charge"];
  public static Resource LiquidHydrogen => registry["Liquid Hydrogen"];
  public static Resource LiquidOxygen => registry["Liquid Oxygen"];
  public static Resource RP1 => registry["RP-1"];
  public static Resource Hydrazine => registry["Hydrazine"];
  public static Resource Xenon => registry["Xenon"];

  private Resource() {}


  public static Resource Get(string name) {
    return registry[name];
  }
}