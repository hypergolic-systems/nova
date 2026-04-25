using System.Collections.Generic;
using System.Linq;
using Nova.Core.Components;
using Nova.Core.Components.Crew;
using Nova.Core.Components.Control;
using Nova.Core.Components.Electrical;
using Nova.Core.Components.Propulsion;
using Nova.Core.Components.Structural;
using Nova.Core.Resources;
using Nova.Core.Flight;
using Nova.Core.Utils;
namespace Nova.Components;

/// <summary>
/// Factory for creating Core VirtualComponents from KSP ConfigNodes.
/// Owns the KSP module name → Core type mapping.
/// </summary>
public static class ComponentFactory {

  private static readonly Dictionary<string, string> moduleNameToType = new() {
    ["NovaTankModule"] = "TankVolume",
    ["NovaEngineModule"] = "Engine",
    ["NovaDecouplerModule"] = "Decoupler",
    ["NovaBatteryModule"] = "Battery",
    ["NovaLightModule"] = "Light",
    ["NovaRcsModule"] = "Rcs",
    ["NovaReactionWheelModule"] = "ReactionWheel",
    ["NovaSolarModule"] = "SolarPanel",
    ["NovaDeployableSolarModule"] = "SolarPanel",
    ["NovaDockingModule"] = "DockingPort",
    ["NovaCrewModule"] = "Crew",
    ["NovaCommandModule"] = "Command",
  };

  public static void RegisterModuleMapping(string moduleName, string typeName) {
    moduleNameToType[moduleName] = typeName;
  }

  public static string ResolveTypeName(string moduleName) {
    if (moduleNameToType.TryGetValue(moduleName, out var typeName))
      return typeName;
    return null;
  }

  public static bool IsHgsModule(string moduleName) {
    return moduleNameToType.ContainsKey(moduleName);
  }

  /// <summary>
  /// Create a VirtualComponent from a KSP MODULE ConfigNode.
  /// </summary>
  public static VirtualComponent Create(ConfigNode moduleNode) {
    var moduleName = moduleNode.GetValue("name");
    var typeName = ResolveTypeName(moduleName);
    return typeName switch {
      "Engine" => CreateEngine(moduleNode),
      "TankVolume" => CreateTankVolume(moduleNode),
      "Battery" => CreateBattery(moduleNode),
      "Rcs" => CreateRcs(moduleNode),
      "ReactionWheel" => CreateReactionWheel(moduleNode),
      "SolarPanel" => CreateSolarPanel(moduleNode),
      "Light" => CreateLight(moduleNode),
      "Decoupler" => CreateDecoupler(moduleNode),
      "DockingPort" => CreateDockingPort(moduleNode),
      "Crew" => CreateCrew(moduleNode),
      "Command" => new Command(),
      _ => throw new System.Exception($"Unknown component type '{typeName}' for module '{moduleName}'"),
    };
  }

  public static Engine CreateEngine(ConfigNode node) {
    var engine = new Engine();
    engine.Initialize(
      thrust: double.Parse(node.GetValue("thrust")),
      isp: double.Parse(node.GetValue("isp")),
      alternatorRate: node.GetValue("alternatorRate") != null
        ? double.Parse(node.GetValue("alternatorRate")) : 0,
      propellants: node.GetNodes("PROPELLANT")
        .Select(n => (Resource.Get(n.GetValue("resource")),
                     double.Parse(n.GetValue("ratio"))))
        .ToList()
    );
    return engine;
  }

  public static TankVolume CreateTankVolume(ConfigNode node) {
    var tank = new TankVolume { Volume = double.Parse(node.GetValue("volume")) };
    foreach (var tankNode in node.GetNodes("TANK")) {
      var capacity = double.Parse(tankNode.GetValue("capacity"));
      tank.Tanks.Add(new Buffer {
        Resource = Resource.Get(tankNode.GetValue("resource")),
        Capacity = capacity,
        Contents = tankNode.GetValue("value") != null
          ? double.Parse(tankNode.GetValue("value")) : capacity,
        MaxRateOut = tankNode.GetValue("maxRateOut") != null
          ? double.Parse(tankNode.GetValue("maxRateOut")) : double.PositiveInfinity,
        MaxRateIn = tankNode.GetValue("maxRateIn") != null
          ? double.Parse(tankNode.GetValue("maxRateIn")) : double.PositiveInfinity,
      });
    }
    return tank;
  }

  public static Battery CreateBattery(ConfigNode node) {
    return new Battery {
      Buffer = new Buffer {
        Resource = Resource.ElectricCharge,
        Capacity = double.Parse(node.GetValue("capacity")),
        Contents = double.Parse(node.GetValue("value")),
        MaxRateIn = 10,
        MaxRateOut = 10,
      },
    };
  }

  public static Rcs CreateRcs(ConfigNode node) {
    var rcs = new Rcs();
    rcs.Initialize(
      thrusterPower: double.Parse(node.GetValue("thrusterPower")),
      isp: double.Parse(node.GetValue("isp")),
      propellants: node.GetNodes("PROPELLANT")
        .Select(n => (Resource.Get(n.GetValue("resource")),
                     double.Parse(n.GetValue("ratio"))))
        .ToList()
    );
    return rcs;
  }

  public static ReactionWheel CreateReactionWheel(ConfigNode node) {
    return new ReactionWheel {
      PitchTorque = double.Parse(node.GetValue("pitchTorque")),
      YawTorque = double.Parse(node.GetValue("yawTorque")),
      RollTorque = double.Parse(node.GetValue("rollTorque")),
      ElectricRate = double.Parse(node.GetValue("electricRate")),
    };
  }

  public static SolarPanel CreateSolarPanel(ConfigNode node) {
    return new SolarPanel {
      ChargeRate = double.Parse(node.GetValue("chargeRate")),
    };
  }

  public static Light CreateLight(ConfigNode node) {
    return new Light {
      Rate = double.Parse(node.GetValue("rate")),
    };
  }

  public static Decoupler CreateDecoupler(ConfigNode node) {
    var decoupler = new Decoupler();
    foreach (var resNode in node.GetNodes("ALLOWED_RESOURCE")) {
      var resource = Resource.Get(resNode.GetValue("resource"));
      decoupler.AllowedResources.Add(resource);
      if (resNode.GetValue("direction") == "up")
        decoupler.UpOnlyResources.Add(resource);
    }
    if (node.GetValue("priority") != null)
      decoupler.Priority = int.Parse(node.GetValue("priority"));
    return decoupler;
  }

  public static DockingPort CreateDockingPort(ConfigNode node) {
    var port = new DockingPort();
    foreach (var resNode in node.GetNodes("ALLOWED_RESOURCE")) {
      var resource = Resource.Get(resNode.GetValue("resource"));
      port.AllowedResources.Add(resource);
      if (resNode.GetValue("direction") == "up")
        port.UpOnlyResources.Add(resource);
    }
    if (node.GetValue("priority") != null)
      port.Priority = int.Parse(node.GetValue("priority"));
    return port;
  }

  public static Crew CreateCrew(ConfigNode node) {
    return new Crew {
      Capacity = int.Parse(node.GetValue("capacity")),
    };
  }
}
