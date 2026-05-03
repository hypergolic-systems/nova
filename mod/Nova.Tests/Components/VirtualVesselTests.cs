using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nova.Core;
using Nova.Core.Components;
using Nova.Core.Components.Electrical;
using Nova.Core.Components.Propulsion;
using Nova.Core.Components.Structural;
using Nova.Core.Resources;
using Nova.Core.Flight;
using Nova.Core.Utils;
using Buffer = Nova.Core.Resources.Buffer;

namespace Nova.Tests.Components;

[TestClass]
public class VirtualVesselTests {

  /// <summary>
  /// Build a parentMap from (id, parentId_or_null, children) definitions.
  /// partDefs: list of (id, partName, childIds). First entry is root.
  /// </summary>
  private static Dictionary<uint, uint?> BuildParentMap(
      (uint id, string partName, uint[] children)[] partDefs) {
    var map = new Dictionary<uint, uint?>();
    foreach (var (id, _, children) in partDefs) {
      if (!map.ContainsKey(id))
        map[id] = null; // root if not set as child elsewhere
      foreach (var cid in children)
        map[cid] = id;
    }
    return map;
  }

  private static Engine MakeEngine(double thrust, double isp, params (string resource, double ratio)[] propellants) {
    var engine = new Engine();
    engine.Initialize(thrust, isp,
      propellants.Select(p => (Resource.Get(p.resource), p.ratio)).ToList());
    return engine;
  }

  private static TankVolume MakeTank(double volume, string resource, double capacity, double contents = -1) {
    if (contents < 0) contents = capacity;
    return new TankVolume {
      Volume = volume,
      MaxRate = 10000,
      Tanks = { new Buffer {
        Resource = Resource.Get(resource),
        Capacity = capacity,
        Contents = contents,
      }}
    };
  }

  // Two-resource tank — kerolox-style 40% RP-1 + 60% LOX volumetric mix.
  private static TankVolume MakeKeroloxTank(double volume, double maxRate = 10000) {
    return new TankVolume {
      Volume = volume,
      MaxRate = maxRate,
      Tanks = {
        new Buffer { Resource = Resource.RP1, Capacity = volume * 0.4, Contents = volume * 0.4 },
        new Buffer { Resource = Resource.LiquidOxygen, Capacity = volume * 0.6, Contents = volume * 0.6 },
      },
    };
  }

  private static Engine MakeKeroloxEngine(double thrust = 100, double isp = 300) {
    var engine = new Engine();
    engine.Initialize(thrust, isp, new List<(Resource, double)> {
      (Resource.RP1, 2),
      (Resource.LiquidOxygen, 3),
    });
    return engine;
  }

  private static Decoupler MakeDecoupler(int priority = 1, params string[] allowedResources) {
    var d = new Decoupler { Priority = priority };
    foreach (var r in allowedResources)
      d.AllowedResources.Add(Resource.Get(r));
    return d;
  }

  // Asparagus-style: allowed resources flow up-only (child → parent).
  private static Decoupler MakeUpOnlyDecoupler(int priority = 1, params string[] allowedResources) {
    var d = new Decoupler { Priority = priority };
    foreach (var r in allowedResources) {
      var res = Resource.Get(r);
      d.AllowedResources.Add(res);
      d.UpOnlyResources.Add(res);
    }
    return d;
  }

  [TestMethod]
  public void DecouplerBlocksResourceFlow() {
    // KSP tree: pod(1) → tank(2) → decoupler(3) → engine(4)
    var partDefs = new[] {
      (1u, "pod", new uint[] { 2 }),
      (2u, "tank", new uint[] { 3 }),
      (3u, "decoupler", new uint[] { 4 }),
      (4u, "engine", Array.Empty<uint>()),
    };

    var vessel = new VirtualVessel();
    vessel.AddPart(1, "pod", 0, new List<VirtualComponent>());
    vessel.AddPart(2, "tank", 0, new List<VirtualComponent> { MakeTank(100, "RP-1", 100) });
    vessel.AddPart(3, "decoupler", 0, new List<VirtualComponent> { MakeDecoupler() });
    vessel.AddPart(4, "engine", 0, new List<VirtualComponent> { MakeEngine(100, 300, ("RP-1", 1)) });
    vessel.UpdatePartTree(BuildParentMap(partDefs));
    vessel.InitializeSolver(0);

    var engine = vessel.AllComponents().OfType<Engine>().First();
    engine.Throttle = 1.0;
    vessel.Solve();

    Assert.AreEqual(0, engine.Satisfaction, 0.01, "Engine should be starved — decoupler blocks fuel from tank above");
  }

