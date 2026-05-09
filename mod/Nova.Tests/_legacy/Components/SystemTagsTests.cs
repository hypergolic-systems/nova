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
  public void TankVolume_TagsStorage() {
    var tags = SystemTags.For(new VirtualComponent[] { new TankVolume() });
    CollectionAssert.AreEqual(new List<string> { SystemTags.Storage }, tags);
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
  public void Engine_TagsPropulsionOnly() {
    var engine = new Engine();
    var tags = SystemTags.For(new VirtualComponent[] { engine });
    CollectionAssert.AreEqual(new List<string> { SystemTags.Propulsion }, tags);
  }

  [TestMethod]
  public void MixedComponents_DedupeAndOrderDeterministically() {
    // C# tags now omit Battery + SolarPanel + Command + FuelCell —
    // those components moved to Rust (nova-sim). Tag tests for them
    // come back when the Rust-side websocket/UI lands.
    var components = new VirtualComponent[] {
      new Light(),
      new ReactionWheel(),
    };
    var tags = SystemTags.For(components);
    CollectionAssert.AreEqual(
      new List<string> {
        SystemTags.PowerConsume,
        SystemTags.Attitude,
      },
      tags);
  }
}
