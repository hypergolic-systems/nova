using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nova.Core.Components;
using Nova.Core.Components.Electrical;
using Nova.Core.Components.Propulsion;
using Nova.Core.Components.Thermal;
using Nova.Core.Persistence.Protos;
using Nova.Core.Resources;
using Buffer = Nova.Core.Resources.Buffer;

namespace Nova.Tests.Components;

[TestClass]
public class IonEngineTests {

  // NSTAR-realistic reference numbers — kept aligned with
  // configs/overrides/propulsion/ionEngine.cfg so calibration tests
  // exercise the shipping balance.
  private const double ThrustKn               = 0.09;
  private const double IspS                   = 3100;
  private const double RatedPowerW            = 2300;
  private const double JetEfficiency          = 0.66;
  private const double ThermalMassJK          = 5000;
  private const double AmbientK               = 290;
  private const double MaxOperatingTempK      = 1500;
  private const double MaxHeatRejectionW      = 1500;
  private const double TripXeShortfallThreshold = 0.05;

  private static IonEngine MakeIon() {
    var ion = new IonEngine();
    ion.InitializeIon(
      thrust: ThrustKn,
      isp: IspS,
      propellant: Resource.Xenon,
      ratedPowerW: RatedPowerW,
      jetEfficiency: JetEfficiency,
      thermalMassJK: ThermalMassJK,
      ambientK: AmbientK,
      maxOperatingTempK: MaxOperatingTempK,
      maxHeatRejectionW: MaxHeatRejectionW,
      tripXeShortfallThreshold: TripXeShortfallThreshold);
    ion.Class = "Ionic";
    return ion;
  }

  // Xenon tank — 100 L is comfortably more than any single test burns.
  private static TankVolume MakeXenonTank(double liters = 100) {
    return new TankVolume {
      Volume = liters,
      MaxRate = 1,
      Tanks = {
        new Buffer {
          Resource = Resource.Xenon,
          Capacity = liters,
          Contents = liters,
        },
      },
    };
  }

  private static TankVolume MakeEmptyXenonTank(double liters = 100) {
    return new TankVolume {
      Volume = liters,
      MaxRate = 1,
      Tanks = {
        new Buffer {
          Resource = Resource.Xenon,
          Capacity = liters,
          Contents = 0,
        },
      },
    };
  }

  private static Battery MakeBattery(double capacity = 1e6, double maxRate = 1e5) {
    return new Battery {
      Buffer = new Buffer {
        Resource = Resource.ElectricCharge,
        Capacity = capacity,
        Contents = capacity,
        MaxRateIn = maxRate,
        MaxRateOut = maxRate,
      },
    };
  }

  private static Radiator MakeRadiator(double wattsPerSide = 2000) {
    return new Radiator {
      VacuumCoolingW = wattsPerSide,
      AtmCoolingW = wattsPerSide,
      EcPerWattCooling = 0,
      IsDeployed = true,
    };
  }

  // Tank + battery + (optional) radiator + ion engine. Parent chain:
  // tank as root, engine + battery (+ radiator) attached. Both EC and
  // Xe flow up the tree to the engine.
  private static VirtualVessel BuildVessel(IonEngine ion, TankVolume tank,
                                            Battery battery, Radiator radiator = null) {
    var vv = new VirtualVessel();
    vv.AddPart(1u, "engine",  1.0, new List<VirtualComponent> { ion });
    vv.AddPart(2u, "tank",    1.0, new List<VirtualComponent> { tank });
    vv.AddPart(3u, "battery", 1.0, new List<VirtualComponent> { battery });
    var parents = new Dictionary<uint, uint?> {
      { 2u, null },
      { 1u, 2u },
      { 3u, 2u },
    };
    if (radiator != null) {
      vv.AddPart(4u, "radiator", 1.0, new List<VirtualComponent> { radiator });
      parents[4u] = 2u;
    }
    vv.UpdatePartTree(parents);
    vv.InitializeSolver(0);
    return vv;
  }

  // ─── Round-trip / persistence ───────────────────────────────────────

