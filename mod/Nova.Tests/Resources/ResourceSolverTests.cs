using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nova.Core;
using Nova.Core.Resources;
using Nova.Core.Flight;
using Nova.Core.Utils;
namespace Nova.Tests.Resources;

[TestClass]
public class ResourceSolverTests {

  [TestMethod]
  public void ProducerSuppliesDevices() {
    var solver = new ResourceSolver();
    var node = solver.AddNode();

    var producer = node.AddDevice(ResourceSolver.Priority.Low);
    producer.AddOutput(Resource.ElectricCharge, 100);
    producer.Demand = 1.0;

    var d1 = node.AddDevice(ResourceSolver.Priority.Low);
    d1.AddInput(Resource.ElectricCharge, 10);
    d1.Demand = 1.0;

    var d2 = node.AddDevice(ResourceSolver.Priority.Low);
    d2.AddInput(Resource.ElectricCharge, 25);
    d2.Demand = 1.0;

    solver.Solve();

    Assert.AreEqual(1.0, d1.Activity, 0.01);
    Assert.AreEqual(1.0, d2.Activity, 0.01);
    Assert.AreEqual(0.35, producer.Activity, 0.01);
  }

  [TestMethod]
  public void BufferDrainWhenProducerInsufficient() {
    var solver = new ResourceSolver();
    var node = solver.AddNode();

    var producer = node.AddDevice(ResourceSolver.Priority.Low);
    producer.AddOutput(Resource.ElectricCharge, 10);
    producer.Demand = 1.0;

    var buf = node.AddBuffer(Resource.ElectricCharge, 100);
    buf.FlowLimits(10, 10);
    buf.Contents = 100;

    var d = node.AddDevice(ResourceSolver.Priority.Low);
    d.AddInput(Resource.ElectricCharge, 15);
    d.Demand = 1.0;

    solver.Solve();

    Assert.AreEqual(1.0, d.Activity, 0.01);
    Assert.AreEqual(1.0, producer.Activity, 0.01);
    Assert.AreEqual(-5, buf.Rate, 0.5);
  }

  [TestMethod]
  public void BufferOnlySupply() {
    var solver = new ResourceSolver();
    var node = solver.AddNode();

    var buf = node.AddBuffer(Resource.ElectricCharge, 100);
    buf.FlowLimits(0, 50);
    buf.Contents = 100;

    var d = node.AddDevice(ResourceSolver.Priority.Low);
    d.AddInput(Resource.ElectricCharge, 30);
    d.Demand = 1.0;

    solver.Solve();

    Assert.AreEqual(1.0, d.Activity, 0.01);
    Assert.AreEqual(-30, buf.Rate, 0.5);
  }

  [TestMethod]
  public void BufferOnlySupplyRateLimited() {
    var solver = new ResourceSolver();
    var node = solver.AddNode();

    var buf = node.AddBuffer(Resource.ElectricCharge, 100);
    buf.FlowLimits(0, 20);
    buf.Contents = 100;

    var d = node.AddDevice(ResourceSolver.Priority.Low);
    d.AddInput(Resource.ElectricCharge, 30);
    d.Demand = 1.0;

    solver.Solve();

    Assert.AreEqual(20.0 / 30.0, d.Activity, 0.01);
    Assert.AreEqual(-20, buf.Rate, 0.5);
  }

  [TestMethod]
  public void DeviceWithCoupledInputs() {
    // Engine-like: two propellants must scale together.
    var solver = new ResourceSolver();
    var node = solver.AddNode();

    var rp1Tank = node.AddBuffer(Resource.RP1, 390);
    rp1Tank.FlowLimits(0, 100);
    rp1Tank.Contents = 390;

    var loxTank = node.AddBuffer(Resource.LiquidOxygen, 130);
    loxTank.FlowLimits(0, 100);
    loxTank.Contents = 130;

    var engine = node.AddDevice(ResourceSolver.Priority.Low);
    engine.AddInput(Resource.RP1, 62.3);
    engine.AddInput(Resource.LiquidOxygen, 20.8);
    engine.Demand = 1.0;

    solver.Solve();

    Assert.AreEqual(1.0, engine.Activity, 0.01);
    Assert.AreEqual(-62.3, rp1Tank.Rate, 0.5);
    Assert.AreEqual(-20.8, loxTank.Rate, 0.5);
  }