  [TestMethod]
  public void DecouplerAllowsSpecificResource() {
    var partDefs = new[] {
      (1u, "pod", new uint[] { 2 }),
      (2u, "solarPanel", new uint[] { 3 }),
      (3u, "decoupler", new uint[] { 4 }),
      (4u, "light", Array.Empty<uint>()),
    };

    var vessel = new VirtualVessel();
    vessel.AddPart(1, "pod", 0, new List<VirtualComponent>());
    vessel.AddPart(2, "solarPanel", 0, new List<VirtualComponent> {
      new Battery { Buffer = new Buffer {
        Resource = Resource.ElectricCharge, Capacity = 100, Contents = 100,
        MaxRateIn = 10, MaxRateOut = 10,
      }}
    });
    vessel.AddPart(3, "decoupler", 0, new List<VirtualComponent> {
      MakeDecoupler(1, "Electric Charge")
    });
    vessel.AddPart(4, "light", 0, new List<VirtualComponent> { new Light { Rate = 5 } });
    vessel.UpdatePartTree(BuildParentMap(partDefs));
    vessel.InitializeSolver(0);

    vessel.Solve();

    var light = vessel.AllComponents().OfType<Light>().First();
    Assert.AreEqual(5, light.ActualRate, 0.1, "Light should receive EC through decoupler");
  }

  [TestMethod]
  public void VesselWithoutDecoupler_FlatTopology() {
    var partDefs = new[] {
      (1u, "pod", new uint[] { 2 }),
      (2u, "tank", new uint[] { 3 }),
      (3u, "engine", Array.Empty<uint>()),
    };

    var vessel = new VirtualVessel();
    vessel.AddPart(1, "pod", 0, new List<VirtualComponent>());
    vessel.AddPart(2, "tank", 0, new List<VirtualComponent> { MakeTank(100, "RP-1", 100) });
    vessel.AddPart(3, "engine", 0, new List<VirtualComponent> { MakeEngine(100, 300, ("RP-1", 1)) });
    vessel.UpdatePartTree(BuildParentMap(partDefs));
    vessel.InitializeSolver(0);

    var engine = vessel.AllComponents().OfType<Engine>().First();
    engine.Throttle = 1.0;
    vessel.Solve();

    Assert.IsTrue(engine.Satisfaction > 0.5, "Engine should receive fuel without decoupler");
  }

  [TestMethod]
  public void LocalTankBelowDecoupler_EngineGetsFuel() {
    var partDefs = new[] {
      (1u, "pod", new uint[] { 2 }),
      (2u, "decoupler", new uint[] { 3 }),
      (3u, "tank", new uint[] { 4 }),
      (4u, "engine", Array.Empty<uint>()),
    };

    var vessel = new VirtualVessel();
    vessel.AddPart(1, "pod", 0, new List<VirtualComponent>());
    vessel.AddPart(2, "decoupler", 0, new List<VirtualComponent> { MakeDecoupler() });
    vessel.AddPart(3, "tank", 0, new List<VirtualComponent> { MakeTank(100, "RP-1", 100) });
    vessel.AddPart(4, "engine", 0, new List<VirtualComponent> { MakeEngine(100, 300, ("RP-1", 1)) });
    vessel.UpdatePartTree(BuildParentMap(partDefs));
    vessel.InitializeSolver(0);

    var engine = vessel.AllComponents().OfType<Engine>().First();
    engine.Throttle = 1.0;
    vessel.Solve();

    Assert.IsTrue(engine.Satisfaction > 0.5, "Engine should get fuel from local tank below decoupler");
  }

