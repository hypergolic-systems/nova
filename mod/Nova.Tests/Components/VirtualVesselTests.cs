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
    engine.Initialize(thrust, isp, 0,
      propellants.Select(p => (Resource.Get(p.resource), p.ratio)).ToList());
    return engine;
  }

  private static TankVolume MakeTank(double volume, string resource, double capacity, double contents = -1) {
    if (contents < 0) contents = capacity;
    return new TankVolume {
      Volume = volume,
      Tanks = { new Buffer {
        Resource = Resource.Get(resource),
        Capacity = capacity,
        Contents = contents,
        MaxRateIn = double.PositiveInfinity,
        MaxRateOut = double.PositiveInfinity,
      }}
    };
  }

  private static Decoupler MakeDecoupler(int priority = 1, params string[] allowedResources) {
    var d = new Decoupler { Priority = priority };
    foreach (var r in allowedResources)
      d.AllowedResources.Add(Resource.Get(r));
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
