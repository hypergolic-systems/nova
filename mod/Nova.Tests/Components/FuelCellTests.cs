using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nova.Core.Components;
using Nova.Core.Components.Electrical;
using Nova.Core.Components.Propulsion;
using Nova.Core.Resources;
using Buffer = Nova.Core.Resources.Buffer;
namespace Nova.Tests.Components;

[TestClass]
public class FuelCellTests {

  private const double SmallLh2Rate = 4.96e-4;
  private const double SmallLoxRate = 2.31e-4;
  private const double SmallEcOutput = 2500;

  private static FuelCell MakeFuelCell(bool isActive = false, double ec = SmallEcOutput) {
    return new FuelCell {
      Lh2Rate = SmallLh2Rate,
      LoxRate = SmallLoxRate,
      EcOutput = ec,
      IsActive = isActive,
    };
  }

  private static Battery MakeBattery(double capacity, double contents) {
    return new Battery {
      Buffer = new Buffer {
        Resource = Resource.ElectricCharge,
        Capacity = capacity,
        Contents = contents,
        // Big enough to absorb the small fuel cell's full output without
        // throttling. The default Battery limit (10) would cap fill at
        // 10 W, which is well below the small cell's 2.5 kW.
        MaxRateIn = 1e6,
        MaxRateOut = 1e6,
      },
    };
  }

  private static TankVolume MakeTank(Resource resource, double capacity) {
    return new TankVolume {
      Volume = capacity,
      Tanks = {
        new Buffer {
          Resource = resource,
          Capacity = capacity,
          Contents = capacity,
          MaxRateIn = double.PositiveInfinity,
          MaxRateOut = double.PositiveInfinity,
        },
      },
    };
  }

  /// Build a single-part vessel with the given components. The orchestrator
  /// works at vessel scope, so even unit-style hysteresis tests need a
  /// vessel + Tick to drive UpdateFuelCellDevices.
  private static VirtualVessel BuildVessel(params VirtualComponent[] components) {
    var vv = new VirtualVessel();
    vv.AddPart(1u, "test", 1.0, components.ToList());
    vv.UpdatePartTree(new Dictionary<uint, uint?> { { 1u, null } });
    vv.InitializeSolver(0);
    return vv;
  }

  // ---------- Construction + factory mapping ----------

  [TestMethod]
  public void Clone_CopiesAllState() {
    var src = new FuelCell {
      Lh2Rate = 1.0, LoxRate = 0.5, EcOutput = 1000, IsActive = true,
    };
    var dst = (FuelCell)src.Clone();
    Assert.AreEqual(1.0, dst.Lh2Rate);
    Assert.AreEqual(0.5, dst.LoxRate);
    Assert.AreEqual(1000, dst.EcOutput);
    Assert.IsTrue(dst.IsActive);
  }

  [TestMethod]
  public void Structure_RoundTripsThroughProto() {
    var src = new FuelCell { Lh2Rate = 1e-3, LoxRate = 5e-4, EcOutput = 5000 };
    var ps = new Nova.Core.Persistence.Protos.PartStructure();
    src.SaveStructure(ps);

    var dst = new FuelCell();
    dst.LoadStructure(ps);
    Assert.AreEqual(1e-3, dst.Lh2Rate);
    Assert.AreEqual(5e-4, dst.LoxRate);
    Assert.AreEqual(5000, dst.EcOutput);
  }

  [TestMethod]
  public void State_RoundTripsIsActive() {
    var src = new FuelCell { IsActive = true };
    var state = new Nova.Core.Persistence.Protos.PartState();
    src.Save(state);

    var dst = new FuelCell();
    dst.Load(state);
    Assert.IsTrue(dst.IsActive);
  }

  // ---------- Hysteresis ----------

  [TestMethod]
  public void Hysteresis_TurnsOnBelowTwentyPercent() {
    var fc = MakeFuelCell(isActive: false);
    var battery = MakeBattery(capacity: 1000, contents: 150); // SoC = 0.15
    var vessel = BuildVessel(fc, battery, MakeTank(Resource.LiquidHydrogen, 10),
        MakeTank(Resource.LiquidOxygen, 10));

    vessel.Tick(1);

    Assert.IsTrue(fc.IsActive, "SoC=0.15 should flip the cell ON");
  }

  [TestMethod]
  public void Hysteresis_TurnsOffAboveEightyPercent() {
    var fc = MakeFuelCell(isActive: true);
    var battery = MakeBattery(capacity: 1000, contents: 850); // SoC = 0.85
    var vessel = BuildVessel(fc, battery, MakeTank(Resource.LiquidHydrogen, 10),
        MakeTank(Resource.LiquidOxygen, 10));

    vessel.Tick(1);

    Assert.IsFalse(fc.IsActive, "SoC=0.85 should flip the cell OFF");
  }

