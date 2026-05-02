using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nova.Core.Resources;

namespace Nova.Tests.Resources;

[TestClass]
public class ReachableBuffersTests {

  [TestMethod]
  public void SameNode_BufferReachable() {
    var solver = new ResourceSolver();
    var node = solver.AddNode();
    var buf = node.AddBuffer(Resource.LiquidHydrogen, 100);

    var reached = solver.ReachableBuffers(node, Resource.LiquidHydrogen);

    CollectionAssert.AreEquivalent(new[] { buf }, reached);
  }

  [TestMethod]
  public void ParentEngineDrainsChildTank_Bidirectional() {
    var solver = new ResourceSolver();
    var engineNode = solver.AddNode();
    var tankNode = solver.AddNode();
    solver.AddEdge(engineNode, tankNode);
    var buf = tankNode.AddBuffer(Resource.RP1, 100);

    var reached = solver.ReachableBuffers(engineNode, Resource.RP1);

    CollectionAssert.Contains(reached, buf);
  }

  [TestMethod]
  public void ChildEngineDrainsParentTank_Bidirectional() {
    var solver = new ResourceSolver();
    var tankNode = solver.AddNode();
    var engineNode = solver.AddNode();
    solver.AddEdge(tankNode, engineNode);
    var buf = tankNode.AddBuffer(Resource.RP1, 100);

    var reached = solver.ReachableBuffers(engineNode, Resource.RP1);

    CollectionAssert.Contains(reached, buf);
  }

  [TestMethod]
  public void DisallowedResource_NotReachable() {
    var solver = new ResourceSolver();
    var engineNode = solver.AddNode();
    var tankNode = solver.AddNode();
    // Edge only allows LiquidOxygen — RP1 is gated.
    solver.AddEdge(engineNode, tankNode, new HashSet<Resource> { Resource.LiquidOxygen });
    tankNode.AddBuffer(Resource.RP1, 100);

    var reached = solver.ReachableBuffers(engineNode, Resource.RP1);

    Assert.AreEqual(0, reached.Count);
  }

  [TestMethod]
  public void JettisonedNode_Excluded() {
    var solver = new ResourceSolver();
    var engineNode = solver.AddNode();
    var tankNode = solver.AddNode();
    solver.AddEdge(engineNode, tankNode);
    tankNode.AddBuffer(Resource.RP1, 100);

    tankNode.Jettisoned = true;

    var reached = solver.ReachableBuffers(engineNode, Resource.RP1);

    Assert.AreEqual(0, reached.Count);
  }

  [TestMethod]
  public void Asparagus_BeforeJettison_BothBoostersReachable() {
    // Core engine on root; two side boosters as children. Both side
    // tanks crossfeed into the core via allowed edges.
    var solver = new ResourceSolver();
    var coreNode = solver.AddNode();
    var sideA = solver.AddNode();
    var sideB = solver.AddNode();
    solver.AddEdge(coreNode, sideA);
    solver.AddEdge(coreNode, sideB);
    var bufA = sideA.AddBuffer(Resource.RP1, 100);
    var bufB = sideB.AddBuffer(Resource.RP1, 100);

    var reached = solver.ReachableBuffers(coreNode, Resource.RP1);

    CollectionAssert.AreEquivalent(new[] { bufA, bufB }, reached);
  }

  [TestMethod]
  public void Asparagus_AfterJettison_RemainingTankOnlyReachable() {
    var solver = new ResourceSolver();
    var coreNode = solver.AddNode();
    var sideA = solver.AddNode();
    var sideB = solver.AddNode();
    solver.AddEdge(coreNode, sideA);
    solver.AddEdge(coreNode, sideB);
    sideA.AddBuffer(Resource.RP1, 100);
    var bufB = sideB.AddBuffer(Resource.RP1, 100);

    sideA.Jettisoned = true;

    var reached = solver.ReachableBuffers(coreNode, Resource.RP1);

    CollectionAssert.AreEquivalent(new[] { bufB }, reached);
  }

  [TestMethod]
  public void UpOnlyEdge_ChildToParent_ParentCanReachChild() {
    // UpOnly clamps flow ≤ 0 (only child→parent). So an engine at
    // the parent CAN drain a tank at the child — supply still flows.
    var solver = new ResourceSolver();
    var engineNode = solver.AddNode();
    var tankNode = solver.AddNode();
    solver.AddEdge(engineNode, tankNode,
      allowedResources: null,
      upOnlyResources: new HashSet<Resource> { Resource.RP1 });
    var buf = tankNode.AddBuffer(Resource.RP1, 100);

    var reached = solver.ReachableBuffers(engineNode, Resource.RP1);

    CollectionAssert.Contains(reached, buf);
  }

  [TestMethod]
  public void UpOnlyEdge_ParentToChild_ChildCannotReachParent() {
    // From the child's perspective, parent supply is blocked — UpOnly
    // forbids parent→child flow. A child engine cannot drain a parent
    // tank in this configuration.
    var solver = new ResourceSolver();
    var tankNode = solver.AddNode();
    var engineNode = solver.AddNode();
    solver.AddEdge(tankNode, engineNode,
      allowedResources: null,
      upOnlyResources: new HashSet<Resource> { Resource.RP1 });
    tankNode.AddBuffer(Resource.RP1, 100);

    var reached = solver.ReachableBuffers(engineNode, Resource.RP1);

    Assert.AreEqual(0, reached.Count);
  }

  [TestMethod]
  public void ReachableNodes_IncludesStartingNode() {
    var solver = new ResourceSolver();
    var node = solver.AddNode();

    var reached = solver.ReachableNodes(node, Resource.RP1);

    Assert.IsTrue(reached.Contains(node));
  }

  [TestMethod]
  public void ReachableNodes_StartingNodeJettisoned_Empty() {
    var solver = new ResourceSolver();
    var node = solver.AddNode();
    node.Jettisoned = true;

    var reached = solver.ReachableNodes(node, Resource.RP1);

    Assert.AreEqual(0, reached.Count);
  }
}
