using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nova.Core;
using Nova.Core.Components;
using Nova.Core.Components.Propulsion;
using Nova.Core.Resources;
using Nova.Core.Flight;
using Nova.Core.Utils;
using Buffer = Nova.Core.Resources.Buffer;

namespace Nova.Tests.Components;

[TestClass]
public class RcsTests {

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

  private static Rcs MakeRcs(double thrusterPower, double isp, params (string resource, double ratio)[] propellants) {
    var rcs = new Rcs();
    rcs.Initialize(thrusterPower, isp,
      propellants.Select(p => (Resource.Get(p.resource), p.ratio)).ToList());
    return rcs;
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
  public void RcsConsumesHydrazine() {
    var partDefs = new[] {
      (1u, "pod", new uint[] { 2, 3 }),
      (2u, "tank", Array.Empty<uint>()),
      (3u, "rcs", Array.Empty<uint>()),
    };

    var vessel = new VirtualVessel();
    vessel.AddPart(1, "pod", 0, new List<VirtualComponent>());
    vessel.AddPart(2, "tank", 0, new List<VirtualComponent> { MakeTank(100, "Hydrazine", 100) });
    var rcs = MakeRcs(1, 220, ("Hydrazine", 1));
    rcs.ThrusterCount = 4;
    rcs.Throttle = 1.0;
    vessel.AddPart(3, "rcs", 0, new List<VirtualComponent> { rcs });
    vessel.UpdatePartTree(BuildParentMap(partDefs));
    vessel.InitializeSolver(0);
    vessel.Solve();

    Assert.IsTrue(rcs.Satisfaction > 0.99,
      $"Expected full satisfaction with tank available, got {rcs.Satisfaction}");
  }

  [TestMethod]
  public void RcsStarvesWithoutFuel() {
    var partDefs = new[] {
      (1u, "pod", new uint[] { 2 }),
      (2u, "rcs", Array.Empty<uint>()),
    };

    var vessel = new VirtualVessel();
    vessel.AddPart(1, "pod", 0, new List<VirtualComponent>());
    var rcs = MakeRcs(1, 220, ("Hydrazine", 1));
    rcs.ThrusterCount = 4;
    rcs.Throttle = 1.0;
    vessel.AddPart(2, "rcs", 0, new List<VirtualComponent> { rcs });
    vessel.UpdatePartTree(BuildParentMap(partDefs));
    vessel.InitializeSolver(0);
    vessel.Solve();

    Assert.AreEqual(0, rcs.Satisfaction, 0.01,
      "RCS should have zero satisfaction without fuel");
  }

  [TestMethod]
  public void RcsThrottleZeroNoDemand() {
    var partDefs = new[] {
      (1u, "pod", new uint[] { 2, 3 }),
      (2u, "tank", Array.Empty<uint>()),
      (3u, "rcs", Array.Empty<uint>()),
    };

    var vessel = new VirtualVessel();
    vessel.AddPart(1, "pod", 0, new List<VirtualComponent>());
    vessel.AddPart(2, "tank", 0, new List<VirtualComponent> { MakeTank(100, "Hydrazine", 100) });
    var rcs = MakeRcs(1, 220, ("Hydrazine", 1));
    rcs.ThrusterCount = 4;
    rcs.Throttle = 0;
    vessel.AddPart(3, "rcs", 0, new List<VirtualComponent> { rcs });
    vessel.UpdatePartTree(BuildParentMap(partDefs));
    vessel.InitializeSolver(0);
    vessel.Solve();

    Assert.AreEqual(0, rcs.NormalizedOutput, 1e-9,
      "Output should be zero when throttle is zero");
  }

  [TestMethod]
  public void RcsCloneRoundTrip() {
    var rcs = MakeRcs(2.5, 230, ("Hydrazine", 1));

    var loaded = (Rcs)rcs.Clone();

    Assert.AreEqual(2.5, loaded.ThrusterPower, 1e-9);
    Assert.AreEqual(230, loaded.Isp, 1e-9);
    Assert.AreEqual(1, loaded.Propellants.Count);
    Assert.AreEqual("Hydrazine", loaded.Propellants[0].Resource.Name);
    Assert.AreEqual(1, loaded.Propellants[0].Ratio, 1e-9);
  }
}