  [TestMethod]
  public void DeviceCoupledRateLimited() {
    // RP-1 rate-limited → both propellants scale down.
    var solver = new ResourceSolver();
    var node = solver.AddNode();

    var rp1Tank = node.AddBuffer(Resource.RP1, 390);
    rp1Tank.FlowLimits(0, 50); // rate-limited
    rp1Tank.Contents = 390;

    var loxTank = node.AddBuffer(Resource.LiquidOxygen, 130);
    loxTank.FlowLimits(0, 100);
    loxTank.Contents = 130;

    var engine = node.AddDevice(ResourceSolver.Priority.Low);
    engine.AddInput(Resource.RP1, 62.3);
    engine.AddInput(Resource.LiquidOxygen, 20.8);
    engine.Demand = 1.0;

    solver.Solve();

    var expectedActivity = 50.0 / 62.3;
    Assert.AreEqual(expectedActivity, engine.Activity, 0.01);
    Assert.AreEqual(20.8 * expectedActivity, -loxTank.Rate, 0.5);
  }

  [TestMethod]
  public void FuelExhaustion() {
    var solver = new ResourceSolver();
    var node = solver.AddNode();

    var rp1Tank = node.AddBuffer(Resource.RP1, 390);
    rp1Tank.FlowLimits(0, 100);
    rp1Tank.Contents = 390;

    var loxTank = node.AddBuffer(Resource.LiquidOxygen, 130);
    loxTank.FlowLimits(0, 100);
    loxTank.Contents = 130;

    var engine = node.AddDevice(ResourceSolver.Priority.Low);
    engine.AddInput(Resource.RP1, 62.3);
    engine.AddInput(Resource.LiquidOxygen, 20.8);
    engine.Demand = 1.0;

    // First solve: tanks full.
    solver.Solve();
    Assert.AreEqual(1.0, engine.Activity, 0.01);

    // Simulate fuel exhaustion.
    rp1Tank.Contents = 0;
    loxTank.Contents = 0;
    solver.Solve();

    Assert.AreEqual(0, engine.Activity, 0.01);
    Assert.AreEqual(0, rp1Tank.Rate, 0.01);
    Assert.AreEqual(0, loxTank.Rate, 0.01);
  }

  [TestMethod]
  public void FuelExhaustionOneEmpty() {
    var solver = new ResourceSolver();
    var node = solver.AddNode();

    var rp1Tank = node.AddBuffer(Resource.RP1, 390);
    rp1Tank.FlowLimits(0, 100);
    rp1Tank.Contents = 0; // empty!

    var loxTank = node.AddBuffer(Resource.LiquidOxygen, 130);
    loxTank.FlowLimits(0, 100);
    loxTank.Contents = 130;

    var engine = node.AddDevice(ResourceSolver.Priority.Low);
    engine.AddInput(Resource.RP1, 62.3);
    engine.AddInput(Resource.LiquidOxygen, 20.8);
    engine.Demand = 1.0;

    solver.Solve();

    Assert.AreEqual(0, engine.Activity, 0.01);
    Assert.AreEqual(0, loxTank.Rate, 0.01);
  }

  [TestMethod]
  public void BufferFill() {
    var solver = new ResourceSolver();
    var node = solver.AddNode();

    var producer = node.AddDevice(ResourceSolver.Priority.Low);
    producer.AddOutput(Resource.ElectricCharge, 10);
    producer.Demand = 1.0;

    var buf = node.AddBuffer(Resource.ElectricCharge, 100);
    buf.FlowLimits(10, 10);
    buf.Contents = 0;

    var d = node.AddDevice(ResourceSolver.Priority.Low);
    d.AddInput(Resource.ElectricCharge, 3);
    d.Demand = 1.0;

    solver.Solve();

    Assert.AreEqual(1.0, d.Activity, 0.01);
    Assert.AreEqual(7, buf.Rate, 0.5);
  }

