using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nova.Core.Components;
using Nova.Core.Components.Electrical;
using Nova.Core.Components.Propulsion;

namespace Nova.Tests.Components;

[TestClass]
public class SystemTagsTests {

  [TestMethod]
  public void EmptyList_ReturnsEmpty() {
    var tags = SystemTags.For(new List<VirtualComponent>());
    CollectionAssert.AreEqual(new List<string>(), tags);
  }

  [TestMethod]
  public void SolarPanel_TagsPowerGen() {
    var tags = SystemTags.For(new VirtualComponent[] { new SolarPanel() });
    CollectionAssert.AreEqual(new List<string> { SystemTags.PowerGen }, tags);
  }

  [TestMethod]
  public void Battery_TagsPowerStore() {
    var tags = SystemTags.For(new VirtualComponent[] { new Battery() });
    CollectionAssert.AreEqual(new List<string> { SystemTags.PowerStore }, tags);
  }

  [TestMethod]
  public void Light_TagsPowerConsume() {
    var tags = SystemTags.For(new VirtualComponent[] { new Light() });
    CollectionAssert.AreEqual(new List<string> { SystemTags.PowerConsume }, tags);
  }

  [TestMethod]
  public void ReactionWheel_TagsConsumeAndAttitude() {
    var tags = SystemTags.For(new VirtualComponent[] { new ReactionWheel() });
    CollectionAssert.AreEqual(
      new List<string> { SystemTags.PowerConsume, SystemTags.Attitude },
      tags);
  }

  [TestMethod]
  public void EngineWithoutAlternator_TagsPropulsionOnly() {
    var engine = new Engine { AlternatorRate = 0 };
    var tags = SystemTags.For(new VirtualComponent[] { engine });
    CollectionAssert.AreEqual(new List<string> { SystemTags.Propulsion }, tags);
  }

  [TestMethod]
  public void EngineWithAlternator_TagsPropulsionAndPowerGen() {
    var engine = new Engine { AlternatorRate = 1.5 };
    var tags = SystemTags.For(new VirtualComponent[] { engine });
    // Power-gen sorts before propulsion in canonical order.
    CollectionAssert.AreEqual(
      new List<string> { SystemTags.PowerGen, SystemTags.Propulsion },
      tags);
  }

  [TestMethod]
  public void MixedComponents_DedupeAndOrderDeterministically() {
    // A pod-like part with battery + reaction wheel + light + solar.
    // Expected canonical order: power-gen, power-consume, power-store, attitude.
    var components = new VirtualComponent[] {
      new Light(),
      new Battery(),
      new ReactionWheel(),
      new SolarPanel(),
    };
    var tags = SystemTags.For(components);
    CollectionAssert.AreEqual(
      new List<string> {
        SystemTags.PowerGen,
        SystemTags.PowerConsume,
        SystemTags.PowerStore,
        SystemTags.Attitude,
      },
      tags);
  }

  [TestMethod]
  public void DuplicateComponents_ProduceSingleTag() {
    var components = new VirtualComponent[] {
      new Battery(),
      new Battery(),
      new Battery(),
    };
    var tags = SystemTags.For(components);
    CollectionAssert.AreEqual(new List<string> { SystemTags.PowerStore }, tags);
  }
}
