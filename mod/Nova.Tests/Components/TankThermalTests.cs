using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nova.Core;
using Nova.Core.Components;
using Nova.Core.Components.Electrical;
using Nova.Core.Components.Propulsion;
using Nova.Core.Components.Thermal;
using Nova.Core.Resources;
using Nova.Core.Systems;
using Buffer = Nova.Core.Resources.Buffer;
using PartStructure = Nova.Core.Persistence.Protos.PartStructure;
using InsulationTier = Nova.Core.Components.Propulsion.InsulationTier;

namespace Nova.Tests.Components;

[TestClass]
public class TankThermalTests {

  private const double SecondsPerDay = 86400.0;

  // Baseline (3%/day for LH2) × passive(1.0 for MLI) × dt(1 s) / day-in-seconds.
  // ≈ -1000 L × 0.03 / 86400 = -3.47e-4 L/s.
  private const double Tolerance = 1e-9;

  // ─── Static math: per-slice helpers without OnBuildSystems ────────

  private static TankVolume MakeCryoTank(string resource, double capacity,
      InsulationTier tier, double volume = -1, int stage = -1) {
    if (volume < 0) {
      volume = capacity * (1.0 + InsulationTierTable.VolumePenalty(tier));
    }
    if (stage < 0) stage = InsulationTierTable.MaxStage(tier);
    return new TankVolume {
      Volume = volume,
      MaxRate = 1000,
      Tanks = { new Buffer { Resource = Resource.Get(resource), Capacity = capacity, Contents = capacity } },
      Tiers = { tier },
      CoolerStages = { stage },
    };
  }

  [TestMethod]
  public void Mli_LH2_NetBoiloffEqualsResourceBaseline() {
    var tank = MakeCryoTank("Liquid Hydrogen", 1000, InsulationTier.MLI);
    Assert.AreEqual(0.03, tank.SliceNetBoiloffFractionPerDay(0), Tolerance);
  }

  [TestMethod]
  public void HeavyMli_LH2_NetBoiloffIsTenPercentOfBaseline() {
    var tank = MakeCryoTank("Liquid Hydrogen", 1000, InsulationTier.HeavyMLI);
    Assert.AreEqual(0.003, tank.SliceNetBoiloffFractionPerDay(0), Tolerance);
  }

  [TestMethod]
  public void Bac_WithoutDevice_DegradesToPassive() {
    // Pre-OnBuildSystems (no CoolerDevices populated): Activity defaults
    // to 0 → blend evaluates to the tier's passive fraction (10% of
    // baseline for BAC). Same effective rate as HeavyMLI alone — captures
    // the realistic "cooler offline" degraded mode.
    var tank = MakeCryoTank("Liquid Hydrogen", 1000, InsulationTier.BAC);
    Assert.AreEqual(0.003, tank.SliceNetBoiloffFractionPerDay(0), Tolerance);
  }

  [TestMethod]
  public void Rp1_Storable_NoBoiloffRegardlessOfTier() {
    var tank = MakeCryoTank("RP-1", 1000, InsulationTier.ZBO);
    Assert.AreEqual(0.0, tank.SliceNetBoiloffFractionPerDay(0), Tolerance);
    Assert.AreEqual(0.0, tank.SliceMaxEcW(0), Tolerance);
    Assert.AreEqual(0.0, tank.SliceMaxHeatW(0), Tolerance);
  }

  // Recompute the expected EC draw from the physical model — kept
  // alongside the implementation as a second source of truth so a
  // formula bug is caught here even if the implementation changes
  // shape. Mirrors the math documented in InsulationTier.cs.
  private static double ExpectedMaxEcW(Resource res, InsulationTier tier, double capacity) {
    var data = InsulationTierTable.Get(tier);
    if (!data.IsActive) return 0;
    var deltaT = InsulationTierTable.AmbientK - res.BoilingPointK;
    var qBaseline = capacity * res.MliBoiloffFractionPerDay
                  * res.Density * res.LatentHeatJPerKg / SecondsPerDay;
    var qRemove = qBaseline * (data.PassiveFraction - data.ActiveFraction);
    var cop = data.CarnotEfficiency * res.BoilingPointK / deltaT;
    return qRemove / cop;
  }

  [TestMethod]
  public void BacEc_MatchesPhysicalModel_ForCryogens() {
    foreach (var name in new[] { "Liquid Hydrogen", "Liquid Oxygen", "Methane" }) {
      var tank = MakeCryoTank(name, 1000, InsulationTier.BAC);
      var expected = ExpectedMaxEcW(Resource.Get(name), InsulationTier.BAC, 1000);
      Assert.AreEqual(expected, tank.SliceMaxEcW(0), 1e-9, name);
    }
  }

