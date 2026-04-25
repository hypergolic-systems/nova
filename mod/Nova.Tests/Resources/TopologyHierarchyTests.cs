using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nova.Core.Resources;
using Nova.Core.Flight;
using Nova.Core.Utils;
namespace Nova.Tests.Resources;

[TestClass]
public class TopologyHierarchyTests {

  [TestMethod]
  public void ChildNodeUnrestricted_ResourcesFlowThrough() {
    var solver = new ResourceSolver();
    var root = solver.AddNode();
    var child = solver.AddNode();
    solver.AddEdge(root, child);

    var converter = root.AddConverter();
    converter.AddOutput(Resource.ElectricCharge, 100);

    var d = child.AddDevice(ResourceSolver.Priority.Low);
    d.AddInput(Resource.ElectricCharge, 50);
    d.Demand = 1.0;

    solver.Solve();

    Assert.AreEqual(1.0, d.Activity, 0.01, "Consumer on child node should be fully satisfied");
  }

  [TestMethod]
  public void ChildNodeBlockAll_ResourcesBlocked() {
    var solver = new ResourceSolver();
    var root = solver.AddNode();
    var child = solver.AddNode();
    solver.AddEdge(root, child, new HashSet<Resource>());

    var converter = root.AddConverter();
    converter.AddOutput(Resource.ElectricCharge, 100);

    var d = child.AddDevice(ResourceSolver.Priority.Low);
    d.AddInput(Resource.ElectricCharge, 50);
    d.Demand = 1.0;

    solver.Solve();

    Assert.AreEqual(0, d.Activity, 0.01, "Consumer should get nothing when all resources blocked");
  }

  [TestMethod]
  public void ChildNodeSelectiveAllow_OnlyAllowedResourceFlows() {
    var solver = new ResourceSolver();
    var root = solver.AddNode();
    var child = solver.AddNode();
    solver.AddEdge(root, child, new HashSet<Resource> { Resource.ElectricCharge });

    var ecConverter = root.AddConverter();
    ecConverter.AddOutput(Resource.ElectricCharge, 100);

    var rp1Converter = root.AddConverter();
    rp1Converter.AddOutput(Resource.RP1, 100);

    var ecDevice = child.AddDevice(ResourceSolver.Priority.Low);
    ecDevice.AddInput(Resource.ElectricCharge, 50);
    ecDevice.Demand = 1.0;

    var rp1Device = child.AddDevice(ResourceSolver.Priority.Low);
    rp1Device.AddInput(Resource.RP1, 50);
    rp1Device.Demand = 1.0;

    solver.Solve();

    Assert.AreEqual(1.0, ecDevice.Activity, 0.01, "EC should flow through (allowed)");
    Assert.AreEqual(0, rp1Device.Activity, 0.01, "RP-1 should be blocked");
  }

  [TestMethod]
  public void ChildNodeSelfSufficient_WorksWhenBlocked() {
    var solver = new ResourceSolver();
    var root = solver.AddNode();
    var child = solver.AddNode();
    solver.AddEdge(root, child, new HashSet<Resource>());

    var buf = child.AddBuffer(Resource.ElectricCharge, 100);
    buf.FlowLimits(0, 50);
    buf.Contents = 100;

    var d = child.AddDevice(ResourceSolver.Priority.Low);
    d.AddInput(Resource.ElectricCharge, 30);
    d.Demand = 1.0;

    solver.Solve();

    Assert.AreEqual(1.0, d.Activity, 0.01, "Consumer should drain from local buffer");
  }

  [TestMethod]
  public void BufferOnChildFlowsWhenAllowed() {
    var solver = new ResourceSolver();
    var root = solver.AddNode();
    var child = solver.AddNode();
    solver.AddEdge(root, child);

    var buf = child.AddBuffer(Resource.ElectricCharge, 100);
    buf.FlowLimits(0, 50);
    buf.Contents = 100;

    var d = root.AddDevice(ResourceSolver.Priority.Low);
    d.AddInput(Resource.ElectricCharge, 30);
    d.Demand = 1.0;

    solver.Solve();

    Assert.AreEqual(1.0, d.Activity, 0.01, "Consumer on root should be fed by child buffer");
  }

  [TestMethod]
  public void PriorityDrain_ChildDrainsBeforeRoot() {
    var solver = new ResourceSolver();
    var root = solver.AddNode();
    var child = solver.AddNode();
    child.DrainPriority = 1;
    solver.AddEdge(root, child);

    var rootBuf = root.AddBuffer(Resource.ElectricCharge, 100);
    rootBuf.FlowLimits(0, 50);
    rootBuf.Contents = 100;

    var childBuf = child.AddBuffer(Resource.ElectricCharge, 100);
    childBuf.FlowLimits(0, 50);
    childBuf.Contents = 100;

    var d = root.AddDevice(ResourceSolver.Priority.Low);
    d.AddInput(Resource.ElectricCharge, 30);
    d.Demand = 1.0;

    solver.Solve();

    Assert.IsTrue(childBuf.Rate < -0.1, $"Child buffer should drain, Rate={childBuf.Rate}");
    Assert.AreEqual(0, rootBuf.Rate, 0.01, "Root buffer should not drain while child has fuel");
  }
}