  [TestMethod]
  public void State_RoundTripsAllFields() {
    var src = MakeIon();
    src.Active = true;
    src.Tripped = true;
    src.TripReason = IonTripReason.XeStarvation;
    // CoreTempK rounds through HeatBuffer.Contents at save time.
    src.HeatBuffer = new Buffer {
      Resource = Resource.Heat,
      Capacity = double.PositiveInfinity,
      BaselineContents = ThermalMassJK * (1100 - AmbientK),
    };

    var srcState = new PartState();
    src.Save(srcState);

    Assert.IsTrue(srcState.IonEngine.Active);
    Assert.IsTrue(srcState.IonEngine.Tripped);
    Assert.AreEqual((int)IonTripReason.XeStarvation, srcState.IonEngine.TripReason);
    Assert.AreEqual(1100, srcState.IonEngine.CoreTempK, 1e-6);

    var dst = MakeIon();
    dst.Load(srcState);
    Assert.IsTrue(dst.Active);
    Assert.IsTrue(dst.Tripped);
    Assert.AreEqual(IonTripReason.XeStarvation, dst.TripReason);
  }

  // Throttle is a per-tick input from NovaIonEngineModule. Load resets
  // it so any solve between Load and the next FixedUpdate sees a
  // stopped engine.
  [TestMethod]
  public void Load_ResetsThrottleToZero() {
    var src = MakeIon();
    src.Active = true;
    src.Throttle = 0.8;
    var state = new PartState();
    src.Save(state);

    var dst = MakeIon();
    dst.Throttle = 0.5;
    dst.Load(state);
    Assert.AreEqual(0.0, dst.Throttle, 1e-12);
  }

  // ─── Tripped behaviour ──────────────────────────────────────────────

  [TestMethod]
  public void Tripped_ReportsShutdownStatus() {
    var ion = MakeIon();
    ion.Active = true;
    ion.Tripped = true;
    Assert.AreEqual((byte)3, ion.EngineStatus);
  }

  [TestMethod]
  public void Tripped_ZeroesThrustOutputFraction() {
    var ion = MakeIon();
    ion.Active = true;
    ion.Throttle = 1.0;
    ion.Tripped = true;
    Assert.AreEqual(0.0, ion.ThrustOutputFraction, 1e-12);
  }

  // A tripped engine sits in the throttle chain until the player
  // resets the latch. OnPreSolve must clear Throttle before the base
  // sets xeDevice.Demand, so the staging solver sees 0 demand.
  [TestMethod]
  public void Tripped_ZeroesXeAndEcDemand() {
    var ion = MakeIon();
    var vessel = BuildVessel(ion, MakeXenonTank(), MakeBattery(), MakeRadiator());
    ion.Active = true;
    ion.Throttle = 1.0;
    ion.Tripped = true;
    ion.TripReason = IonTripReason.XeStarvation;

    vessel.Tick(0.1);

    Assert.AreEqual(0.0, ion.NormalizedOutput, 1e-9);
    Assert.AreEqual(0.0, ion.CurrentEcW, 1e-6);
    Assert.AreEqual(0.0, ion.ThrustOutputFraction, 1e-12);
  }

  // ─── Trip detection ─────────────────────────────────────────────────

  // Empty Xenon tank + full battery + Throttle > 0 → after one solve,
  // staging delivers ~0 xenon despite EC being healthy. Threshold
  // (xeSat < ecSat - 0.05) fires → trip on XeStarvation.
  [TestMethod]
  public void XeStarvation_TripsWhenXeEmpty() {
    var ion = MakeIon();
    var vessel = BuildVessel(ion, MakeEmptyXenonTank(), MakeBattery(), MakeRadiator());
    ion.Active = true;
    ion.Throttle = 1.0;

    vessel.Tick(0.1);

    Assert.IsTrue(ion.Tripped, "Engine should trip when Xe tank is empty");
    Assert.AreEqual(IonTripReason.XeStarvation, ion.TripReason);
    Assert.IsFalse(ion.Active, "Trip should clear Active");
  }