  [TestMethod]
  public void ZboEc_MatchesPhysicalModel_ForCryogens() {
    foreach (var name in new[] { "Liquid Hydrogen", "Liquid Oxygen", "Methane" }) {
      var tank = MakeCryoTank(name, 1000, InsulationTier.ZBO);
      var expected = ExpectedMaxEcW(Resource.Get(name), InsulationTier.ZBO, 1000);
      Assert.AreEqual(expected, tank.SliceMaxEcW(0), 1e-9, name);
    }
  }

  [TestMethod]
  public void HeatOut_EqualsEcInputPlusCoolingLoad() {
    // Q_hot = Q_cold + W_in. Q_cold = ec × COP, so Q_hot = ec × (1 + COP).
    // Cross-checked against the model formula for sanity.
    foreach (var name in new[] { "Liquid Hydrogen", "Liquid Oxygen", "Methane" }) {
      foreach (var tier in new[] { InsulationTier.BAC, InsulationTier.ZBO }) {
        var tank = MakeCryoTank(name, 1000, tier);
        var res = Resource.Get(name);
        var data = InsulationTierTable.Get(tier);
        var cop = data.CarnotEfficiency * res.BoilingPointK
                / (InsulationTierTable.AmbientK - res.BoilingPointK);
        Assert.AreEqual(tank.SliceMaxEcW(0) * (1.0 + cop),
                        tank.SliceMaxHeatW(0), 1e-9,
                        $"{name} {tier}");
      }
    }
  }

  [TestMethod]
  public void Lh2CoolingCosts_MoreEcThanLox_AtSameTier() {
    // Physical anchor — the whole point of switching off a flat per-litre
    // EC table to a temperature-delta model is that LH₂ at 20 K is
    // unambiguously more expensive than LOX at 90 K under any active
    // tier. Catches accidental simplifications that flatten this back out.
    foreach (var tier in new[] { InsulationTier.BAC, InsulationTier.ZBO }) {
      var lh2 = MakeCryoTank("Liquid Hydrogen", 1000, tier);
      var lox = MakeCryoTank("Liquid Oxygen", 1000, tier);
      Assert.IsTrue(lh2.SliceMaxEcW(0) > lox.SliceMaxEcW(0) * 1.5,
          $"{tier}: LH2 EC {lh2.SliceMaxEcW(0)} should be > 1.5× LOX EC {lox.SliceMaxEcW(0)}");
    }
  }

  [TestMethod]
  public void EcDraw_ScalesLinearlyWithCapacity() {
    // Heat leak ∝ slice capacity, so EC must too. Holding tier and
    // resource fixed, doubling capacity must double draw exactly.
    var small = MakeCryoTank("Liquid Hydrogen", 500, InsulationTier.BAC);
    var large = MakeCryoTank("Liquid Hydrogen", 5000, InsulationTier.BAC);
    Assert.AreEqual(small.SliceMaxEcW(0) * 10, large.SliceMaxEcW(0), 1e-9);
  }

  // ─── End-to-end through VirtualVessel (Solve + cooler LP) ──────────

  private static Dictionary<uint, uint?> ParentMap((uint id, uint[] children)[] defs) {
    var map = new Dictionary<uint, uint?>();
    foreach (var (id, _) in defs)
      if (!map.ContainsKey(id)) map[id] = null;
    foreach (var (id, children) in defs)
      foreach (var c in children) map[c] = id;
    return map;
  }

  private static (VirtualVessel vessel, TankVolume tank) BuildBacVessel(
      double batteryRate, double batteryContents, double radiatorW) {
    // pod → tank → battery → radiator
    var defs = new[] {
      (1u, new uint[] { 2 }),
      (2u, new uint[] { 3 }),
      (3u, new uint[] { 4 }),
      (4u, System.Array.Empty<uint>()),
    };
    var tank = MakeCryoTank("Liquid Hydrogen", 1000, InsulationTier.BAC);
    var battery = new Battery {
      Buffer = new Buffer {
        Resource = Resource.ElectricCharge,
        Capacity = System.Math.Max(batteryContents, 1),
        Contents = batteryContents,
        MaxRateIn = batteryRate,
        MaxRateOut = batteryRate,
      },
    };
    var radiator = new Radiator {
      VacuumCoolingW = radiatorW,
      AtmCoolingW = radiatorW,
      EcPerWattCooling = 0,
      IsDeployed = true,
    };

    var vessel = new VirtualVessel();
    vessel.AddPart(1, "pod",      0, new List<VirtualComponent>());
    vessel.AddPart(2, "tank",     0, new List<VirtualComponent> { tank });
    vessel.AddPart(3, "battery",  0, new List<VirtualComponent> { battery });
    vessel.AddPart(4, "radiator", 0, new List<VirtualComponent> { radiator });
    vessel.UpdatePartTree(ParentMap(defs));
    vessel.InitializeSolver(0);
    return (vessel, tank);
  }

