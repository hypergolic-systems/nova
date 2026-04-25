using System;
using System.Collections.Generic;

namespace Nova.Core.Resources;

public class Resource {
  public string Name;
  public string Abbreviation;
  public UnitDefinition Unit;
  public double Density; // kg/U

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
    registry["Electric Charge"] = new Resource {
      Name = "Electric Charge",
      Abbreviation = "EC",
      Unit = UnitDefinition.Watt,
      Density = 0,
    };
    
    registry["Liquid Hydrogen"] = new Resource {
      Name = "Liquid Hydrogen",
      Abbreviation = "LH2",
      Unit = UnitDefinition.Liter,
      Density = 0.07,
    };

    registry["Liquid Oxygen"] = new Resource {
      Name = "Liquid Oxygen",
      Abbreviation = "LOX",
      Unit = UnitDefinition.Liter,
      Density = 1.1,
    };

    registry["RP-1"] = new Resource {
      Name = "RP-1",
      Abbreviation = "RP-1",
      Unit = UnitDefinition.Liter,
      Density = 0.9,
    };

    registry["Hydrazine"] = new Resource {
      Name = "Hydrazine",
      Abbreviation = "N2H4",
      Unit = UnitDefinition.Liter,
      Density = 1.0,
    };

    registry["Xenon"] = new Resource {
      Name = "Xenon",
      Abbreviation = "Xe",
      Unit = UnitDefinition.Liter,
      // KSP uses a density of around ~1 kg/L for Xenon. However, Dawn used supercritical Xenon at a
      // density of ~2 kg/L, so we use the denser value.
      Density = 2,
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