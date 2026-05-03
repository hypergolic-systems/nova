using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nova.Core.Resources;
using Nova.Core.Systems;

namespace Nova.Tests.Systems;

[TestClass]
public class StagingFlowSystemTests {

  // ── Sanity ───────────────────────────────────────────────────────

  [TestMethod]
  public void SingleTank_SingleConsumer_FullSatisfaction() {
    var sys = new StagingFlowSystem();
    var node = sys.AddNode();
    var tank = node.AddBuffer(Resource.RP1, 1000);
    tank.FlowLimits(0, 100);
    tank.Contents = 1000;

    var d = sys.RegisterDemand(node, Resource.RP1, 30);
    sys.Solve();

    Assert.AreEqual(1.0, d.Activity, 0.001);
    Assert.AreEqual(-30, tank.Rate, 0.001);
  }

  [TestMethod]
  public void EmptyTank_DemandUnsatisfied() {
    var sys = new StagingFlowSystem();
    var node = sys.AddNode();
    var tank = node.AddBuffer(Resource.RP1, 1000);
    tank.FlowLimits(0, 100);
    tank.Contents = 0;

    var d = sys.RegisterDemand(node, Resource.RP1, 30);
    sys.Solve();

    Assert.AreEqual(0.0, d.Activity, 0.001);
    Assert.AreEqual(0, tank.Rate, 0.001);
  }

  [TestMethod]
  public void RateCapped_BufferLimitedToMaxRateOut() {
    var sys = new StagingFlowSystem();
    var node = sys.AddNode();
    var tank = node.AddBuffer(Resource.RP1, 1000);
    tank.FlowLimits(0, 20);
    tank.Contents = 1000;

    // Demand 30, but tank only delivers up to 20.
    var d = sys.RegisterDemand(node, Resource.RP1, 30);
    sys.Solve();

    Assert.AreEqual(20.0 / 30.0, d.Activity, 0.001);
    Assert.AreEqual(-20, tank.Rate, 0.001);
  }

  // ── Asparagus ───────────────────────────────────────────────────

  [TestMethod]
  public void Asparagus_SymmetricSides_DrainEqually_CoreUntouched() {
    // Two side tanks (high DP) and a core tank (low DP), each at the
    // same node-of-engines that consumes RP-1. Side tanks share a DP
    // → drain proportionally; core stays at zero.
    var sys = new StagingFlowSystem();
    var sideL = sys.AddNode(); sideL.DrainPriority = 2;
    var sideR = sys.AddNode(); sideR.DrainPriority = 2;
    var core  = sys.AddNode(); core.DrainPriority  = 1;
    var engineNode = sys.AddNode();

    foreach (var n in new[] { sideL, sideR, core }) {
      var t = n.AddBuffer(Resource.RP1, 1000);
      t.FlowLimits(0, 1000);
      t.Contents = 1000;
    }
    sys.AddEdge(sideL, engineNode);
    sys.AddEdge(sideR, engineNode);
    sys.AddEdge(core,  engineNode);

    var d = sys.RegisterDemand(engineNode, Resource.RP1, 60);
    sys.Solve();

    Assert.AreEqual(1.0, d.Activity, 0.001);
    Assert.AreEqual(-30, sideL.Buffers[0].Rate, 0.001);
    Assert.AreEqual(-30, sideR.Buffers[0].Rate, 0.001);
    Assert.AreEqual(0,   core.Buffers[0].Rate,  0.001);
  }

  [TestMethod]
  public void Asparagus_AsymmetricSides_DrainProportional() {
    // Side tanks have 657 / 584 contents at the same DP. They should
    // drain proportional to their amounts so they empty in lockstep.
    var sys = new StagingFlowSystem();
    var sideL = sys.AddNode(); sideL.DrainPriority = 2;
    var sideR = sys.AddNode(); sideR.DrainPriority = 2;
    var engineNode = sys.AddNode();

    var tL = sideL.AddBuffer(Resource.RP1, 1000); tL.FlowLimits(0, 1000); tL.Contents = 657;
    var tR = sideR.AddBuffer(Resource.RP1, 1000); tR.FlowLimits(0, 1000); tR.Contents = 584;

    sys.AddEdge(sideL, engineNode);
    sys.AddEdge(sideR, engineNode);

    var d = sys.RegisterDemand(engineNode, Resource.RP1, 100);
    sys.Solve();

    Assert.AreEqual(1.0, d.Activity, 0.001);
    var totalAmount = 657.0 + 584.0;
    Assert.AreEqual(-100 * 657 / totalAmount, tL.Rate, 0.01);
    Assert.AreEqual(-100 * 584 / totalAmount, tR.Rate, 0.01);
    // After 1s of integration the ratio remains the same.
    sys.Tick(1.0);
    Assert.AreEqual(657 / 584.0, tL.Contents / tR.Contents, 0.001);
  }

