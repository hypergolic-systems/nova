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
  public void ConverterSuppliesDevice() {
    var solver = new ResourceSolver();
    var node = solver.AddNode();

    var converter = node.AddConverter();
    converter.AddOutput(Resource.ElectricCharge, 100);

    var d1 = node.AddDevice(ResourceSolver.Priority.Low);
    d1.AddInput(Resource.ElectricCharge, 10);
    d1.Demand = 1.0;

    var d2 = node.AddDevice(ResourceSolver.Priority.Low);
    d2.AddInput(Resource.ElectricCharge, 25);
    d2.Demand = 1.0;

    solver.Solve();

    Assert.AreEqual(1.0, d1.Activity, 0.01);
    Assert.AreEqual(1.0, d2.Activity, 0.01);
    Assert.AreEqual(0.35, converter.Activity, 0.01);
  }

  [TestMethod]
  public void BufferDrainWhenConverterInsufficient() {
    var solver = new ResourceSolver();
    var node = solver.AddNode();

    var converter = node.AddConverter();
    converter.AddOutput(Resource.ElectricCharge, 10);

    var buf = node.AddBuffer(Resource.ElectricCharge, 100);
    buf.FlowLimits(10, 10);
    buf.Contents = 100;

    var d = node.AddDevice(ResourceSolver.Priority.Low);
    d.AddInput(Resource.ElectricCharge, 15);
    d.Demand = 1.0;

    solver.Solve();

    Assert.AreEqual(1.0, d.Activity, 0.01);
    Assert.AreEqual(1.0, converter.Activity, 0.01);
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

    var converter = node.AddConverter();
    converter.AddOutput(Resource.ElectricCharge, 10);

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
  public void ParentConstraint() {
    // Alternator: converter bounded by engine activity.
    var solver = new ResourceSolver();
    var node = solver.AddNode();

    var rp1Tank = node.AddBuffer(Resource.RP1, 1000);
    rp1Tank.FlowLimits(0, 100);
    rp1Tank.Contents = 1000;

    var engine = node.AddDevice(ResourceSolver.Priority.Low);
    engine.AddInput(Resource.RP1, 30);
    engine.Demand = 0.5; // 50% throttle

    var alternator = node.AddConverter();
    alternator.AddParent(engine);
    alternator.AddOutput(Resource.ElectricCharge, 10);

    var light = node.AddDevice(ResourceSolver.Priority.Low);
    light.AddInput(Resource.ElectricCharge, 20);
    light.Demand = 1.0;

    solver.Solve();

    Assert.AreEqual(0.5, engine.Activity, 0.01);
    // Alternator bounded by engine activity (0.5), produces max 5 EC.
    // Light needs 20, gets at most 5 → activity = 5/20 = 0.25.
    Assert.AreEqual(0.25, light.Activity, 0.01);
    Assert.IsTrue(alternator.Activity <= engine.Activity + 1e-6);
  }

  [TestMethod]
  public void ParentConstraintNoBackpressure() {
    // No EC demand → alternator at 0, engine unaffected.
    var solver = new ResourceSolver();
    var node = solver.AddNode();

    var rp1Tank = node.AddBuffer(Resource.RP1, 1000);
    rp1Tank.FlowLimits(0, 100);
    rp1Tank.Contents = 1000;

    var engine = node.AddDevice(ResourceSolver.Priority.Low);
    engine.AddInput(Resource.RP1, 30);
    engine.Demand = 1.0;

    var alternator = node.AddConverter();
    alternator.AddParent(engine);
    alternator.AddOutput(Resource.ElectricCharge, 10);

    solver.Solve();

    Assert.AreEqual(1.0, engine.Activity, 0.01, "Engine should run at full demand");
    Assert.AreEqual(0, alternator.Activity, 0.01, "No EC demand → alternator off");
  }

  [TestMethod]
  public void AlternatorFeedsBattery() {
    // Engine alternator produces EC, fills battery, powers light.
    var solver = new ResourceSolver();
    var node = solver.AddNode();

    var rp1Tank = node.AddBuffer(Resource.RP1, 1000);
    rp1Tank.FlowLimits(0, 100);
    rp1Tank.Contents = 1000;

    var battery = node.AddBuffer(Resource.ElectricCharge, 100);
    battery.FlowLimits(10, 10);
    battery.Contents = 50;

    var engine = node.AddDevice(ResourceSolver.Priority.Low);
    engine.AddInput(Resource.RP1, 30);
    engine.Demand = 1.0;

    var alternator = node.AddConverter();
    alternator.AddParent(engine);
    alternator.AddOutput(Resource.ElectricCharge, 7);

    var light = node.AddDevice(ResourceSolver.Priority.Low);
    light.AddInput(Resource.ElectricCharge, 5);
    light.Demand = 1.0;

    solver.Solve();

    Assert.AreEqual(1.0, engine.Activity, 0.01, "Engine fully satisfied");
    Assert.AreEqual(1.0, light.Activity, 0.01, "Light fully satisfied from alternator");
    Assert.IsTrue(alternator.Activity > 0.7, $"Alternator should run, got {alternator.Activity}");
    Assert.IsTrue(battery.Rate >= 0, $"Battery should not drain (alternator covers demand), rate={battery.Rate}");
  }

  [TestMethod]
  public void CostMinimization() {
    // Two converters, different costs. Solver should prefer cheaper.
    var solver = new ResourceSolver();
    var node = solver.AddNode();

    var cheap = node.AddConverter();
    cheap.AddOutput(Resource.ElectricCharge, 10);
    cheap.Cost = 1;

    var expensive = node.AddConverter();
    expensive.AddOutput(Resource.ElectricCharge, 10);
    expensive.Cost = 10;

    var d = node.AddDevice(ResourceSolver.Priority.Low);
    d.AddInput(Resource.ElectricCharge, 8);
    d.Demand = 1.0;

    solver.Solve();

    Assert.AreEqual(1.0, d.Activity, 0.01);
    Assert.AreEqual(0.8, cheap.Activity, 0.01, "Cheap converter should run");
    Assert.AreEqual(0, expensive.Activity, 0.01, "Expensive converter should not run");
  }

  [TestMethod]
  public void DrainCost() {
    // Converter (cost 1) vs buffer drain (drain cost 10). Prefer converter.
    var solver = new ResourceSolver();
    solver.SetDrainCost(Resource.ElectricCharge, 10);
    var node = solver.AddNode();

    var converter = node.AddConverter();
    converter.AddOutput(Resource.ElectricCharge, 20);
    converter.Cost = 1;

    var buf = node.AddBuffer(Resource.ElectricCharge, 100);
    buf.FlowLimits(0, 20);
    buf.Contents = 100;

    var d = node.AddDevice(ResourceSolver.Priority.Low);
    d.AddInput(Resource.ElectricCharge, 10);
    d.Demand = 1.0;

    solver.Solve();

    Assert.AreEqual(1.0, d.Activity, 0.01);
    Assert.AreEqual(0.5, converter.Activity, 0.01, "Converter should supply (cheaper)");
    Assert.AreEqual(0, buf.Rate, 0.5, "Buffer should not drain");
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
}
