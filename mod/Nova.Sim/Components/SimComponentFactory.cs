using System;
using System.Linq;
using Nova.Core.Components;
using Nova.Core.Components.Communications;
using Nova.Core.Components.Control;
using Nova.Core.Components.Crew;
using Nova.Core.Components.Electrical;
using Nova.Core.Components.Propulsion;
using Nova.Core.Components.Science;
using Nova.Core.Components.Structural;
using Nova.Core.Components.Thermal;
using Nova.Core.Resources;
using Nova.Core.Utils;
using Buffer = Nova.Core.Resources.Buffer;

namespace Nova.Sim.Components;

// Sim-side mirror of mod/Nova/Components/ComponentFactory.cs.
//
// The mod-side factory takes KSP's stock ConfigNode (from
// Assembly-CSharp); the sim runs outside KSP and uses
// Nova.Core.Utils.ConfigNode, which exposes the same
// GetValue/GetNodes API. The dispatch table and per-kind creation
// logic mirror the mod side exactly.
//
// If this duplication starts drifting, the right fix is to move
// the factory wholesale into Nova.Core (it has zero genuine KSP
// dependencies once ConfigNode is Core-typed) and have the mod
// side adapt KSP ConfigNode → Core ConfigNode at the boundary.
public static class SimComponentFactory {
  public static VirtualComponent Create(ConfigNode moduleNode) {
    var moduleName = moduleNode.GetValue("name");
    switch (moduleName) {
      case "NovaTankModule":            return CreateTankVolume(moduleNode);
      case "NovaEngineModule":          return CreateEngine(moduleNode);
      case "NovaNuclearEngineModule":   return CreateNuclearEngine(moduleNode);
      case "NovaDecouplerModule":       return CreateDecoupler(moduleNode);
      case "NovaBatteryModule":         return CreateBattery(moduleNode);
      case "NovaFuelCellModule":        return CreateFuelCell(moduleNode);
      case "NovaLightModule":           return CreateLight(moduleNode);
      case "NovaRcsModule":             return CreateRcs(moduleNode);
      case "NovaReactionWheelModule":   return CreateReactionWheel(moduleNode);
      case "NovaSolarModule":           return CreateSolarPanel(moduleNode, deployable: false);
      case "NovaDeployableSolarModule": return CreateSolarPanel(moduleNode, deployable: true);
      case "NovaDockingModule":         return CreateDockingPort(moduleNode);
      case "NovaCrewModule":            return CreateCrew(moduleNode);
      case "NovaCommandModule":         return CreateCommand(moduleNode);
      case "NovaProbeModule":           return CreateProbe(moduleNode);
      case "NovaThermometerModule":     return CreateThermometer(moduleNode);
      case "NovaDataStorageModule":     return CreateDataStorage(moduleNode);
      case "NovaAntennaModule":         return CreateAntenna(moduleNode);
      case "NovaRtgModule":             return CreateRtg(moduleNode);
      case "NovaRadiatorModule":        return CreateRadiator(moduleNode);
      default: return null; // unknown / non-Nova module — skipped
    }
  }

  // True iff the module name is one the sim factory recognises. Lets
  // the loader skip stock modules (`ModuleEngines`, `ModuleScienceContainer`,
  // …) without first attempting Create.
  public static bool IsNovaModule(string moduleName) {
    return moduleName != null && moduleName.StartsWith("Nova");
  }

  // ---- Per-component creators (mirror mod ComponentFactory) -------

  private static double D(ConfigNode n, string key) =>
    double.Parse(n.GetValue(key), System.Globalization.CultureInfo.InvariantCulture);

  private static double DOr(ConfigNode n, string key, double fallback) {
    var v = n.GetValue(key);
    return v == null ? fallback :
      double.Parse(v, System.Globalization.CultureInfo.InvariantCulture);
  }

  private static int I(ConfigNode n, string key) =>
    int.Parse(n.GetValue(key), System.Globalization.CultureInfo.InvariantCulture);

  private static long L(ConfigNode n, string key) =>
    long.Parse(n.GetValue(key), System.Globalization.CultureInfo.InvariantCulture);

  public static TankVolume CreateTankVolume(ConfigNode node) {
    var maxRateValue = node.GetValue("maxRate")
      ?? throw new ArgumentException("NovaTankModule: 'maxRate' is required");
    var tank = new TankVolume {
      Volume = D(node, "volume"),
      MaxRate = double.Parse(maxRateValue, System.Globalization.CultureInfo.InvariantCulture),
    };
    foreach (var tankNode in node.GetNodes("TANK")) {
      var capacity = D(tankNode, "capacity");
      tank.Tanks.Add(new Buffer {
        Resource = Resource.Get(tankNode.GetValue("resource")),
        Capacity = capacity,
        Contents = tankNode.GetValue("value") != null
          ? D(tankNode, "value") : capacity,
      });
    }
    return tank;
  }