  [TestMethod]
  public void CloneRoundTrip_PreservesComponents() {
    var partDefs = new[] {
      (1u, "pod", new uint[] { 2 }),
      (2u, "decoupler", new uint[] { 3 }),
      (3u, "tank", Array.Empty<uint>()),
    };

    var vessel = new VirtualVessel();
    vessel.AddPart(1, "pod", 0, new List<VirtualComponent>());
    vessel.AddPart(2, "decoupler", 0, new List<VirtualComponent> { MakeDecoupler() });
    vessel.AddPart(3, "tank", 0, new List<VirtualComponent> { MakeTank(100, "RP-1", 100) });
    vessel.UpdatePartTree(BuildParentMap(partDefs));
    vessel.InitializeSolver(0);

    var vessel2 = vessel.Clone(0);
    var tanks = vessel2.AllComponents().OfType<TankVolume>().ToList();
    Assert.AreEqual(1, tanks.Count, "Should have one tank after round-trip");
  }

  [TestMethod]
  public void ExtractParts_SplitsVessel() {
    var partDefs = new[] {
      (1u, "pod", new uint[] { 2 }),
      (2u, "engine", Array.Empty<uint>()),
    };

    var vessel = new VirtualVessel();
    vessel.AddPart(1, "pod", 0, new List<VirtualComponent>());
    vessel.AddPart(2, "engine", 0, new List<VirtualComponent> { MakeEngine(100, 300, ("RP-1", 1)) });
    vessel.UpdatePartTree(BuildParentMap(partDefs));
    vessel.InitializeSolver(0);

    var extracted = vessel.ExtractParts(new HashSet<uint> { 2 });

    Assert.AreEqual(1, extracted.Count, "Should extract one part");
    Assert.IsTrue(extracted.ContainsKey(2), "Should contain the engine part");

    var remainingEngines = vessel.AllComponents().OfType<Engine>().Count();
    Assert.AreEqual(0, remainingEngines, "Original vessel should have no engines after extraction");
  }

  [TestMethod]
  public void AsparagusStaging_SideTanksDrainFirst() {
    var partDefs = new[] {
      (1u, "pod", new uint[] { 2, 4, 6 }),
      (2u, "coreTank", new uint[] { 3 }),
      (3u, "engine", Array.Empty<uint>()),
      (4u, "decoupler", new uint[] { 5 }),
      (5u, "sideTank", Array.Empty<uint>()),
      (6u, "decoupler", new uint[] { 7 }),
      (7u, "sideTank", Array.Empty<uint>()),
    };

    var vessel = new VirtualVessel();
    vessel.AddPart(1, "pod", 0, new List<VirtualComponent>());
    vessel.AddPart(2, "coreTank", 0, new List<VirtualComponent> { MakeTank(100, "RP-1", 100) });
    vessel.AddPart(3, "engine", 0, new List<VirtualComponent> { MakeEngine(100, 300, ("RP-1", 1)) });
    vessel.AddPart(4, "decoupler", 0, new List<VirtualComponent> { MakeDecoupler(1, "RP-1") });
    vessel.AddPart(5, "sideTank", 0, new List<VirtualComponent> { MakeTank(100, "RP-1", 100) });
    vessel.AddPart(6, "decoupler", 0, new List<VirtualComponent> { MakeDecoupler(1, "RP-1") });
    vessel.AddPart(7, "sideTank", 0, new List<VirtualComponent> { MakeTank(100, "RP-1", 100) });
    vessel.UpdatePartTree(BuildParentMap(partDefs));
    vessel.InitializeSolver(0);

    var engine = vessel.AllComponents().OfType<Engine>().First();
    engine.Throttle = 1.0;
    vessel.Solve();

    Assert.AreEqual(1.0, engine.Satisfaction, 0.01, "Engine should be fully satisfied");

    var allTanks = vessel.AllComponents().OfType<TankVolume>().ToList();
    var coreTank = allTanks.First(t => vessel.GetComponents(2).Contains(t));
    var sideTankL = allTanks.First(t => vessel.GetComponents(5).Contains(t));
    var sideTankR = allTanks.First(t => vessel.GetComponents(7).Contains(t));

    var totalSideDrain = sideTankL.Tanks[0].Rate + sideTankR.Tanks[0].Rate;
    Assert.IsTrue(totalSideDrain < -1, $"Side tanks should supply the engine, total drain={totalSideDrain}");

    Assert.AreEqual(0, coreTank.Tanks[0].Rate, 0.01, "Core tank should not drain while side tanks have fuel");

    // Symmetric side tanks (equal capacity, equal contents) must drain at
    // equal rates. A pure cost-min LP picks an arbitrary one of the
    // equally-cheap solutions; the fairness phase forces proportional
    // drain so neither side gets stranded with full tanks.
    Assert.AreEqual(sideTankL.Tanks[0].Rate, sideTankR.Tanks[0].Rate, 0.01,
      $"Symmetric side tanks must drain at equal rates: L={sideTankL.Tanks[0].Rate}, R={sideTankR.Tanks[0].Rate}");
  }