  [TestMethod]
  public void Bac_WithFullSupply_RunsAtActiveRate() {
    // Plenty of EC + a radiator that comfortably exceeds the slice's
    // computed waste-heat output. Activity should reach ~1.0 and the
    // slice's realised boiloff should collapse to the tier's active
    // fraction (1% of LH2's 3%/day baseline = 0.0003).
    var (vessel, tank) = BuildBacVessel(batteryRate: 500, batteryContents: 1e6, radiatorW: 500);
    vessel.Solve();

    var expectedEc = ExpectedMaxEcW(Resource.LiquidHydrogen, InsulationTier.BAC, 1000);
    Assert.AreEqual(0.0003, tank.SliceNetBoiloffFractionPerDay(0), 1e-9);
    Assert.AreEqual(expectedEc, tank.SliceCurrentEcW(0), 1e-6);
    Assert.IsTrue(tank.SliceCurrentHeatW(0) > tank.SliceCurrentEcW(0),
        "Cooler heat output must exceed EC draw (Q_hot = Q_cold + W_in)");
  }

  [TestMethod]
  public void Bac_WithStarvedEc_DegradesToPassive() {
    // Empty battery, no other EC source → cooler activity = 0 → slice
    // boiloff degrades to BAC's passive fraction (= HeavyMLI baseline).
    var (vessel, tank) = BuildBacVessel(batteryRate: 500, batteryContents: 0, radiatorW: 200);
    vessel.Solve();

    Assert.AreEqual(0.003, tank.SliceNetBoiloffFractionPerDay(0), 1e-9);
    Assert.AreEqual(0.0, tank.SliceCurrentEcW(0), 1e-6);
    Assert.AreEqual(0.0, tank.SliceCurrentHeatW(0), 1e-6);
  }

  [TestMethod]
  public void OnPostSolve_WritesBoiloffToBackgroundDrainRate() {
    // Boiloff lives on Buffer.BackgroundDrainRate (engine Rate stays
    // at 0 with no engine on the vessel). The split lets
    // DeltaVSimulation's tier-spent check read engine Rate alone
    // without false positives from slow background drain.
    var (vessel, tank) = BuildPassiveVessel(InsulationTier.MLI);
    vessel.Solve();

    Assert.AreEqual(0.0, tank.Tanks[0].Rate, 1e-12, "engine rate should be 0 with no engine");
    Assert.AreEqual(-1000.0 * 0.03 / SecondsPerDay, tank.Tanks[0].BackgroundDrainRate, 1e-12);
  }

  [TestMethod]
  public void OnPostSolve_HeavyMli_TenPercentOfMliRate() {
    var (vessel, tank) = BuildPassiveVessel(InsulationTier.HeavyMLI);
    vessel.Solve();
    Assert.AreEqual(-1000.0 * 0.003 / SecondsPerDay, tank.Tanks[0].BackgroundDrainRate, 1e-12);
  }

  private static (VirtualVessel vessel, TankVolume tank) BuildPassiveVessel(InsulationTier tier) {
    var defs = new[] {
      (1u, new uint[] { 2 }),
      (2u, System.Array.Empty<uint>()),
    };
    var tank = MakeCryoTank("Liquid Hydrogen", 1000, tier);
    var vessel = new VirtualVessel();
    vessel.AddPart(1, "pod",  0, new List<VirtualComponent>());
    vessel.AddPart(2, "tank", 0, new List<VirtualComponent> { tank });
    vessel.UpdatePartTree(ParentMap(defs));
    vessel.InitializeSolver(0);
    return (vessel, tank);
  }

  // ─── Persistence round-trip ────────────────────────────────────────

  [TestMethod]
  public void Tier_RoundTripsThroughStructure() {
    var tank = new TankVolume { Volume = 4000 };
    tank.Tanks.Add(new Buffer { Resource = Resource.LiquidHydrogen, Capacity = 2960 });
    tank.Tanks.Add(new Buffer { Resource = Resource.LiquidOxygen,   Capacity = 1040 });
    tank.Tiers.Add(InsulationTier.ZBO);
    tank.Tiers.Add(InsulationTier.HeavyMLI);

    var ps = new PartStructure();
    tank.SaveStructure(ps);
    var fresh = new TankVolume();
    fresh.LoadStructure(ps);

    Assert.AreEqual(2, fresh.Tiers.Count);
    Assert.AreEqual(InsulationTier.ZBO, fresh.Tiers[0]);
    Assert.AreEqual(InsulationTier.HeavyMLI, fresh.Tiers[1]);
  }
}