  [TestMethod]
  public void MultiNodeFlow() {
    // Tank on one node, engine on another, connected by edge.
    var solver = new ResourceSolver();
    var tankNode = solver.AddNode();
    var engineNode = solver.AddNode();
    solver.AddEdge(tankNode, engineNode);

    var rp1Tank = tankNode.AddBuffer(Resource.RP1, 1000);
    rp1Tank.FlowLimits(0, 100);
    rp1Tank.Contents = 1000;

    var engine = engineNode.AddDevice(ResourceSolver.Priority.Low);
    engine.AddInput(Resource.RP1, 30);
    engine.Demand = 1.0;

    solver.Solve();

    Assert.AreEqual(1.0, engine.Activity, 0.01);
    Assert.AreEqual(-30, rp1Tank.Rate, 0.5);
  }

  [TestMethod]
  public void DevicePriorityOrdering() {
    // High-priority device gets satisfied first when supply is limited.
    var solver = new ResourceSolver();
    var node = solver.AddNode();

    var buf = node.AddBuffer(Resource.ElectricCharge, 100);
    buf.FlowLimits(0, 10);
    buf.Contents = 100;

    var critical = node.AddDevice(ResourceSolver.Priority.Critical);
    critical.AddInput(Resource.ElectricCharge, 8);
    critical.Demand = 1.0;

    var low = node.AddDevice(ResourceSolver.Priority.Low);
    low.AddInput(Resource.ElectricCharge, 8);
    low.Demand = 1.0;

    solver.Solve();

    Assert.AreEqual(1.0, critical.Activity, 0.01, "Critical device fully satisfied");
    Assert.AreEqual(0.25, low.Activity, 0.01, "Low device gets remaining (2/8)");
  }

  [TestMethod]
  public void SolveTwice() {
    var solver = new ResourceSolver();
    var node = solver.AddNode();

    var buf = node.AddBuffer(Resource.ElectricCharge, 100);
    buf.FlowLimits(0, 50);
    buf.Contents = 100;

    var d = node.AddDevice(ResourceSolver.Priority.Low);
    d.AddInput(Resource.ElectricCharge, 10);

    // First solve: zero demand.
    d.Demand = 0;
    solver.Solve();
    Assert.AreEqual(0, d.Activity, 0.01);

    // Second solve: demand increases.
    d.Demand = 1.0;
    buf.Contents = 90;
    solver.Solve();
    Assert.AreEqual(1.0, d.Activity, 0.01);
  }

  [TestMethod]
  public void StagingDrainsHigherPriorityFirst() {
    // Two buffers, same resource, different drain priorities. Higher-priority
    // pool should drain first via cost-min weights.
    var solver = new ResourceSolver();
    var root = solver.AddNode();
    var stage = solver.AddNode();
    stage.DrainPriority = 1; // higher drain priority
    solver.AddEdge(root, stage);

    var rootBuf = root.AddBuffer(Resource.ElectricCharge, 100);
    rootBuf.FlowLimits(0, 50);
    rootBuf.Contents = 100;

    var stageBuf = stage.AddBuffer(Resource.ElectricCharge, 100);
    stageBuf.FlowLimits(0, 50);
    stageBuf.Contents = 100;

    var d = root.AddDevice(ResourceSolver.Priority.Low);
    d.AddInput(Resource.ElectricCharge, 30);
    d.Demand = 1.0;

    solver.Solve();

    Assert.AreEqual(1.0, d.Activity, 0.01);
    Assert.IsTrue(stageBuf.Rate < -0.1, $"Stage buffer should drain first, Rate={stageBuf.Rate}");
    Assert.AreEqual(0, rootBuf.Rate, 0.01, "Root buffer should not drain while stage has fuel");
  }