  [TestMethod]
  public void Hysteresis_HoldsInBand_AboveOnThreshold() {
    // OFF + SoC=0.5 should stay OFF.
    var fc = MakeFuelCell(isActive: false);
    var battery = MakeBattery(capacity: 1000, contents: 500);
    var vessel = BuildVessel(fc, battery, MakeTank(Resource.LiquidHydrogen, 10),
        MakeTank(Resource.LiquidOxygen, 10));

    vessel.Tick(1);

    Assert.IsFalse(fc.IsActive);
  }

  [TestMethod]
  public void Hysteresis_HoldsInBand_BelowOffThreshold() {
    // ON + SoC=0.5 should stay ON.
    var fc = MakeFuelCell(isActive: true);
    var battery = MakeBattery(capacity: 1000, contents: 500);
    var vessel = BuildVessel(fc, battery, MakeTank(Resource.LiquidHydrogen, 10),
        MakeTank(Resource.LiquidOxygen, 10));

    vessel.Tick(1);

    Assert.IsTrue(fc.IsActive);
  }

  [TestMethod]
  public void Hysteresis_NoBatteries_ForcesActive() {
    var fc = MakeFuelCell(isActive: false);
    var vessel = BuildVessel(fc, MakeTank(Resource.LiquidHydrogen, 10),
        MakeTank(Resource.LiquidOxygen, 10));

    vessel.Tick(1);

    Assert.IsTrue(fc.IsActive,
        "With no batteries to gate against, the cell runs continuously");
  }

  // ---------- Forecast (post-solve, on converged rates) ----------

  [TestMethod]
  public void ValidUntil_Charging_ProjectsToOffThreshold() {
    // Cell is ON, batteries below 80%. Pure fuel cell + battery vessel
    // (no consumers) — the LP converges with battery rate ≈ EcOutput, and
    // ValidUntilSeconds = (0.8·capacity − contents) / rate.
    double ec = 1000;
    var fc = MakeFuelCell(isActive: true, ec: ec);
    var battery = MakeBattery(capacity: 10000, contents: 5000); // SoC=0.5
    var vessel = BuildVessel(fc, battery, MakeTank(Resource.LiquidHydrogen, 10),
        MakeTank(Resource.LiquidOxygen, 10));

    vessel.Tick(1);

    // Expect: rate ≈ +1000 W charging, remaining = 0.8·10000 − 5000 = 3000.
    // dt ≈ 3000 / 1000 = 3.0 s. Allow a small fudge for LP conservatism.
    Assert.IsTrue(fc.IsActive);
    Assert.IsTrue(fc.ValidUntilSeconds > 2.5 && fc.ValidUntilSeconds < 3.5,
        $"Expected ~3.0 s, got {fc.ValidUntilSeconds}");
  }

  [TestMethod]
  public void ValidUntil_NotCharging_ReturnsInfinity() {
    // Cell is OFF and there's nothing else producing — the battery rate
    // is 0, so no flip is reachable from this state.
    var fc = MakeFuelCell(isActive: false);
    var battery = MakeBattery(capacity: 1000, contents: 500);
    var vessel = BuildVessel(fc, battery, MakeTank(Resource.LiquidHydrogen, 10),
        MakeTank(Resource.LiquidOxygen, 10));

    vessel.Tick(1);

    Assert.IsFalse(fc.IsActive);
    Assert.AreEqual(double.PositiveInfinity, fc.ValidUntilSeconds);
  }

  [TestMethod]
  public void ValidUntil_NoBatteries_ReturnsInfinity() {
    var fc = MakeFuelCell(isActive: false);
    var vessel = BuildVessel(fc, MakeTank(Resource.LiquidHydrogen, 10),
        MakeTank(Resource.LiquidOxygen, 10));

    vessel.Tick(1);

    Assert.AreEqual(double.PositiveInfinity, fc.ValidUntilSeconds);
  }

  // ---------- Integration ----------

  [TestMethod]
  public void Integration_BatteryStaysAboveOnThreshold_WhileCellRunning() {
    // ON cell + battery + ample LH2/LOx, no consumers. Run a few seconds;
    // the LP should be charging the battery and CurrentOutput should
    // match (or approach) the rated output.
    var fc = MakeFuelCell(isActive: true);
    var battery = MakeBattery(capacity: 100000, contents: 10000); // SoC=0.10
    var vessel = BuildVessel(fc, battery, MakeTank(Resource.LiquidHydrogen, 10),
        MakeTank(Resource.LiquidOxygen, 10));

    vessel.Tick(1);

    Assert.IsTrue(fc.IsActive, "SoC=0.10 starts ON");
    Assert.IsTrue(fc.CurrentOutput > 0, "Cell should be producing EC");
    // Rough sanity: producing within 10% of rated when battery is hungry.
    Assert.IsTrue(fc.CurrentOutput > 0.9 * SmallEcOutput,
        $"Expected ≈{SmallEcOutput} W, got {fc.CurrentOutput}");
  }
}
