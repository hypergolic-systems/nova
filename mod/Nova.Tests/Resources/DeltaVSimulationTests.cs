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

namespace Nova.Tests.Resources;

[TestClass]
public class DeltaVSimulationTests {

  private static Engine MakeEngine(double thrust, double isp, params (Resource resource, double ratio)[] propellants) {
    var engine = new Engine();
    engine.Initialize(thrust, isp, propellants.Select(p => (p.resource, p.ratio)).ToList());
    return engine;
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
        }
      }
    };
  }

  private static Decoupler MakeDecoupler(int priority, params (Resource resource, string direction)[] allowedResources) {
    var dec = new Decoupler { Priority = priority };
    foreach (var (res, dir) in allowedResources) {
      dec.AllowedResources.Add(res);
      if (dir == "up")
        dec.UpOnlyResources.Add(res);
    }
    return dec;
  }

  /// <summary>
  /// Build a VirtualVessel from part definitions and a parent map.
  /// partDefs: (id, name, parentId or null for root, dryMass, components)
  /// </summary>
  private static VirtualVessel BuildVessel(
      (uint id, string name, uint? parentId, double dryMass, List<VirtualComponent> components)[] parts) {
    var vv = new VirtualVessel();
    var parentMap = new Dictionary<uint, uint?>();
    foreach (var (id, name, parentId, dryMass, components) in parts) {
      vv.AddPart(id, name, dryMass, components);
      parentMap[id] = parentId;
    }
    vv.UpdatePartTree(parentMap);
    vv.InitializeSolver(0);
    return vv;
  }

  private const double G0 = 9.80665;

  [TestMethod]
  public void UpOnlyEdge_BlocksReverseFlow() {
    // Parent has tank, child has engine. Edge is up-only.
    // Engine on child should NOT be able to pull fuel from parent.
    var vessel = BuildVessel(new[] {
      (1u, "pod", (uint?)null, 5.0, new List<VirtualComponent>()),
      (2u, "tank", (uint?)1u, 5.0, new List<VirtualComponent> { MakeTank(Resource.RP1, 100) }),
      (3u, "decoupler", (uint?)1u, 0.0, new List<VirtualComponent> { MakeDecoupler(1, (Resource.RP1, "up")) }),
      (4u, "engine", (uint?)3u, 5.0, new List<VirtualComponent> { MakeEngine(10, 300, (Resource.RP1, 1)) }),
    });

    var engine = vessel.AllComponents().OfType<Engine>().First();
    engine.Throttle = 1.0;
    vessel.Invalidate();
    vessel.Tick(0);

    Assert.AreEqual(0, engine.Satisfaction, 0.01,
      "Engine below up-only decoupler should be starved — fuel can't flow down");
  }

  [TestMethod]
  public void SingleStage_SingleEngine() {
    // One tank (100L RP-1, density=0.8 → 80 kg fuel) + one engine.
    // Dry mass = 10 kg (pod) + 5 kg (tank) + 5 kg (engine) = 20 kg.
    // Wet mass = 20 + 80 = 100 kg.
    // Expected Δv = Isp * g0 * ln(100 / 20)
    var vessel = BuildVessel(new[] {
      (1u, "pod", (uint?)null, 10.0, new List<VirtualComponent>()),
      (2u, "tank", (uint?)1u, 5.0, new List<VirtualComponent> { MakeTank(Resource.RP1, 100) }),
      (3u, "engine", (uint?)2u, 5.0, new List<VirtualComponent> { MakeEngine(10, 300, (Resource.RP1, 1)) }),
    });

    var stages = new List<DeltaVSimulation.StageDefinition> {
      new() { InverseStageIndex = 0, EnginePartIds = new() { 3 } },
    };
    var results = DeltaVSimulation.Run(vessel, stages);

    Assert.AreEqual(1, results.Count, "Should have one stage");

    var expected = 300 * G0 * Math.Log(100.0 / 20.0);
    Assert.AreEqual(expected, results[0].DeltaV, expected * 0.01,
      $"Delta-v should be ~{expected:F0} m/s");
    Assert.AreEqual(100, results[0].StartMass, 1, "Start mass should be ~100 kg");
    Assert.AreEqual(20, results[0].EndMass, 1, "End mass should be ~20 kg");
    Assert.AreEqual(300, results[0].Isp, 1, "Isp should be 300s");
  }

  [TestMethod]
  public void TwoStages_Serial() {
    var vessel = BuildVessel(new[] {
      (1u, "pod", (uint?)null, 5.0, new List<VirtualComponent>()),
      (2u, "upperTank", (uint?)1u, 5.0, new List<VirtualComponent> { MakeTank(Resource.RP1, 50) }),
      (3u, "upperEngine", (uint?)2u, 5.0, new List<VirtualComponent> { MakeEngine(10, 300, (Resource.RP1, 1)) }),
      (4u, "decoupler", (uint?)1u, 0.0, new List<VirtualComponent> { MakeDecoupler(1, (Resource.RP1, "up")) }),
      (5u, "lowerTank", (uint?)4u, 5.0, new List<VirtualComponent> { MakeTank(Resource.RP1, 100) }),
      (6u, "lowerEngine", (uint?)5u, 5.0, new List<VirtualComponent> { MakeEngine(10, 300, (Resource.RP1, 1)) }),
    });

    var stages = new List<DeltaVSimulation.StageDefinition> {
      // Stage 2 (fires first): both engines ignite
      new() { InverseStageIndex = 2, EnginePartIds = new() { 3, 6 } },
      // Stage 1: jettison lower stage after crossfeed drains it
      new() { InverseStageIndex = 1, DecouplerPartIds = new() { 4 } },
      // Stage 0: upper engine continues on upper tank
      new() { InverseStageIndex = 0 },
    };
    var results = DeltaVSimulation.Run(vessel, stages);

    Assert.IsTrue(results.Count >= 2, $"Should have at least 2 stages, got {results.Count}");

    // Stage 2: both engines drain lower tank via crossfeed (80 kg fuel).
    // Wet=145, after lower fuel gone=65 (still have 40 kg upper fuel).
    var boosterExpected = 300 * G0 * Math.Log(145.0 / 65.0);
    var boosterResult = results.First(r => r.InverseStageIndex == 2);
    Assert.AreEqual(boosterExpected, boosterResult.DeltaV, boosterExpected * 0.05,
      $"Booster Δv should be ~{boosterExpected:F0} m/s");

    // Stage 1: jettison lower (10 kg dry), upper engine burns upper tank (40 kg fuel).
    // Wet=55, dry=15.
    var upperExpected = 300 * G0 * Math.Log(55.0 / 15.0);
    var upperResult = results.First(r => r.InverseStageIndex == 1);
    Assert.AreEqual(upperExpected, upperResult.DeltaV, upperExpected * 0.05,
      $"Upper Δv should be ~{upperExpected:F0} m/s");
  }

  [TestMethod]
  public void Asparagus_CrossfeedThenJettison() {
    var vessel = BuildVessel(new[] {
      (1u, "pod", (uint?)null, 5.0, new List<VirtualComponent>()),
      (2u, "centerTank", (uint?)1u, 5.0, new List<VirtualComponent> { MakeTank(Resource.RP1, 100) }),
      (3u, "centerEngine", (uint?)2u, 5.0, new List<VirtualComponent> { MakeEngine(10, 300, (Resource.RP1, 1)) }),
      // Left booster
      (4u, "decoupler", (uint?)1u, 0.0, new List<VirtualComponent> { MakeDecoupler(1, (Resource.RP1, "up")) }),
      (5u, "sideTank", (uint?)4u, 2.0, new List<VirtualComponent> { MakeTank(Resource.RP1, 50) }),
      (6u, "sideEngine", (uint?)5u, 3.0, new List<VirtualComponent> { MakeEngine(10, 300, (Resource.RP1, 1)) }),
      // Right booster
      (7u, "decoupler", (uint?)1u, 0.0, new List<VirtualComponent> { MakeDecoupler(1, (Resource.RP1, "up")) }),
      (8u, "sideTank", (uint?)7u, 2.0, new List<VirtualComponent> { MakeTank(Resource.RP1, 50) }),
      (9u, "sideEngine", (uint?)8u, 3.0, new List<VirtualComponent> { MakeEngine(10, 300, (Resource.RP1, 1)) }),
    });

    var stages = new List<DeltaVSimulation.StageDefinition> {
      // Stage 2: all 3 engines ignite
      new() { InverseStageIndex = 2, EnginePartIds = new() { 3, 6, 9 } },
      // Stage 1: jettison side boosters
      new() { InverseStageIndex = 1, DecouplerPartIds = new() { 4, 7 } },
      // Stage 0: center engine continues
      new() { InverseStageIndex = 0 },
    };
    var results = DeltaVSimulation.Run(vessel, stages);

    Assert.IsTrue(results.Count >= 2, $"Should have at least 2 stages, got {results.Count}");

    var totalDv = results.Sum(r => r.DeltaV);
    Assert.IsTrue(totalDv > 1000, $"Total Δv should be substantial, got {totalDv:F0} m/s");

    // Booster stage (2) and core stage should both contribute delta-v.
    var boosterDv = results.Where(r => r.InverseStageIndex == 2).Sum(r => r.DeltaV);
    var coreDv = results.Where(r => r.InverseStageIndex >= 0 && r.InverseStageIndex < 2).Sum(r => r.DeltaV);
    Assert.IsTrue(boosterDv > 0 && coreDv > 0,
      $"Both phases should contribute delta-v. Booster={boosterDv:F0}, Core={coreDv:F0}");
  }

  [TestMethod]
  public void Run_WithExplicitTime_MatchesDefaultOverload() {
    var vessel = BuildVessel(new[] {
      (1u, "pod", (uint?)null, 10.0, new List<VirtualComponent>()),
      (2u, "tank", (uint?)1u, 5.0, new List<VirtualComponent> { MakeTank(Resource.RP1, 100) }),
      (3u, "engine", (uint?)2u, 5.0, new List<VirtualComponent> { MakeEngine(10, 300, (Resource.RP1, 1)) }),
    });

    var stages = new List<DeltaVSimulation.StageDefinition> {
      new() { InverseStageIndex = 0, EnginePartIds = new() { 3 } },
    };

    var defaultResults = DeltaVSimulation.Run(vessel, stages);
    var timedResults = DeltaVSimulation.Run(vessel, stages, 12345.0);

    Assert.AreEqual(defaultResults.Count, timedResults.Count, "Stage count should match");
    Assert.AreEqual(defaultResults[0].DeltaV, timedResults[0].DeltaV, 0.01,
      "Delta-v should be identical regardless of simulation time parameter");
    Assert.AreEqual(defaultResults[0].StartMass, timedResults[0].StartMass, 0.01);
    Assert.AreEqual(defaultResults[0].EndMass, timedResults[0].EndMass, 0.01);
  }

  [TestMethod]
  public void BatteryCrossfeed_DoesNotPreventJettison() {
    var vessel = BuildVessel(new[] {
      (1u, "pod", (uint?)null, 5.0, new List<VirtualComponent>()),
      (2u, "centerTank", (uint?)1u, 5.0, new List<VirtualComponent> { MakeTank(Resource.RP1, 100) }),
      (10u, "centerEngine", (uint?)2u, 5.0, new List<VirtualComponent> { MakeEngine(10, 300, (Resource.RP1, 1)) }),
      (3u, "light", (uint?)1u, 0.0, new List<VirtualComponent> { new Light { Rate = 5 } }),
      // Side booster with battery
      (4u, "decoupler", (uint?)1u, 0.0, new List<VirtualComponent> {
        MakeDecoupler(1, (Resource.RP1, "up"), (Resource.ElectricCharge, null))
      }),
      (5u, "sideTank", (uint?)4u, 2.0, new List<VirtualComponent> { MakeTank(Resource.RP1, 50) }),
      (6u, "sideEngine", (uint?)5u, 3.0, new List<VirtualComponent> { MakeEngine(10, 300, (Resource.RP1, 1)) }),
      (7u, "battery", (uint?)4u, 1.0, new List<VirtualComponent> {
        new Battery { Buffer = new Buffer {
          Resource = Resource.ElectricCharge, Capacity = 1000, Contents = 1000,
          MaxRateIn = 10, MaxRateOut = 10,
        }}
      }),
    });

    var stages = new List<DeltaVSimulation.StageDefinition> {
      // Stage 2: both engines ignite
      new() { InverseStageIndex = 2, EnginePartIds = new() { 10, 6 } },
      // Stage 1: jettison side booster (despite battery still crossfeeding EC)
      new() { InverseStageIndex = 1, DecouplerPartIds = new() { 4 } },
      // Stage 0: center engine continues
      new() { InverseStageIndex = 0 },
    };
    var results = DeltaVSimulation.Run(vessel, stages);

    Assert.IsTrue(results.Count >= 2,
      $"Should have at least 2 stages (side jettison despite battery crossfeed), got {results.Count}");
  }

  // Regression: in-flight stage activation triggers
  // ExtractParts → RebuildTopology on the remaining VirtualVessel.
  // After that rebuild, DeltaVSimulation must still produce a valid
  // result for whatever stages remain — otherwise the dV display
  // empties out the moment the player presses space.
  [TestMethod]
  public void DvSurvivesPostStageRebuild() {
    // 2-stage rocket: pod + upper-tank + upper-engine on one branch,
    // decoupler + lower-tank + lower-engine on the booster branch.
    var vessel = BuildVessel(new[] {
      (1u, "pod", (uint?)null, 5.0, new List<VirtualComponent>()),
      (2u, "upperTank", (uint?)1u, 5.0, new List<VirtualComponent> { MakeTank(Resource.RP1, 50) }),
      (3u, "upperEngine", (uint?)2u, 5.0, new List<VirtualComponent> { MakeEngine(10, 300, (Resource.RP1, 1)) }),
      (4u, "decoupler", (uint?)1u, 0.0, new List<VirtualComponent> { MakeDecoupler(1, (Resource.RP1, "up")) }),
      (5u, "lowerTank", (uint?)4u, 5.0, new List<VirtualComponent> { MakeTank(Resource.RP1, 100) }),
      (6u, "lowerEngine", (uint?)5u, 5.0, new List<VirtualComponent> { MakeEngine(10, 300, (Resource.RP1, 1)) }),
    });

    // Simulate the in-flight stage-1 firing: KSP separates the booster
    // (decoupler + lower tank + lower engine) into a new vessel. Nova's
    // HandleVesselSplit calls ExtractParts then RebuildTopology.
    var boosterIds = new HashSet<uint> { 4u, 5u, 6u };
    vessel.ExtractParts(boosterIds);
    var newParentMap = new Dictionary<uint, uint?> {
      { 1u, null }, { 2u, 1u }, { 3u, 2u },
    };
    vessel.UpdatePartTree(newParentMap);
    vessel.InitializeSolver(0);

    // Remaining stages: only Stage 0 (the upper).
    var stages = new List<DeltaVSimulation.StageDefinition> {
      new() { InverseStageIndex = 0, EnginePartIds = new() { 3 } },
    };
    var results = DeltaVSimulation.Run(vessel, stages);

    Assert.AreEqual(1, results.Count,
        "Stage 0 should still produce a dV result post-rebuild");
    Assert.IsTrue(results[0].DeltaV > 0,
        $"Stage 0 dV should be positive, got {results[0].DeltaV}");
    Assert.AreEqual(0, results[0].InverseStageIndex);
  }
}