  [TestMethod]
  public void Asparagus_StagedDrop_HighDpFirstThenLowDp() {
    // Two stages of side tanks. After drop, demand remaining draws
    // from the next-stage sides at lower DP.
    var sys = new StagingFlowSystem();
    var outer = sys.AddNode(); outer.DrainPriority = 3;
    var inner = sys.AddNode(); inner.DrainPriority = 2;
    var engineNode = sys.AddNode();

    var tOuter = outer.AddBuffer(Resource.RP1, 1000);
    tOuter.FlowLimits(0, 1000); tOuter.Contents = 1000;
    var tInner = inner.AddBuffer(Resource.RP1, 1000);
    tInner.FlowLimits(0, 1000); tInner.Contents = 1000;

    sys.AddEdge(outer, engineNode);
    sys.AddEdge(inner, engineNode);

    var d = sys.RegisterDemand(engineNode, Resource.RP1, 60);
    sys.Solve();

    Assert.AreEqual(1.0, d.Activity, 0.001);
    Assert.AreEqual(-60, tOuter.Rate, 0.001);
    Assert.AreEqual(0,   tInner.Rate, 0.001);
  }

  [TestMethod]
  public void HigherDpExhausted_LowDpSuppliesResidual() {
    // High-DP tank can supply 20; demand is 50. Low-DP tank covers the
    // remaining 30.
    var sys = new StagingFlowSystem();
    var hi = sys.AddNode(); hi.DrainPriority = 2;
    var lo = sys.AddNode(); lo.DrainPriority = 1;
    var engineNode = sys.AddNode();

    var tHi = hi.AddBuffer(Resource.RP1, 1000); tHi.FlowLimits(0, 20); tHi.Contents = 1000;
    var tLo = lo.AddBuffer(Resource.RP1, 1000); tLo.FlowLimits(0, 1000); tLo.Contents = 1000;

    sys.AddEdge(hi, engineNode);
    sys.AddEdge(lo, engineNode);

    var d = sys.RegisterDemand(engineNode, Resource.RP1, 50);
    sys.Solve();

    Assert.AreEqual(1.0, d.Activity, 0.001);
    Assert.AreEqual(-20, tHi.Rate, 0.001);
    Assert.AreEqual(-30, tLo.Rate, 0.001);
  }

  // ── Connectivity ────────────────────────────────────────────────

  [TestMethod]
  public void DisconnectedSubNetworks_FairIndependently() {
    // Two completely-disjoint networks share the resource. Each fair-
    // shares within itself; no cross-flow between the two.
    var sys = new StagingFlowSystem();

    // Net A: 1 tank, 1 engine.
    var tankA = sys.AddNode();
    var engA = sys.AddNode();
    var bA = tankA.AddBuffer(Resource.RP1, 500);
    bA.FlowLimits(0, 1000); bA.Contents = 500;
    sys.AddEdge(tankA, engA);
    var dA = sys.RegisterDemand(engA, Resource.RP1, 30);

    // Net B: 1 tank, 1 engine. Different RP-1 stash.
    var tankB = sys.AddNode();
    var engB = sys.AddNode();
    var bB = tankB.AddBuffer(Resource.RP1, 700);
    bB.FlowLimits(0, 1000); bB.Contents = 700;
    sys.AddEdge(tankB, engB);
    var dB = sys.RegisterDemand(engB, Resource.RP1, 50);

    sys.Solve();

    Assert.AreEqual(1.0, dA.Activity, 0.001);
    Assert.AreEqual(1.0, dB.Activity, 0.001);
    Assert.AreEqual(-30, bA.Rate, 0.001);
    Assert.AreEqual(-50, bB.Rate, 0.001);
  }

  [TestMethod]
  public void EdgeBlocksResource_ConsumerStarves() {
    // Decoupler analog: edge has AllowedResources = {LOX} only. RP-1
    // consumer on the far side sees nothing.
    var sys = new StagingFlowSystem();
    var tank = sys.AddNode();
    var engineNode = sys.AddNode();
    var t = tank.AddBuffer(Resource.RP1, 1000);
    t.FlowLimits(0, 1000); t.Contents = 1000;

    sys.AddEdge(tank, engineNode, allowedResources: new HashSet<Resource> { Resource.LiquidOxygen });

    var d = sys.RegisterDemand(engineNode, Resource.RP1, 30);
    sys.Solve();

    Assert.AreEqual(0.0, d.Activity, 0.001);
    Assert.AreEqual(0, t.Rate, 0.001);
  }