  [TestMethod]
  public void AsparagusStaging_CoreTankDrainsAfterSidesEmpty() {
    var partDefs = new[] {
      (1u, "pod", new uint[] { 2, 4, 6 }),
      (2u, "coreTank", new uint[] { 3 }),
      (3u, "engine", Array.Empty<uint>()),
      (4u, "decoupler", new uint[] { 5 }),
      (5u, "sideTank", Array.Empty<uint>()),
      (6u, "decoupler", new uint[] { 7 }),
      (7u, "sideTank", Array.Empty<uint>()),
    };

    var vessel = new VirtualVessel();
    vessel.AddPart(1, "pod", 0, new List<VirtualComponent>());
    vessel.AddPart(2, "coreTank", 0, new List<VirtualComponent> { MakeTank(100, "RP-1", 100) });
    vessel.AddPart(3, "engine", 0, new List<VirtualComponent> { MakeEngine(100, 300, ("RP-1", 1)) });
    vessel.AddPart(4, "decoupler", 0, new List<VirtualComponent> { MakeDecoupler(1, "RP-1") });
    vessel.AddPart(5, "sideTank", 0, new List<VirtualComponent> { MakeTank(100, "RP-1", 0, 0) });
    vessel.AddPart(6, "decoupler", 0, new List<VirtualComponent> { MakeDecoupler(1, "RP-1") });
    vessel.AddPart(7, "sideTank", 0, new List<VirtualComponent> { MakeTank(100, "RP-1", 0, 0) });
    vessel.UpdatePartTree(BuildParentMap(partDefs));
    vessel.InitializeSolver(0);

    var engine = vessel.AllComponents().OfType<Engine>().First();
    engine.Throttle = 1.0;
    vessel.Solve();

    Assert.AreEqual(1.0, engine.Satisfaction, 0.01, "Engine should be satisfied from core tank");

    var coreTank = vessel.AllComponents().OfType<TankVolume>()
      .First(t => vessel.GetComponents(2).Contains(t));

    Assert.IsTrue(coreTank.Tanks[0].Rate < -0.1, "Core tank should be draining when side tanks are empty");
  }