  // No radiator on the vessel → heat outlet has no consumer →
  // heatOutletDevice.Activity = 0 → all waste heat accumulates in the
  // private buffer → CoreTempK climbs past MaxOperatingTempK → trip on
  // Overtemp.
  [TestMethod]
  public void Overtemp_TripsWhenNoRadiator() {
    var ion = MakeIon();
    // Oversized battery (100 MJ, 1e5 W rate) — guarantees EC isn't the
    // bottleneck during the ~7700 s buffer fill. Want a clean overtemp
    // failure, not an EC-depletion edge case.
    var vessel = BuildVessel(ion, MakeXenonTank(),
                              MakeBattery(capacity: 1e8, maxRate: 1e5),
                              radiator: null);
    ion.Active = true;
    ion.Throttle = 1.0;

    // Advance enough that ~780 W of waste heat overheats the 5000 J/K
    // buffer past the 1210 K span (MaxOperatingTempK - AmbientK).
    // dT/dt = 780/5000 = 0.156 K/s → ~7770 s to span 1210 K.
    for (int i = 0; i < 200; i++) {
      vessel.Tick((i + 1) * 50.0);
      if (ion.Tripped) break;
    }

    Assert.IsTrue(ion.Tripped, "Engine should trip from overtemp");
    Assert.AreEqual(IonTripReason.Overtemp, ion.TripReason);
  }

  // With a healthy radiator sized above the waste-heat output,
  // temperature stabilises and the engine runs indefinitely.
  [TestMethod]
  public void RadiatorPresent_KeepsTempSteady() {
    var ion = MakeIon();
    // 2 kW radiator comfortably exceeds 780 W waste.
    var vessel = BuildVessel(ion, MakeXenonTank(), MakeBattery(), MakeRadiator(2000));
    ion.Active = true;
    ion.Throttle = 1.0;

    for (int i = 0; i < 100; i++) vessel.Tick((i + 1) * 10.0);

    Assert.IsFalse(ion.Tripped, "Engine should not trip with sufficient cooling");
    Assert.IsTrue(ion.CoreTempK < MaxOperatingTempK,
        $"CoreTempK={ion.CoreTempK} should stay below MaxOperatingTempK={MaxOperatingTempK}");
  }

  // ─── Coupling ───────────────────────────────────────────────────────

  // ThrustOutputFraction = min(xe.Activity, ec.Activity). When EC is
  // bottlenecked (battery MaxRateOut below RatedPowerW) but Xe is fully
  // supplied, thrust tracks the EC fraction. Threshold direction makes
  // this safe — xeSat > ecSat so no Xe-starvation trip fires.
  [TestMethod]
  public void Couple_EcBottleneck_GivesEcLimitedThrust() {
    var ion = MakeIon();
    // Battery MaxRateOut = 1000 W < RatedPowerW = 2300 W. With full
    // throttle the LP will clip ecActivity at ~1000/2300 ≈ 0.435.
    var vessel = BuildVessel(ion, MakeXenonTank(),
                              MakeBattery(capacity: 1e6, maxRate: 1000),
                              MakeRadiator());
    ion.Active = true;
    ion.Throttle = 1.0;

    vessel.Tick(0.1);

    Assert.IsFalse(ion.Tripped,
        "EC bottleneck should not trip (Xe satisfied, no Xe-starvation)");
    double expected = 1000.0 / RatedPowerW;
    Assert.AreEqual(expected, ion.ThrustOutputFraction, 0.02,
        "Thrust fraction should track EC bottleneck (~0.435)");
  }

  // ─── ActivateForBurn (DV-sim hook) ──────────────────────────────────

  [TestMethod]
  public void ActivateForBurn_ClearsTripLatch() {
    var ion = MakeIon();
    ion.Tripped = true;
    ion.TripReason = IonTripReason.Overtemp;

    ion.ActivateForBurn();

    Assert.IsFalse(ion.Tripped, "DV sim should never inherit a tripped engine");
    Assert.AreEqual(IonTripReason.None, ion.TripReason);
    Assert.IsTrue(ion.Active);
    Assert.AreEqual(1.0, ion.Throttle, 1e-9);
  }
}