  [TestMethod]
  public void EdgeAllowsResource_FlowGoesThrough() {
    var sys = new StagingFlowSystem();
    var tank = sys.AddNode();
    var engineNode = sys.AddNode();
    var t = tank.AddBuffer(Resource.RP1, 1000);
    t.FlowLimits(0, 1000); t.Contents = 1000;

    sys.AddEdge(tank, engineNode,
        allowedResources: new HashSet<Resource> { Resource.RP1, Resource.LiquidOxygen });

    var d = sys.RegisterDemand(engineNode, Resource.RP1, 30);
    sys.Solve();

    Assert.AreEqual(1.0, d.Activity, 0.001);
    Assert.AreEqual(-30, t.Rate, 0.001);
  }

  [TestMethod]
  public void UpOnlyEdge_ChildCannotPullFromParent() {
    // Up-only resource flows ONLY child → parent (i.e. up toward root).
    // Parent→child is blocked. A consumer at the child cannot pull
    // from the parent's tank.
    var sys = new StagingFlowSystem();
    var parent = sys.AddNode();
    var child = sys.AddNode();
    var t = parent.AddBuffer(Resource.RP1, 1000);
    t.FlowLimits(0, 1000); t.Contents = 1000;

    sys.AddEdge(parent, child, upOnlyResources: new HashSet<Resource> { Resource.RP1 });

    var d = sys.RegisterDemand(child, Resource.RP1, 30);
    sys.Solve();

    Assert.AreEqual(0.0, d.Activity, 0.001);
    Assert.AreEqual(0, t.Rate, 0.001);
  }

  [TestMethod]
  public void UpOnlyEdge_ParentCanPullFromChild() {
    // Tank at child, demand at parent. Up-only allows child→parent
    // flow → parent's consumer reaches child's tank.
    var sys = new StagingFlowSystem();
    var parent = sys.AddNode();
    var child = sys.AddNode();
    var t = child.AddBuffer(Resource.RP1, 1000);
    t.FlowLimits(0, 1000); t.Contents = 1000;

    sys.AddEdge(parent, child, upOnlyResources: new HashSet<Resource> { Resource.RP1 });

    var d = sys.RegisterDemand(parent, Resource.RP1, 30);
    sys.Solve();

    Assert.AreEqual(1.0, d.Activity, 0.001);
    Assert.AreEqual(-30, t.Rate, 0.001);
  }

  // ── Jettison ────────────────────────────────────────────────────

  [TestMethod]
  public void JettisonedNode_ExcludedFromActivePools() {
    var sys = new StagingFlowSystem();
    var sideA = sys.AddNode(); sideA.DrainPriority = 2;
    var sideB = sys.AddNode(); sideB.DrainPriority = 2;
    var engineNode = sys.AddNode();
    foreach (var n in new[] { sideA, sideB }) {
      var t = n.AddBuffer(Resource.RP1, 500);
      t.FlowLimits(0, 1000); t.Contents = 500;
    }
    sys.AddEdge(sideA, engineNode);
    sys.AddEdge(sideB, engineNode);

    sideB.Jettisoned = true;

    var d = sys.RegisterDemand(engineNode, Resource.RP1, 30);
    sys.Solve();

    Assert.AreEqual(1.0, d.Activity, 0.001);
    Assert.AreEqual(-30, sideA.Buffers[0].Rate, 0.001);
    Assert.AreEqual(0,   sideB.Buffers[0].Rate, 0.001);
  }

  // ── MaxTickDt ───────────────────────────────────────────────────

  [TestMethod]
  public void MaxTickDt_TimeUntilFirstPoolEmpties() {
    // 100 L tank draining at 10 L/s should report 10 s horizon.
    var sys = new StagingFlowSystem();
    var node = sys.AddNode();
    var t = node.AddBuffer(Resource.RP1, 1000);
    t.FlowLimits(0, 100); t.Contents = 100;

    sys.RegisterDemand(node, Resource.RP1, 10);
    sys.Solve();

    Assert.AreEqual(10.0, sys.MaxTickDt(), 0.001);
  }

  [TestMethod]
  public void MaxTickDt_NoActivePools_PositiveInfinity() {
    var sys = new StagingFlowSystem();
    var node = sys.AddNode();
    var t = node.AddBuffer(Resource.RP1, 1000);
    t.FlowLimits(0, 100); t.Contents = 1000;
    // No demands registered.
    sys.Solve();
    Assert.AreEqual(double.PositiveInfinity, sys.MaxTickDt());
  }

