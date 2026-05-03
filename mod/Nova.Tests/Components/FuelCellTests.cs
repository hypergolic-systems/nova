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
  private const double SmallLh2ManifoldCap = 1.0;
  private const double SmallLoxManifoldCap = 0.466;
  private const double SmallRefillRateLh2 = 0.1;

  private static FuelCell MakeFuelCell(
      bool isActive = false,
      double ec = SmallEcOutput,
      double lh2Manifold = SmallLh2ManifoldCap,   // start full unless overridden
      double loxManifold = SmallLoxManifoldCap,
      bool refillActive = false) {
    return new FuelCell {
      Lh2Rate = SmallLh2Rate,
      LoxRate = SmallLoxRate,
      EcOutput = ec,
      RefillRateLh2 = SmallRefillRateLh2,
      Lh2Manifold = new Accumulator { Capacity = SmallLh2ManifoldCap, Contents = lh2Manifold },
      LoxManifold = new Accumulator { Capacity = SmallLoxManifoldCap, Contents = loxManifold },
      IsActive = isActive,
      RefillActive = refillActive,
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
      MaxRate = 10000,
      Tanks = {
        new Buffer {
          Resource = resource,
          Capacity = capacity,
          Contents = capacity,
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
      Lh2Rate = 1.0, LoxRate = 0.5, EcOutput = 1000,
      RefillRateLh2 = 0.2,
      Lh2Manifold = new Accumulator { Capacity = 2.0, Contents = 1.5 },
      LoxManifold = new Accumulator { Capacity = 1.0, Contents = 0.8 },
      IsActive = true, RefillActive = true,
    };
    var dst = (FuelCell)src.Clone();
    Assert.AreEqual(1.0, dst.Lh2Rate);
    Assert.AreEqual(0.5, dst.LoxRate);
    Assert.AreEqual(1000, dst.EcOutput);
    Assert.AreEqual(2.0, dst.Lh2Manifold.Capacity);
    Assert.AreEqual(1.0, dst.LoxManifold.Capacity);
    Assert.AreEqual(0.2, dst.RefillRateLh2);
    Assert.AreEqual(1.5, dst.Lh2Manifold.Contents);
    Assert.AreEqual(0.8, dst.LoxManifold.Contents);
    Assert.IsTrue(dst.IsActive);
    Assert.IsTrue(dst.RefillActive);
  }

  [TestMethod]
  public void State_RoundTripsAllFields() {
    var src = new FuelCell {
      IsActive = true, RefillActive = true,
      Lh2Manifold = new Accumulator { Capacity = 1.0, Contents = 0.42 },
      LoxManifold = new Accumulator { Capacity = 0.5, Contents = 0.19 },
    };
    var state = new Nova.Core.Persistence.Protos.PartState();
    src.Save(state);

    var dst = new FuelCell {
      // Capacity isn't persisted (cfg-derived); seed the load target.
      Lh2Manifold = new Accumulator { Capacity = 1.0 },
      LoxManifold = new Accumulator { Capacity = 0.5 },
    };
    dst.Load(state);
    Assert.IsTrue(dst.IsActive);
    Assert.IsTrue(dst.RefillActive);
    Assert.AreEqual(0.42, dst.Lh2Manifold.Contents);
    Assert.AreEqual(0.19, dst.LoxManifold.Contents);
  }

  // ---------- Production hysteresis ----------

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

  // ---------- Refill hysteresis ----------

  [TestMethod]
  public void Refill_TurnsOnWhenManifoldLow() {
    var fc = MakeFuelCell(isActive: false, refillActive: false,
        lh2Manifold: 0.05);  // 5% of 1 L
    var battery = MakeBattery(capacity: 1000, contents: 500);
    var vessel = BuildVessel(fc, battery, MakeTank(Resource.LiquidHydrogen, 10),
        MakeTank(Resource.LiquidOxygen, 10));

    vessel.Tick(1);

    Assert.IsTrue(fc.RefillActive, "Manifold below 10% should flip refill ON");
  }

  [TestMethod]
  public void Refill_TurnsOffWhenManifoldFull() {
    var fc = MakeFuelCell(isActive: false, refillActive: true,
        lh2Manifold: SmallLh2ManifoldCap, loxManifold: SmallLoxManifoldCap);
    var battery = MakeBattery(capacity: 1000, contents: 500);
    var vessel = BuildVessel(fc, battery, MakeTank(Resource.LiquidHydrogen, 10),
        MakeTank(Resource.LiquidOxygen, 10));

    vessel.Tick(1);

    Assert.IsFalse(fc.RefillActive, "Both manifolds at capacity should flip refill OFF");
  }

  [TestMethod]
  public void Refill_HoldsInBand() {
    // 50% manifold, refill OFF — neither threshold crossed, stays OFF.
    var fc = MakeFuelCell(isActive: false, refillActive: false,
        lh2Manifold: 0.5, loxManifold: 0.233);
    var battery = MakeBattery(capacity: 1000, contents: 500);
    var vessel = BuildVessel(fc, battery, MakeTank(Resource.LiquidHydrogen, 10),
        MakeTank(Resource.LiquidOxygen, 10));

    vessel.Tick(1);

    Assert.IsFalse(fc.RefillActive);
  }

  // ---------- Manifold integration ----------

  [TestMethod]
  public void Manifold_DrainsWhileProducing() {
    // ON cell + thirsty battery + plenty of EC sink. Tick a window and
    // confirm manifold contents drop. The drain rate is small (μL/s) so
    // we look at the trend, not an exact figure.
    var fc = MakeFuelCell(isActive: true);
    var battery = MakeBattery(capacity: 100000, contents: 10000);
    // No main tanks → refill can't run → manifold drains in isolation.
    var vessel = BuildVessel(fc, battery);

    // First Tick establishes Activities; integration before that runs
    // with the default zero rates and would no-op the manifold.
    vessel.Tick(0.001);
    double initialLh2 = fc.Lh2Manifold.Contents;
    vessel.Tick(60.001);

    Assert.IsTrue(fc.Lh2Manifold.Contents < initialLh2,
        $"Expected manifold drain, got {initialLh2} → {fc.Lh2Manifold.Contents}");
  }

  [TestMethod]
  public void Manifold_RefillsWhenLow() {
    // Start at 5% manifold with cell OFF (no production), main tank full.
    // Refill should trip on, fill manifold, trip off near capacity.
    var fc = MakeFuelCell(isActive: false, refillActive: false,
        lh2Manifold: 0.05, loxManifold: 0.0233);
    // Battery above 80% so production stays OFF.
    var battery = MakeBattery(capacity: 1000, contents: 900);
    var vessel = BuildVessel(fc, battery, MakeTank(Resource.LiquidHydrogen, 10),
        MakeTank(Resource.LiquidOxygen, 10));

    // Warmup so the first integration uses the post-solve refill rate.
    vessel.Tick(0.001);
    vessel.Tick(20.001);  // 20 s — well past the ~9.5 s refill at 0.1 L/s

    Assert.IsTrue(fc.Lh2Manifold.Contents > 0.9 * SmallLh2ManifoldCap,
        $"Expected near-full manifold, got {fc.Lh2Manifold.Contents}");
  }

  [TestMethod]
  public void Manifold_StarvationStopsProduction() {
    // Empty manifold, no main tanks → refill can't fill, production
    // can't continue. Confirm CurrentOutput is zero.
    var fc = MakeFuelCell(isActive: true, lh2Manifold: 0, loxManifold: 0);
    var battery = MakeBattery(capacity: 1000, contents: 100);  // SoC=0.1, wants on
    var vessel = BuildVessel(fc, battery);  // no LH2/LOx tanks

    vessel.Tick(1);

    Assert.IsTrue(fc.IsActive, "Cell wants to be ON (SoC=0.1)");
    Assert.AreEqual(0, fc.CurrentOutput, 1e-6,
        "But starved manifold + empty tanks → no production");
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

  [TestMethod]
  public void ValidUntil_ProductionProjectsToManifoldEmpty() {
    // ON cell, full manifold, no main tanks → refill is blocked, so
    // production drains the manifold in isolation. ValidUntilSeconds
    // should track time-to-empty rather than the (longer) SoC flip.
    // Use a deliberately oversized battery so dtSocFlip ≫ dtMfdEmpty
    // and the manifold-empty term is the binding one.
    var fc = MakeFuelCell(isActive: true);
    var battery = MakeBattery(capacity: 1e7, contents: 1e6);  // SoC=0.1, dtSocFlip≈2800s
    var vessel = BuildVessel(fc, battery);

    vessel.Tick(1);

    // Expected: manifold ≈ 1 L LH₂, drain ≈ Activity·SmallLh2Rate.
    // With Activity ≈ 1, dt ≈ 1.0 / 4.96e-4 ≈ 2016 s.
    Assert.IsTrue(fc.ValidUntilSeconds > 1500 && fc.ValidUntilSeconds < 2500,
        $"Expected ~2016 s manifold-empty horizon, got {fc.ValidUntilSeconds}");
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
