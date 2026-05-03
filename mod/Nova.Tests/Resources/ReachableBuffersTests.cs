using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nova.Core.Resources;
using Nova.Core.Systems;

namespace Nova.Tests.Resources;

// Reach queries on StagingFlowSystem — exercised by NovaEngineTopic
// when computing per-engine fuel-pool grouping for the HUD. Up-only
// semantics here mirror PartModule wiring: an "up" decoupler/dock
// only lets resources flow from child to parent (toward the root).
[TestClass]
public class ReachableBuffersTests {

  [TestMethod]
  public void SameNode_BufferReachable() {
    var sys = new StagingFlowSystem();
    var node = sys.AddNode();
    var buf = node.AddBuffer(Resource.LiquidHydrogen, 100);

    var reached = sys.ReachableBuffers(node, Resource.LiquidHydrogen);

    CollectionAssert.AreEquivalent(new[] { buf }, reached);
  }

  [TestMethod]
  public void ParentEngineDrainsChildTank_Bidirectional() {
    var sys = new StagingFlowSystem();
    var engineNode = sys.AddNode();
    var tankNode = sys.AddNode();
    sys.AddEdge(engineNode, tankNode);
    var buf = tankNode.AddBuffer(Resource.RP1, 100);

    var reached = sys.ReachableBuffers(engineNode, Resource.RP1);

    CollectionAssert.Contains(reached, buf);
  }

  [TestMethod]
  public void ChildEngineDrainsParentTank_Bidirectional() {
    var sys = new StagingFlowSystem();
    var tankNode = sys.AddNode();
    var engineNode = sys.AddNode();
    sys.AddEdge(tankNode, engineNode);
    var buf = tankNode.AddBuffer(Resource.RP1, 100);

    var reached = sys.ReachableBuffers(engineNode, Resource.RP1);

    CollectionAssert.Contains(reached, buf);
  }

  [TestMethod]
  public void DisallowedResource_NotReachable() {
    var sys = new StagingFlowSystem();
    var engineNode = sys.AddNode();
    var tankNode = sys.AddNode();
    // Edge only allows LiquidOxygen — RP1 is gated.
    sys.AddEdge(engineNode, tankNode, new HashSet<Resource> { Resource.LiquidOxygen });
    tankNode.AddBuffer(Resource.RP1, 100);

    var reached = sys.ReachableBuffers(engineNode, Resource.RP1);

    Assert.AreEqual(0, reached.Count);
  }

  [TestMethod]
  public void JettisonedNode_Excluded() {
    var sys = new StagingFlowSystem();
    var engineNode = sys.AddNode();
    var tankNode = sys.AddNode();
    sys.AddEdge(engineNode, tankNode);
    tankNode.AddBuffer(Resource.RP1, 100);

    tankNode.Jettisoned = true;

    var reached = sys.ReachableBuffers(engineNode, Resource.RP1);

    Assert.AreEqual(0, reached.Count);
  }

  [TestMethod]
  public void Asparagus_BeforeJettison_BothBoostersReachable() {
    // Core engine on root; two side boosters as children. Both side
    // tanks crossfeed into the core via allowed edges.
    var sys = new StagingFlowSystem();
    var coreNode = sys.AddNode();
    var sideA = sys.AddNode();
    var sideB = sys.AddNode();
    sys.AddEdge(coreNode, sideA);
    sys.AddEdge(coreNode, sideB);
    var bufA = sideA.AddBuffer(Resource.RP1, 100);
    var bufB = sideB.AddBuffer(Resource.RP1, 100);

    var reached = sys.ReachableBuffers(coreNode, Resource.RP1);

    CollectionAssert.AreEquivalent(new[] { bufA, bufB }, reached);
  }

  [TestMethod]
  public void Asparagus_AfterJettison_RemainingTankOnlyReachable() {
    var sys = new StagingFlowSystem();
    var coreNode = sys.AddNode();
    var sideA = sys.AddNode();
    var sideB = sys.AddNode();
    sys.AddEdge(coreNode, sideA);
    sys.AddEdge(coreNode, sideB);
    sideA.AddBuffer(Resource.RP1, 100);
    var bufB = sideB.AddBuffer(Resource.RP1, 100);

    sideA.Jettisoned = true;

    var reached = sys.ReachableBuffers(coreNode, Resource.RP1);

    CollectionAssert.AreEquivalent(new[] { bufB }, reached);
  }

  [TestMethod]
  public void UpOnlyEdge_ChildToParent_ParentCanReachChild() {
    // UpOnly clamps flow to child→parent only. An engine at the parent
    // (root-side) CAN drain a tank at the child — supply flows up to it.
    var sys = new StagingFlowSystem();
    var engineNode = sys.AddNode();
    var tankNode = sys.AddNode();
    sys.AddEdge(engineNode, tankNode,
      allowedResources: null,
      upOnlyResources: new HashSet<Resource> { Resource.RP1 });
    var buf = tankNode.AddBuffer(Resource.RP1, 100);

    var reached = sys.ReachableBuffers(engineNode, Resource.RP1);

    CollectionAssert.Contains(reached, buf);
  }

  [TestMethod]
  public void UpOnlyEdge_ParentToChild_ChildCannotReachParent() {
    // From the child's perspective, parent supply is blocked — UpOnly
    // forbids parent→child flow. A child engine cannot drain a parent
    // tank in this configuration.
    var sys = new StagingFlowSystem();
    var tankNode = sys.AddNode();
    var engineNode = sys.AddNode();
    sys.AddEdge(tankNode, engineNode,
      allowedResources: null,
      upOnlyResources: new HashSet<Resource> { Resource.RP1 });
    tankNode.AddBuffer(Resource.RP1, 100);

    var reached = sys.ReachableBuffers(engineNode, Resource.RP1);

    Assert.AreEqual(0, reached.Count);
  }

  [TestMethod]
  public void ReachableNodes_IncludesStartingNode() {
    var sys = new StagingFlowSystem();
    var node = sys.AddNode();

    var reached = sys.ReachableNodes(node, Resource.RP1);

    Assert.IsTrue(reached.Contains(node));
  }

  [TestMethod]
  public void ReachableNodes_StartingNodeJettisoned_Empty() {
    var sys = new StagingFlowSystem();
    var node = sys.AddNode();
    node.Jettisoned = true;

    var reached = sys.ReachableNodes(node, Resource.RP1);

    Assert.AreEqual(0, reached.Count);
  }
}
