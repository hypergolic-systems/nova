using System.Collections.Generic;
using System.Linq;
using Nova.Core.Components;
using Nova.Core.Components.Communications;
using Nova.Core.Components.Crew;
using Nova.Core.Components.Control;
using Nova.Core.Components.Electrical;
using Nova.Core.Components.Propulsion;
using Nova.Core.Components.Science;
using Nova.Core.Components.Structural;
using Nova.Core.Components.Thermal;
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
    ["NovaNuclearEngineModule"] = "NuclearEngine",
    ["NovaIonEngineModule"] = "IonEngine",
    ["NovaDecouplerModule"] = "Decoupler",
    ["NovaBatteryModule"] = "Battery",
    ["NovaFuelCellModule"] = "FuelCell",
    ["NovaLightModule"] = "Light",
    ["NovaRcsModule"] = "Rcs",
    ["NovaReactionWheelModule"] = "ReactionWheel",
    ["NovaSolarModule"] = "SolarPanel",
    ["NovaDeployableSolarModule"] = "SolarPanel",
    ["NovaDockingModule"] = "DockingPort",
    ["NovaCrewModule"] = "Crew",
    ["NovaCommandModule"] = "Command",
    ["NovaProbeModule"] = "Probe",
    ["NovaThermometerModule"] = "Thermometer",
    ["NovaDataStorageModule"] = "DataStorage",
    ["NovaAntennaModule"] = "Antenna",
    ["NovaRtgModule"] = "Rtg",
    ["NovaRadiatorModule"] = "Radiator",
    ["NovaMysteryGooModule"] = "MysteryGoo",
    ["NovaLandingLegModule"] = "LandingLeg",
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
      "NuclearEngine" => CreateNuclearEngine(moduleNode),
      "IonEngine" => CreateIonEngine(moduleNode),
      "TankVolume" => CreateTankVolume(moduleNode),
      "Battery" => CreateBattery(moduleNode),
      "FuelCell" => CreateFuelCell(moduleNode),
      "Rcs" => CreateRcs(moduleNode),
      "ReactionWheel" => CreateReactionWheel(moduleNode),
      "SolarPanel" => CreateSolarPanel(moduleNode),
      "Light" => CreateLight(moduleNode),
      "Decoupler" => CreateDecoupler(moduleNode),
      "DockingPort" => CreateDockingPort(moduleNode),
      "Crew" => CreateCrew(moduleNode),
      "Command" => CreateCommand(moduleNode),
      "Probe" => CreateProbe(moduleNode),
      "Thermometer" => CreateThermometer(moduleNode),
      "DataStorage" => CreateDataStorage(moduleNode),
      "Antenna" => CreateAntenna(moduleNode),
      "Rtg" => CreateRtg(moduleNode),
      "Radiator" => CreateRadiator(moduleNode),
      "MysteryGoo" => CreateMysteryGoo(moduleNode),
      "LandingLeg" => CreateLandingLeg(moduleNode),
      _ => throw new System.Exception($"Unknown component type '{typeName}' for module '{moduleName}'"),
    };
  }

  public static FuelCell CreateFuelCell(ConfigNode node) {
    var manifoldCap = double.Parse(node.GetValue("manifoldCapacity"));
    // A fresh cell ships with manifold primed — same convention as
    // batteries (capacity=value at build time). Editor save round-
    // trips manifold contents, so subsequent loads honour drain.
    return new FuelCell {
      Lh2Rate    = double.Parse(node.GetValue("lh2Rate")),
      LoxRate    = double.Parse(node.GetValue("loxRate")),
      EcOutput   = double.Parse(node.GetValue("ecOutput")),
      RefillRate = double.Parse(node.GetValue("refillRate")),
      Manifold   = new Accumulator { Capacity = manifoldCap, Contents = manifoldCap },
      IsActive = false,
      RefillActive = false,
    };
  }

  public static Engine CreateEngine(ConfigNode node) {
    var engine = new Engine();
    engine.Initialize(
      thrust: double.Parse(node.GetValue("thrust")),
      isp: double.Parse(node.GetValue("isp")),
      propellants: node.GetNodes("PROPELLANT")
        .Select(n => (Resource.Get(n.GetValue("resource")),
                     double.Parse(n.GetValue("ratio"))))
        .ToList()
    );
    // `engineClass` is required — surfaced on the editor part-info popup
    // as the marquee class line. Catalogue is open (Booster / Sustainer /
    // Vacuum / Ionic / ...); the factory doesn't validate values, only
    // existence. Same policy as thrust/isp — no defaults.
    engine.Class = node.GetValue("engineClass")
      ?? throw new System.ArgumentException(
          "NovaEngineModule: 'engineClass' is required (e.g. Booster, Sustainer, Vacuum, Ionic).");
    // Gimbal config: optional. cfg specifies degrees (matches stock
    // ModuleGimbal); we store radians on the component because every
    // downstream consumer (sin / cos for the side-force calc) needs
    // radians and converting once here keeps that conversion off the
    // per-tick path. Geometry fields stay zero — `NovaEngineModule`
    // populates them at `OnStart` from the part's gimbal transform.
    var gimbalRange = node.GetValue("gimbalRange");
    if (gimbalRange != null)
      engine.GimbalRangeRad = double.Parse(gimbalRange) * System.Math.PI / 180.0;
    return engine;
  }

  // NTR (LV-N) reactor. Every value is config-declared — see
  // configs/overrides/propulsion/liquidEngineLV-N.cfg for the canonical
  // numbers. Single propellant (LH₂) by construction; the cfg writer
  // is trusted to declare exactly that. Per the "factories are thin
  // parsers" memory: no formulas, no defaults, no policy.
  public static NuclearEngine CreateNuclearEngine(ConfigNode node) {
    var propNode = node.GetNodes("PROPELLANT").FirstOrDefault()
      ?? throw new System.ArgumentException(
          "NovaNuclearEngineModule: PROPELLANT { resource = ... } is required.");
    var propResource = Resource.Get(propNode.GetValue("resource"));

    var reactor = new NuclearEngine();
    reactor.InitializeNuclear(
      thrust:            double.Parse(node.GetValue("thrust")),
      isp:               double.Parse(node.GetValue("isp")),
      propellant:        propResource,
      thermalMassJK:     double.Parse(node.GetValue("thermalMassJK")),
      ambientK:          double.Parse(node.GetValue("ambientK")),
      coldThresholdK:    double.Parse(node.GetValue("coldThresholdK")),
      warmupDurationSec: double.Parse(node.GetValue("warmupDurationSec")),
      idleTempK:         double.Parse(node.GetValue("idleTempK")),
      operatingTempK:    double.Parse(node.GetValue("operatingTempK")),
      spoolEndThrottle:  double.Parse(node.GetValue("spoolEndThrottle")),
      idlePowerW:        double.Parse(node.GetValue("idlePowerW")),
      maxPowerW:         double.Parse(node.GetValue("maxPowerW")),
      coolingCoeffWK:    double.Parse(node.GetValue("coolingCoeffWK")),
      inletTempK:        double.Parse(node.GetValue("inletTempK")),
      cpH2JKgK:          double.Parse(node.GetValue("cpH2JKgK")),
      slewRatePerSec:    double.Parse(node.GetValue("slewRatePerSec")),
      decayTauSeconds:   double.Parse(node.GetValue("decayTauSeconds"))
    );
    var gimbalRange = node.GetValue("gimbalRange");
    if (gimbalRange != null)
      reactor.GimbalRangeRad = double.Parse(gimbalRange) * System.Math.PI / 180.0;
    return reactor;
  }

  // NSTAR-class ion thruster. Single propellant (Xenon); EC + Heat
  // plumbing is set up by IonEngine.OnBuildSystems against the config-
  // declared rated power and thermal envelope. Per the "factories are
  // thin parsers" memory: no formulas, no defaults — every value is
  // declared in configs/overrides/propulsion/ionEngine.cfg.
  public static IonEngine CreateIonEngine(ConfigNode node) {
    var propNode = node.GetNodes("PROPELLANT").FirstOrDefault()
      ?? throw new System.ArgumentException(
          "NovaIonEngineModule: PROPELLANT { resource = ... } is required.");
    var propResource = Resource.Get(propNode.GetValue("resource"));

    var ion = new IonEngine();
    ion.InitializeIon(
      thrust:                   double.Parse(node.GetValue("thrust")),
      isp:                      double.Parse(node.GetValue("isp")),
      propellant:               propResource,
      ratedPowerW:              double.Parse(node.GetValue("ratedPowerW")),
      jetEfficiency:            double.Parse(node.GetValue("jetEfficiency")),
      thermalMassJK:            double.Parse(node.GetValue("thermalMassJK")),
      ambientK:                 double.Parse(node.GetValue("ambientK")),
      maxOperatingTempK:        double.Parse(node.GetValue("maxOperatingTempK")),
      maxHeatRejectionW:        double.Parse(node.GetValue("maxHeatRejectionW")),
      tripXeShortfallThreshold: double.Parse(node.GetValue("tripXeShortfallThreshold"))
    );
    ion.Class = node.GetValue("engineClass")
      ?? throw new System.ArgumentException(
          "NovaIonEngineModule: 'engineClass' is required (typically 'Ionic').");
    return ion;
  }

  public static TankVolume CreateTankVolume(ConfigNode node) {
    var maxRateValue = node.GetValue("maxRate")
      ?? throw new System.ArgumentException("NovaTankModule: 'maxRate' is required (L/s, shared in/out at the part level).");
    var tank = new TankVolume {
      Volume = double.Parse(node.GetValue("volume")),
      MaxRate = double.Parse(maxRateValue),
    };
    double footprintSum = 0;
    foreach (var tankNode in node.GetNodes("TANK")) {
      var capacity = double.Parse(tankNode.GetValue("capacity"));
      var insulationValue = tankNode.GetValue("insulation");
      var tier = InsulationTier.MLI;
      if (insulationValue != null
          && !System.Enum.TryParse(insulationValue, ignoreCase: true, out tier))
        throw new System.ArgumentException(
            $"NovaTankModule: unknown insulation tier '{insulationValue}' (expected one of MLI, HeavyMLI, BAC, ZBO).");
      tank.Tanks.Add(new Buffer {
        Resource = Resource.Get(tankNode.GetValue("resource")),
        Capacity = capacity,
        Contents = tankNode.GetValue("value") != null
          ? double.Parse(tankNode.GetValue("value")) : capacity,
      });
      tank.Tiers.Add(tier);
      footprintSum += capacity * (1.0 + InsulationTierTable.VolumePenalty(tier));
    }
    if (footprintSum > tank.Volume + 1e-6)
      throw new System.ArgumentException(
          $"NovaTankModule: TANK capacities + tier volume penalties ({footprintSum:F2} L) " +
          $"exceed declared Volume ({tank.Volume:F2} L). Reduce capacities or pick a lower tier.");
    return tank;
  }

  public static Battery CreateBattery(ConfigNode node) {
    var maxRate = double.Parse(node.GetValue("maxRate"));
    return new Battery {
      Buffer = new Buffer {
        Resource = Resource.ElectricCharge,
        Capacity = double.Parse(node.GetValue("capacity")),
        Contents = double.Parse(node.GetValue("value")),
        MaxRateIn = maxRate,
        MaxRateOut = maxRate,
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
    var wheel = new ReactionWheel {
      PitchTorque = double.Parse(node.GetValue("pitchTorque")),
      YawTorque = double.Parse(node.GetValue("yawTorque")),
      RollTorque = double.Parse(node.GetValue("rollTorque")),
      ElectricRate = double.Parse(node.GetValue("electricRate")),
    };
    // Prime the buffer to full so editor saves round-trip with a
    // charged wheel — without this, the editor's `Save` writes
    // `Contents = 0` (Buffer is still null at that point) and the
    // flight `Load` faithfully restores an empty buffer, starving
    // the wheel on launch. Capacity comes from the component's own
    // derived property; the factory stays formula-free.
    wheel.Buffer = new Accumulator {
      Capacity = wheel.BufferCapacityJoules,
      Contents = wheel.BufferCapacityJoules,
    };
    return wheel;
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

  public static Command CreateCommand(ConfigNode node) {
    return new Command {
      IdleDraw = double.Parse(node.GetValue("idleDraw")),
      TestLoadRate = double.Parse(node.GetValue("testLoadRate")),
    };
  }

  public static Probe CreateProbe(ConfigNode node) {
    var capacity = double.Parse(node.GetValue("commandCapacityBytes"));
    return new Probe {
      IdleDraw = double.Parse(node.GetValue("idleDraw")),
      TestLoadRate = double.Parse(node.GetValue("testLoadRate")),
      SasLevel = int.Parse(node.GetValue("sasLevel")),
      CommandCapacityBytes  = capacity,
      CommandDecayBps       = double.Parse(node.GetValue("commandDecayBps")),
      CommandReceiveRateBps = double.Parse(node.GetValue("commandReceiveRateBps")),
      InputCostBps          = double.Parse(node.GetValue("inputCostBps")),
      // Prime the ledger to full so editor reads land on capacity (no
      // VirtualVessel / sim clock there). Flight `OnBuildSystems` re-
      // anchors `CommandBaselineUT` to the live clock; `Load` overrides
      // both fields when a save is restored.
      CommandBaselineBytes = capacity,
    };
  }

  public static Thermometer CreateThermometer(ConfigNode node) {
    return new Thermometer {
      EcRate = double.Parse(node.GetValue("ecRate")),
    };
  }

  public static DataStorage CreateDataStorage(ConfigNode node) {
    return new DataStorage {
      CapacityBytes = long.Parse(node.GetValue("capacityBytes")),
    };
  }

  public static Antenna CreateAntenna(ConfigNode node) {
    return new Antenna {
      TxPower = double.Parse(node.GetValue("txPower")),
      Gain = double.Parse(node.GetValue("gain")),
      MaxRate = double.Parse(node.GetValue("maxRate")),
      RefDistance = double.Parse(node.GetValue("refDistance")),
    };
  }

  public static Rtg CreateRtg(ConfigNode node) {
    return new Rtg {
      ReferencePower      = double.Parse(node.GetValue("referencePower")),
      HalfLifeDays        = double.Parse(node.GetValue("halfLifeDays")),
      ThermalOutput       = double.Parse(node.GetValue("thermalOutput")),
      MaxOperatingTempC   = double.Parse(node.GetValue("maxOperatingTempC")),
      ThermalMassJK       = double.Parse(node.GetValue("thermalMassJK")),
      MaxHeatRateOut      = double.Parse(node.GetValue("maxHeatRateOut")),
      VacuumRejectionW    = double.Parse(node.GetValue("vacuumRejectionW")),
      AtmRejectionW       = double.Parse(node.GetValue("atmRejectionW")),
      ReferenceUT         = 0,
    };
  }

  public static Radiator CreateRadiator(ConfigNode node) {
    return new Radiator {
      VacuumCoolingW   = double.Parse(node.GetValue("vacuumCoolingW")),
      AtmCoolingW      = double.Parse(node.GetValue("atmCoolingW")),
      EcPerWattCooling = double.Parse(node.GetValue("ecPerWattCooling")),
      // IsDeployable / IsDeployed are set by NovaRadiatorModule.OnStart
      // — derived from whether `animationName` is configured (the
      // single source of truth for deploy mechanics).
    };
  }

  public static MysteryGoo CreateMysteryGoo(ConfigNode node) {
    return new MysteryGoo {
      Capacity              = int.Parse(node.GetValue("capacity")),
      AllowedSampleTypeIds  = node.GetValues("allowedSampleType").ToList(),
      InitialSampleTypeIds  = node.GetValues("initialSample").ToList(),
    };
  }

  public static LandingLeg CreateLandingLeg(ConfigNode node) {
    // Editor-tunable bools (RequiresStaging / StartsDeployed) are NOT
    // read here — NovaLandingLegModule applies the KSPField defaults
    // in OnStart only when LoadedFromSave is false, so a persisted
    // editor flip survives the round-trip. Activated / Position /
    // TargetPosition are runtime state set OnStart or Load.
    return new LandingLeg {
      MotorPowerW   = double.Parse(node.GetValue("motorPowerW")),
      DeploySeconds = double.Parse(node.GetValue("deploySeconds")),
    };
  }
}
