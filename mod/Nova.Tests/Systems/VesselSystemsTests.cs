using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nova.Core.Resources;
using Nova.Core.Systems;

namespace Nova.Tests.Systems;

[TestClass]
public class VesselSystemsTests {

  private static (VesselSystems systems, StagingFlowSystem.Node node) Setup() {
    var systems = new VesselSystems();
    var node = systems.Staging.AddNode();
    return (systems, node);
  }

  // ── Domain routing ────────────────────────────────────────────────

  [TestMethod]
  public void AddDevice_RoutesTopologicalToStaging() {
    var (systems, node) = Setup();
    var d = systems.AddDevice(node,
        inputs: new[] { (Resource.LiquidHydrogen, 1.0) });
    Assert.AreEqual(ResourceDomain.Topological, d.Domain);
  }

  [TestMethod]
  public void AddDevice_RoutesUniformToProcess() {
    var (systems, node) = Setup();
    var d = systems.AddDevice(node,
        inputs: new[] { (Resource.ElectricCharge, 100.0) });
    Assert.AreEqual(ResourceDomain.Uniform, d.Domain);
  }

  [TestMethod]
  public void AddDevice_OutputOnly_ProcessOK() {
    var (systems, node) = Setup();
    var d = systems.AddDevice(node,
        outputs: new[] { (Resource.ElectricCharge, 2500.0) });
    Assert.AreEqual(ResourceDomain.Uniform, d.Domain);
  }

  // ── Validation ────────────────────────────────────────────────────

  [TestMethod]
  public void AddDevice_RejectsMixedDomainInputs() {
    var (systems, node) = Setup();
    Assert.ThrowsException<ArgumentException>(() =>
      systems.AddDevice(node, inputs: new[] {
        (Resource.LiquidHydrogen, 1.0),     // Topological
        (Resource.ElectricCharge, 100.0),   // Uniform — boom
      }));
  }

  [TestMethod]
  public void AddDevice_RejectsMixedInputOutputDomains() {
    var (systems, node) = Setup();
    // Inputs uniform, outputs topological — outputs leg will fail
    // before the topological-output rule even fires (mixed-domain
    // throws first).
    Assert.ThrowsException<ArgumentException>(() =>
      systems.AddDevice(node,
        inputs:  new[] { (Resource.ElectricCharge, 100.0) },
        outputs: new[] { (Resource.LiquidHydrogen, 1.0) }));
  }

  [TestMethod]
  public void AddDevice_RejectsTopologicalOutput() {
    var (systems, node) = Setup();
    Assert.ThrowsException<ArgumentException>(() =>
      systems.AddDevice(node,
        outputs: new[] { (Resource.LiquidHydrogen, 1.0) }));
  }

  [TestMethod]
  public void AddDevice_RejectsEmpty() {
    var (systems, node) = Setup();
    Assert.ThrowsException<ArgumentException>(() =>
      systems.AddDevice(node));
  }
}