  [TestMethod]
  public void AsparagusStaging_ThreeKeroloxEngines_CenterFedByUpFlow() {
    // In-game repro: kerolox tanks (RP-1 + LOX in one TankVolume),
    // 3 engines (center + 2 sides), each with their own kerolox tank.
    // Up-only decouplers allow both fuels through. Center tank should
    // not drain while sides have fuel.
    var partDefs = new[] {
      (1u, "pod", new uint[] { 2, 4, 6 }),
      (2u, "coreTank", new uint[] { 3 }),
      (3u, "centerEngine", Array.Empty<uint>()),
      (4u, "decouplerL", new uint[] { 5 }),
      (5u, "sideTankL", new uint[] { 8 }),
      (6u, "decouplerR", new uint[] { 7 }),
      (7u, "sideTankR", new uint[] { 9 }),
      (8u, "sideEngineL", Array.Empty<uint>()),
      (9u, "sideEngineR", Array.Empty<uint>()),
    };

    var vessel = new VirtualVessel();
    vessel.AddPart(1, "pod", 0, new List<VirtualComponent>());
    vessel.AddPart(2, "coreTank", 0, new List<VirtualComponent> { MakeKeroloxTank(2000) });
    vessel.AddPart(3, "centerEngine", 0, new List<VirtualComponent> { MakeKeroloxEngine() });
    vessel.AddPart(4, "decouplerL", 0, new List<VirtualComponent> { MakeUpOnlyDecoupler(1, "RP-1", "Liquid Oxygen") });
    var sideTankLeft = MakeKeroloxTank(2000);
    sideTankLeft.Tanks[0].Contents = 657;  // RP-1
    sideTankLeft.Tanks[1].Contents = 986;  // LOX
    vessel.AddPart(5, "sideTankL", 0, new List<VirtualComponent> { sideTankLeft });
    vessel.AddPart(6, "decouplerR", 0, new List<VirtualComponent> { MakeUpOnlyDecoupler(1, "RP-1", "Liquid Oxygen") });
    var sideTankRight = MakeKeroloxTank(2000);
    sideTankRight.Tanks[0].Contents = 584;  // RP-1 (asymmetric)
    sideTankRight.Tanks[1].Contents = 876;  // LOX (asymmetric)
    vessel.AddPart(7, "sideTankR", 0, new List<VirtualComponent> { sideTankRight });
    vessel.AddPart(8, "sideEngineL", 0, new List<VirtualComponent> { MakeKeroloxEngine() });
    vessel.AddPart(9, "sideEngineR", 0, new List<VirtualComponent> { MakeKeroloxEngine() });
    vessel.UpdatePartTree(BuildParentMap(partDefs));
    vessel.InitializeSolver(0);

    var coreTank = vessel.AllComponents().OfType<TankVolume>()
      .First(t => vessel.GetComponents(2).Contains(t));
    var sideTankL = vessel.AllComponents().OfType<TankVolume>()
      .First(t => vessel.GetComponents(5).Contains(t));
    var sideTankR = vessel.AllComponents().OfType<TankVolume>()
      .First(t => vessel.GetComponents(7).Contains(t));
    var engines = vessel.AllComponents().OfType<Engine>().ToList();

    // Throttle sequence stresses warm-start. After each solve, check
    // the asparagus invariant.
    foreach (var throttle in new[] { 1.0, 0.4, 0.7, 0.2, 1.0, 0.5 }) {
      foreach (var e in engines) e.Throttle = throttle;
      vessel.Invalidate();
      vessel.Solve();

      foreach (var e in engines)
        Assert.AreEqual(1.0, e.Satisfaction, 0.01,
          $"throttle={throttle}: engine should be fully satisfied; got {e.Satisfaction}");

      foreach (var b in coreTank.Tanks)
        Assert.AreEqual(0, b.Rate, 0.01,
          $"throttle={throttle}: core {b.Resource.Name} should not drain; got {b.Rate}");

      // Asymmetric sides: drain rate should be proportional to current
      // contents (max-min fairness). drain/amount equal across pools.
      for (int i = 0; i < sideTankL.Tanks.Count; i++) {
        var bL = sideTankL.Tanks[i];
        var bR = sideTankR.Tanks[i];
        var ratioL = -bL.Rate / bL.Contents;
        var ratioR = -bR.Rate / bR.Contents;
        Assert.AreEqual(ratioL, ratioR, 1e-3,
          $"throttle={throttle}: {bL.Resource.Name} drain/amount must be equal across sides: L={bL.Rate}/{bL.Contents}={ratioL:E3}, R={bR.Rate}/{bR.Contents}={ratioR:E3}");
      }
    }
  }