  // ── Tick ────────────────────────────────────────────────────────

  [TestMethod]
  public void ClockAdvance_LerpsContents() {
    var sys = new StagingFlowSystem();
    var node = sys.AddNode();
    var t = node.AddBuffer(Resource.RP1, 1000);
    t.FlowLimits(0, 100); t.Contents = 1000;

    sys.RegisterDemand(node, Resource.RP1, 30);
    sys.Solve();

    // Buffer doesn't integrate per-tick — Contents lerps from
    // baseline + Rate × elapsed. Advance the clock directly.
    sys.Clock.UT += 2.0;
    Assert.AreEqual(940, t.Contents, 0.001);
  }

  // ── Multi-resource ──────────────────────────────────────────────

  [TestMethod]
  public void MultipleResources_SolvedIndependently() {
    // RP-1 and LOX with different reach sets. No cross-coupling.
    var sys = new StagingFlowSystem();
    var nodeA = sys.AddNode();
    var nodeB = sys.AddNode();
    var engineNode = sys.AddNode();

    var rp1 = nodeA.AddBuffer(Resource.RP1, 500);
    rp1.FlowLimits(0, 1000); rp1.Contents = 500;
    var lox = nodeB.AddBuffer(Resource.LiquidOxygen, 500);
    lox.FlowLimits(0, 1000); lox.Contents = 500;

    sys.AddEdge(nodeA, engineNode);
    sys.AddEdge(nodeB, engineNode);

    var dRp1 = sys.RegisterDemand(engineNode, Resource.RP1, 20);
    var dLox = sys.RegisterDemand(engineNode, Resource.LiquidOxygen, 30);

    sys.Solve();

    Assert.AreEqual(1.0, dRp1.Activity, 0.001);
    Assert.AreEqual(1.0, dLox.Activity, 0.001);
    Assert.AreEqual(-20, rp1.Rate, 0.001);
    Assert.AreEqual(-30, lox.Rate, 0.001);
  }

  [TestMethod]
  public void UniformResource_Ignored() {
    // EC is Uniform — StagingFlowSystem must not touch it.
    var sys = new StagingFlowSystem();
    var node = sys.AddNode();
    var bat = node.AddBuffer(Resource.ElectricCharge, 1000);
    bat.FlowLimits(10, 10); bat.Contents = 1000;

    var d = sys.RegisterDemand(node, Resource.ElectricCharge, 5);
    sys.Solve();

    Assert.AreEqual(0.0, d.Activity, 0.001, "Uniform resources must not be solved by StagingFlowSystem");
    Assert.AreEqual(0, bat.Rate, 0.001);
  }

  // ── Multiple consumers in one component ─────────────────────────

  [TestMethod]
  public void TwoConsumersOneTank_FullSatisfactionUnderCap() {
    var sys = new StagingFlowSystem();
    var tank = sys.AddNode();
    var eng1 = sys.AddNode();
    var eng2 = sys.AddNode();

    var t = tank.AddBuffer(Resource.RP1, 1000);
    t.FlowLimits(0, 1000); t.Contents = 1000;

    sys.AddEdge(tank, eng1);
    sys.AddEdge(tank, eng2);

    var d1 = sys.RegisterDemand(eng1, Resource.RP1, 20);
    var d2 = sys.RegisterDemand(eng2, Resource.RP1, 30);

    sys.Solve();

    Assert.AreEqual(1.0, d1.Activity, 0.001);
    Assert.AreEqual(1.0, d2.Activity, 0.001);
    Assert.AreEqual(-50, t.Rate, 0.001);
  }

  [TestMethod]
  public void TwoConsumersOneTank_RateCapped_FairlyShared() {
    var sys = new StagingFlowSystem();
    var tank = sys.AddNode();
    var eng1 = sys.AddNode();
    var eng2 = sys.AddNode();

    var t = tank.AddBuffer(Resource.RP1, 1000);
    t.FlowLimits(0, 30); t.Contents = 1000;

    sys.AddEdge(tank, eng1);
    sys.AddEdge(tank, eng2);

    var d1 = sys.RegisterDemand(eng1, Resource.RP1, 20);
    var d2 = sys.RegisterDemand(eng2, Resource.RP1, 30);

    sys.Solve();

    // Total demand 50; supply capped at 30. Activity = 30/50 = 0.6.
    // Both consumers should get the same activity (proportional to
    // their requested rate).
    Assert.AreEqual(0.6, d1.Activity, 0.01);
    Assert.AreEqual(0.6, d2.Activity, 0.01);
    Assert.AreEqual(-30, t.Rate, 0.001);
  }
}