  public static Engine CreateEngine(ConfigNode node) {
    var engine = new Engine();
    engine.Initialize(
      thrust: D(node, "thrust"),
      isp: D(node, "isp"),
      propellants: node.GetNodes("PROPELLANT")
        .Select(n => (Resource.Get(n.GetValue("resource")), D(n, "ratio")))
        .ToList()
    );
    var gimbalRange = node.GetValue("gimbalRange");
    if (gimbalRange != null) {
      engine.GimbalRangeRad =
        double.Parse(gimbalRange, System.Globalization.CultureInfo.InvariantCulture)
        * Math.PI / 180.0;
    }
    return engine;
  }

  public static NuclearEngine CreateNuclearEngine(ConfigNode node) {
    var propNode = node.GetNodes("PROPELLANT").FirstOrDefault()
      ?? throw new System.ArgumentException(
          "NovaNuclearEngineModule: PROPELLANT { resource = ... } is required.");
    var propResource = Resource.Get(propNode.GetValue("resource"));

    var reactor = new NuclearEngine();
    reactor.InitializeNuclear(
      thrust:            D(node, "thrust"),
      isp:               D(node, "isp"),
      propellant:        propResource,
      thermalMassJK:     D(node, "thermalMassJK"),
      ambientK:          D(node, "ambientK"),
      coldThresholdK:    D(node, "coldThresholdK"),
      warmupDurationSec: D(node, "warmupDurationSec"),
      idleTempK:         D(node, "idleTempK"),
      operatingTempK:    D(node, "operatingTempK"),
      spoolEndThrottle:  D(node, "spoolEndThrottle"),
      idlePowerW:        D(node, "idlePowerW"),
      maxPowerW:         D(node, "maxPowerW"),
      coolingCoeffWK:    D(node, "coolingCoeffWK"),
      inletTempK:        D(node, "inletTempK"),
      cpH2JKgK:          D(node, "cpH2JKgK"),
      slewRatePerSec:    D(node, "slewRatePerSec"),
      decayTauSeconds:   D(node, "decayTauSeconds")
    );
    var gimbalRange = node.GetValue("gimbalRange");
    if (gimbalRange != null) {
      reactor.GimbalRangeRad =
        double.Parse(gimbalRange, System.Globalization.CultureInfo.InvariantCulture)
        * Math.PI / 180.0;
    }
    return reactor;
  }

  public static Battery CreateBattery(ConfigNode node) {
    var maxRate = D(node, "maxRate");
    return new Battery {
      Buffer = new Buffer {
        Resource = Resource.ElectricCharge,
        Capacity = D(node, "capacity"),
        Contents = D(node, "value"),
        MaxRateIn = maxRate,
        MaxRateOut = maxRate,
      },
    };
  }

  public static FuelCell CreateFuelCell(ConfigNode node) {
    var manifoldCap = D(node, "manifoldCapacity");
    return new FuelCell {
      Lh2Rate    = D(node, "lh2Rate"),
      LoxRate    = D(node, "loxRate"),
      EcOutput   = D(node, "ecOutput"),
      RefillRate = D(node, "refillRate"),
      Manifold   = new Accumulator { Capacity = manifoldCap, Contents = manifoldCap },
      IsActive   = false,
      RefillActive = false,
    };
  }

  public static Rcs CreateRcs(ConfigNode node) {
    var rcs = new Rcs();
    rcs.Initialize(
      thrusterPower: D(node, "thrusterPower"),
      isp: D(node, "isp"),
      propellants: node.GetNodes("PROPELLANT")
        .Select(n => (Resource.Get(n.GetValue("resource")), D(n, "ratio")))
        .ToList()
    );
    return rcs;
  }

  public static ReactionWheel CreateReactionWheel(ConfigNode node) {
    var wheel = new ReactionWheel {
      PitchTorque  = D(node, "pitchTorque"),
      YawTorque    = D(node, "yawTorque"),
      RollTorque   = D(node, "rollTorque"),
      ElectricRate = D(node, "electricRate"),
    };
    wheel.Buffer = new Accumulator {
      Capacity = wheel.BufferCapacityJoules,
      Contents = wheel.BufferCapacityJoules,
    };
    return wheel;
  }

  public static SolarPanel CreateSolarPanel(ConfigNode node, bool deployable) {
    // NovaDeployableSolarModule's `retractable` defaults to true; fixed
    // (NovaSolarModule) panels can't be retracted in the first place,
    // so the bit only matters for deployables. Mirrors the OnStart
    // assignment in NovaDeployableSolarModule.
    return new SolarPanel {
      ChargeRate     = D(node, "chargeRate"),
      IsRetractable  = deployable && BOr(node, "retractable", true),
    };
  }