  [TestMethod]
  public void AsparagusStaging_ThreeEngines_CenterFedByUpFlow() {
    // Three engines (center + 2 sides), each on its own tank node.
    // High-DP side tanks should drain (proportional to amount); the
    // single low-DP center tank should NOT drain — center engine is
    // fed by up-flow from the sides via the up-only decoupler edges.
    //
    // Solves repeatedly with varying throttle: GLOP's warm-start
    // re-uses the previous basis, and a basis-arbitrary pick from
    // an earlier solve can persist into a later one. The asparagus
    // invariant (core stays still, sides drain in lockstep) must
    // hold across all of these.
    var partDefs = new[] {
      (1u, "pod", new uint[] { 2, 4, 6 }),
      (2u, "coreTank", new uint[] { 3 }),
      (3u, "centerEngine", Array.Empty<uint>()),
      (4u, "decouplerL", new uint[] { 5 }),
      (5u, "sideTankL", new uint[] { 8 }),
      (6u, "decouplerR", new uint[] { 7 }),
      (7u, "sideTankR", new uint[] { 9 }),
      (8u, "sideEngineL", Array.Empty<uint>()),
      (9u, "sideEngineR", Array.Empty<uint>()),
    };

    var vessel = new VirtualVessel();
    vessel.AddPart(1, "pod", 0, new List<VirtualComponent>());
    vessel.AddPart(2, "coreTank", 0, new List<VirtualComponent> { MakeTank(100, "RP-1", 100) });
    vessel.AddPart(3, "centerEngine", 0, new List<VirtualComponent> { MakeEngine(100, 300, ("RP-1", 1)) });
    vessel.AddPart(4, "decouplerL", 0, new List<VirtualComponent> { MakeDecoupler(1, "RP-1") });
    vessel.AddPart(5, "sideTankL", 0, new List<VirtualComponent> { MakeTank(100, "RP-1", 100) });
    vessel.AddPart(6, "decouplerR", 0, new List<VirtualComponent> { MakeDecoupler(1, "RP-1") });
    vessel.AddPart(7, "sideTankR", 0, new List<VirtualComponent> { MakeTank(100, "RP-1", 100) });
    vessel.AddPart(8, "sideEngineL", 0, new List<VirtualComponent> { MakeEngine(100, 300, ("RP-1", 1)) });
    vessel.AddPart(9, "sideEngineR", 0, new List<VirtualComponent> { MakeEngine(100, 300, ("RP-1", 1)) });
    vessel.UpdatePartTree(BuildParentMap(partDefs));
    vessel.InitializeSolver(0);

    var coreTank = vessel.AllComponents().OfType<TankVolume>()
      .First(t => vessel.GetComponents(2).Contains(t));
    var sideTankL = vessel.AllComponents().OfType<TankVolume>()
      .First(t => vessel.GetComponents(5).Contains(t));
    var sideTankR = vessel.AllComponents().OfType<TankVolume>()
      .First(t => vessel.GetComponents(7).Contains(t));
    var engines = vessel.AllComponents().OfType<Engine>().ToList();

    // Throttle sequence stresses warm-start. After each solve, check
    // the asparagus invariant.
    foreach (var throttle in new[] { 1.0, 0.4, 0.7, 0.2, 1.0, 0.5 }) {
      foreach (var e in engines) e.Throttle = throttle;
      vessel.Invalidate();
      vessel.Solve();

      foreach (var e in engines)
        Assert.AreEqual(1.0, e.Satisfaction, 0.01,
          $"throttle={throttle}: engine should be fully satisfied; got {e.Satisfaction}");

      Assert.AreEqual(0, coreTank.Tanks[0].Rate, 0.01,
        $"throttle={throttle}: core tank should not drain while sides have fuel; got {coreTank.Tanks[0].Rate}");

      Assert.AreEqual(sideTankL.Tanks[0].Rate, sideTankR.Tanks[0].Rate, 0.01,
        $"throttle={throttle}: symmetric side tanks should drain equally: L={sideTankL.Tanks[0].Rate}, R={sideTankR.Tanks[0].Rate}");
    }
  }

  [TestMethod]
  public void NonHgsPartsAreTransparent() {
    var partDefs = new[] {
      (1u, "pod", new uint[] { 2 }),
      (2u, "truss", new uint[] { 3 }),
      (3u, "truss", new uint[] { 4 }),
      (4u, "tank", new uint[] { 5 }),
      (5u, "engine", Array.Empty<uint>()),
    };

    var vessel = new VirtualVessel();
    vessel.AddPart(1, "pod", 0, new List<VirtualComponent>());
    vessel.AddPart(4, "tank", 0, new List<VirtualComponent> { MakeTank(100, "RP-1", 100) });
    vessel.AddPart(5, "engine", 0, new List<VirtualComponent> { MakeEngine(100, 300, ("RP-1", 1)) });
    vessel.UpdatePartTree(BuildParentMap(partDefs));
    vessel.InitializeSolver(0);

    var engine = vessel.AllComponents().OfType<Engine>().First();
    engine.Throttle = 1.0;
    vessel.Solve();

    Assert.IsTrue(engine.Satisfaction > 0.5, "Engine should get fuel through transparent non-Nova parts");
  }
}
