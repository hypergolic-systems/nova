using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nova.Core;
using Nova.Core.Components;
using Nova.Core.Components.Propulsion;
using Nova.Core.Components.Structural;
using Nova.Core.Resources;
using Nova.Core.Flight;
using Nova.Core.Utils;
using Buffer = Nova.Core.Resources.Buffer;

namespace Nova.Tests.Components;

[TestClass]
public class DockingPortTests {

  [TestMethod]
  public void OnSave_OnLoad_RoundTrip() {
    var original = new DockingPort {
      Priority = 2,
    };
    original.AllowedResources.Add(Resource.Get("RP-1"));
    original.AllowedResources.Add(Resource.ElectricCharge);
    original.UpOnlyResources.Add(Resource.Get("RP-1"));

    // Round-trip via Clone (adapter-free serialization path is gone)
    var loaded = (DockingPort)original.Clone();

    Assert.AreEqual(2, loaded.Priority);
    Assert.AreEqual(2, loaded.AllowedResources.Count);
    Assert.IsTrue(loaded.AllowedResources.Contains(Resource.Get("RP-1")));
    Assert.IsTrue(loaded.AllowedResources.Contains(Resource.ElectricCharge));
    Assert.AreEqual(1, loaded.UpOnlyResources.Count);
    Assert.IsTrue(loaded.UpOnlyResources.Contains(Resource.Get("RP-1")));
  }

  // --- Topology tests using VirtualVessel ---

  private static Dictionary<uint, uint?> BuildParentMap(
      (uint id, string partName, uint[] children)[] partDefs) {
    var map = new Dictionary<uint, uint?>();
    foreach (var (id, _, children) in partDefs) {
      if (!map.ContainsKey(id))
        map[id] = null;
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

  private static TankVolume MakeTank(double volume, string resource, double capacity) {
    return new TankVolume {
      Volume = volume,
      MaxRate = 10000,
      Tanks = { new Buffer {
        Resource = Resource.Get(resource),
        Capacity = capacity,
        Contents = capacity,
      }}
    };
  }

  [TestMethod]
  public void DockingPort_NoConfig_AllowsFuel() {
    // Tree: pod(1) → tank(2) → dockingPort(3) → engine(4)
    // No AllowedResources config → all fuel flows through
    var partDefs = new[] {
      (1u, "pod", new uint[] { 2 }),
      (2u, "tank", new uint[] { 3 }),
      (3u, "dockingPort", new uint[] { 4 }),
      (4u, "engine", Array.Empty<uint>()),
    };

    var vessel = new VirtualVessel();
    vessel.AddPart(1, "pod", 0, new List<VirtualComponent>());
    vessel.AddPart(2, "tank", 0, new List<VirtualComponent> { MakeTank(100, "RP-1", 100) });
    vessel.AddPart(3, "dockingPort", 0, new List<VirtualComponent> { new DockingPort() });
    vessel.AddPart(4, "engine", 0, new List<VirtualComponent> { MakeEngine(100, 300, ("RP-1", 1)) });
    vessel.UpdatePartTree(BuildParentMap(partDefs));
    vessel.InitializeSolver(0);

    var engine = vessel.AllComponents().OfType<Engine>().First();
    engine.Throttle = 1.0;
    vessel.Solve();

    Assert.IsTrue(engine.Satisfaction > 0.99,
      "Engine should be fed — docking port allows all fuel through");
  }

  [TestMethod]
  public void MergeParts_InjectsIntoVessel() {
    // Build a minimal vessel, then merge additional parts into it
    var partDefs = new[] {
      (1u, "pod", new uint[] { 2 }),
      (2u, "tank", Array.Empty<uint>()),
    };

    var vessel = new VirtualVessel();
    vessel.AddPart(1, "pod", 0, new List<VirtualComponent>());
    vessel.AddPart(2, "tank", 0, new List<VirtualComponent> { MakeTank(100, "RP-1", 100) });
    vessel.UpdatePartTree(BuildParentMap(partDefs));
    vessel.InitializeSolver(0);

    Assert.AreEqual(1, vessel.AllComponents().Count(), "Should have 1 component (tank)");

    // Create components to merge (simulate docking)
    var engineCmp = MakeEngine(100, 300, ("RP-1", 1));
    var otherParts = new Dictionary<uint, List<VirtualComponent>> {
      [3] = new() { engineCmp },
    };
    var partNames = new Dictionary<uint, string> { [3] = "engine" };
    var dryMasses = new Dictionary<uint, double> { [3] = 500 };

    vessel.MergeParts(otherParts, partNames, dryMasses);

    // After merge, the engine component should be present
    Assert.AreEqual(2, vessel.AllComponents().Count(), "Should have 2 components after merge");
    Assert.AreEqual(1, vessel.AllComponents().OfType<Engine>().Count());
  }
}