  [TestMethod]
  public void DisconnectedSubNetworks_FairnessIndependent() {
    // Two completely disconnected sub-networks on the same solver, both
    // for the same resource. Network 1 has symmetric pools; Network 2
    // has a single pool. With a single global fairness scalar (the old
    // formulation), Network 2's solo pool would couple Network 1's
    // β and prevent Network 1's pools from achieving symmetric drain.
    // Iterative-LP fairness should keep them independent.
    var solver = new ResourceSolver();

    // Network 1: pod-1 with two side tanks feeding one engine.
    var pod1 = solver.AddNode();
    var sideA = solver.AddNode();
    var sideB = solver.AddNode();
    solver.AddEdge(pod1, sideA);
    solver.AddEdge(pod1, sideB);

    var tankA = sideA.AddBuffer(Resource.RP1, 100);
    tankA.FlowLimits(0, 100);
    tankA.Contents = 100;
    var tankB = sideB.AddBuffer(Resource.RP1, 100);
    tankB.FlowLimits(0, 100);
    tankB.Contents = 100;

    var engine1 = pod1.AddDevice(ResourceSolver.Priority.Low);
    engine1.AddInput(Resource.RP1, 10);
    engine1.Demand = 1.0;

    // Network 2: separate pod with a single tank and engine. Same
    // resource (RP-1) but no edges connecting to Network 1.
    var pod2 = solver.AddNode();
    var tankC = pod2.AddBuffer(Resource.RP1, 50);
    tankC.FlowLimits(0, 100);
    tankC.Contents = 50;

    var engine2 = pod2.AddDevice(ResourceSolver.Priority.Low);
    engine2.AddInput(Resource.RP1, 5);
    engine2.Demand = 1.0;

    solver.Solve();

    Assert.AreEqual(1.0, engine1.Activity, 0.01, "Engine 1 should fire fully");
    Assert.AreEqual(1.0, engine2.Activity, 0.01, "Engine 2 should fire fully");

    // Network 1's symmetric tanks must drain at equal rates.
    Assert.AreEqual(tankA.Rate, tankB.Rate, 0.01,
      $"Network 1 symmetric tanks must drain equally: A={tankA.Rate}, B={tankB.Rate}");
    Assert.AreEqual(-5.0, tankA.Rate, 0.01, "Tank A drains at 5 (half of engine 1's 10)");
    Assert.AreEqual(-5.0, tankB.Rate, 0.01, "Tank B drains at 5 (half of engine 1's 10)");

    // Network 2's solo tank drains at engine 2's full demand.
    Assert.AreEqual(-5.0, tankC.Rate, 0.01, "Tank C supplies engine 2 directly");
  }

  [TestMethod]
  public void ConversionChain_DemandPropagatesUpstream() {
    // A converter device transforms resource A into resource B. A
    // downstream consumer demands B; the converter must run, which
    // creates demand on resource A's pool. The LP must satisfy the
    // whole chain in one solve.
    var solver = new ResourceSolver();
    var node = solver.AddNode();

    // Source pool of resource A.
    var tankA = node.AddBuffer(Resource.RP1, 100);
    tankA.FlowLimits(0, 100);
    tankA.Contents = 100;

    // Converter: consumes RP-1, produces EC. Activity = 1 → consumes 2
    // RP-1, produces 10 EC. Same priority as consumer.
    var converter = node.AddDevice(ResourceSolver.Priority.Low);
    converter.AddInput(Resource.RP1, 2);
    converter.AddOutput(Resource.ElectricCharge, 10);
    converter.Demand = 1.0;

    // Consumer of EC.
    var consumer = node.AddDevice(ResourceSolver.Priority.Low);
    consumer.AddInput(Resource.ElectricCharge, 10);
    consumer.Demand = 1.0;

    solver.Solve();

    Assert.AreEqual(1.0, consumer.Activity, 0.01, "Consumer should fire fully");
    Assert.AreEqual(1.0, converter.Activity, 0.01, "Converter should run to feed consumer");
    Assert.AreEqual(-2.0, tankA.Rate, 0.01, "Tank A drains at converter's intake rate");
  }
}