  public static Light CreateLight(ConfigNode node) {
    return new Light { Rate = D(node, "rate") };
  }

  public static Decoupler CreateDecoupler(ConfigNode node) {
    var d = new Decoupler();
    foreach (var resNode in node.GetNodes("ALLOWED_RESOURCE")) {
      var resource = Resource.Get(resNode.GetValue("resource"));
      d.AllowedResources.Add(resource);
      if (resNode.GetValue("direction") == "up")
        d.UpOnlyResources.Add(resource);
    }
    if (node.GetValue("priority") != null) d.Priority = I(node, "priority");
    d.EjectionForce = DOr(node, "ejectionForce", 10.0);
    // Sim doesn't read KSP's `explosiveNodeID`; default to true so the
    // UI toggle is enabled. A future iteration can detect radial-only
    // configs (single surface-attach node) and clear this.
    d.CanFullSeparate = true;
    return d;
  }

  public static DockingPort CreateDockingPort(ConfigNode node) {
    var port = new DockingPort();
    foreach (var resNode in node.GetNodes("ALLOWED_RESOURCE")) {
      var resource = Resource.Get(resNode.GetValue("resource"));
      port.AllowedResources.Add(resource);
      if (resNode.GetValue("direction") == "up")
        port.UpOnlyResources.Add(resource);
    }
    if (node.GetValue("priority") != null) port.Priority = I(node, "priority");
    return port;
  }

  public static Crew CreateCrew(ConfigNode node) {
    return new Crew { Capacity = I(node, "capacity") };
  }

  public static Command CreateCommand(ConfigNode node) {
    return new Command {
      IdleDraw     = D(node, "idleDraw"),
      TestLoadRate = D(node, "testLoadRate"),
    };
  }

  public static Probe CreateProbe(ConfigNode node) {
    var capacity = D(node, "commandCapacityBytes");
    return new Probe {
      IdleDraw              = D(node, "idleDraw"),
      TestLoadRate          = D(node, "testLoadRate"),
      SasLevel              = I(node, "sasLevel"),
      CommandCapacityBytes  = capacity,
      CommandDecayBps       = D(node, "commandDecayBps"),
      CommandReceiveRateBps = D(node, "commandReceiveRateBps"),
      InputCostBps          = D(node, "inputCostBps"),
      CommandBaselineBytes  = capacity,
    };
  }

  public static Thermometer CreateThermometer(ConfigNode node) {
    return new Thermometer { EcRate = D(node, "ecRate") };
  }

  public static DataStorage CreateDataStorage(ConfigNode node) {
    return new DataStorage { CapacityBytes = L(node, "capacityBytes") };
  }

  public static Antenna CreateAntenna(ConfigNode node) {
    return new Antenna {
      TxPower     = D(node, "txPower"),
      Gain        = D(node, "gain"),
      MaxRate     = D(node, "maxRate"),
      RefDistance = D(node, "refDistance"),
    };
  }

  public static Rtg CreateRtg(ConfigNode node) {
    return new Rtg {
      ReferencePower    = D(node, "referencePower"),
      HalfLifeDays      = D(node, "halfLifeDays"),
      ThermalOutput     = D(node, "thermalOutput"),
      MaxOperatingTempC = D(node, "maxOperatingTempC"),
      ThermalMassJK     = D(node, "thermalMassJK"),
      MaxHeatRateOut    = D(node, "maxHeatRateOut"),
      VacuumRejectionW  = D(node, "vacuumRejectionW"),
      AtmRejectionW     = D(node, "atmRejectionW"),
      ReferenceUT       = 0,
    };
  }

  public static Radiator CreateRadiator(ConfigNode node) {
    // Mirrors NovaRadiatorModule's cfg semantics: animationName drives
    // deployability (folding rads vs fixed panels), `retractable`
    // (default true) gates whether the deploy is one-shot. Sim has no
    // OnStart path; we materialise both flags here so Sim consumers
    // see the same capability bits as the in-game adapter would set.
    var animationName = node.GetValue("animationName") ?? "";
    var deployable = !string.IsNullOrEmpty(animationName);
    return new Radiator {
      VacuumCoolingW   = D(node, "vacuumCoolingW"),
      AtmCoolingW      = D(node, "atmCoolingW"),
      EcPerWattCooling = D(node, "ecPerWattCooling"),
      IsDeployable     = deployable,
      IsRetractable    = deployable && BOr(node, "retractable", true),
    };
  }

  private static bool BOr(ConfigNode n, string key, bool fallback) {
    var v = n.GetValue(key);
    return v == null ? fallback : bool.Parse(v);
  }
}
