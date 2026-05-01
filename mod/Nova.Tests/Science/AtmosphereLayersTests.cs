using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nova.Core.Science;

namespace Nova.Tests.Science;

[TestClass]
public class AtmosphereLayersTests {

  private static AtmosphereLayers Kerbin() {
    var l = new AtmosphereLayers();
    l.AddLayer("Kerbin", "troposphere", 18000);
    l.AddLayer("Kerbin", "stratosphere", 45000);
    l.AddLayer("Kerbin", "mesosphere", 70000);
    return l;
  }

  [TestMethod]
  public void GroundLevel_IsTroposphere() {
    Assert.AreEqual("troposphere", Kerbin().LayerAt("Kerbin", 0)?.Name);
  }

  [TestMethod]
  public void Mid_Boundaries() {
    var l = Kerbin();
    // top is exclusive — altitude < top is the layer
    Assert.AreEqual("troposphere", l.LayerAt("Kerbin", 17999.99)?.Name);
    Assert.AreEqual("stratosphere", l.LayerAt("Kerbin", 18000)?.Name);
    Assert.AreEqual("stratosphere", l.LayerAt("Kerbin", 44999.99)?.Name);
    Assert.AreEqual("mesosphere", l.LayerAt("Kerbin", 45000)?.Name);
    Assert.AreEqual("mesosphere", l.LayerAt("Kerbin", 69999.99)?.Name);
  }

  [TestMethod]
  public void AboveAtmosphere_IsNull() {
    Assert.IsNull(Kerbin().LayerAt("Kerbin", 70000));
    Assert.IsNull(Kerbin().LayerAt("Kerbin", 100000));
  }

  [TestMethod]
  public void UnknownBody_IsNull() {
    Assert.IsNull(Kerbin().LayerAt("Mun", 0));
    Assert.IsNull(Kerbin().LayerAt("Mun", 100000));
  }

  [TestMethod]
  public void OutOfOrderInsertion_StillSorted() {
    var l = new AtmosphereLayers();
    l.AddLayer("Kerbin", "mesosphere", 70000);
    l.AddLayer("Kerbin", "troposphere", 18000);
    l.AddLayer("Kerbin", "stratosphere", 45000);
    Assert.AreEqual("troposphere",  l.LayerAt("Kerbin", 1000)?.Name);
    Assert.AreEqual("stratosphere", l.LayerAt("Kerbin", 30000)?.Name);
    Assert.AreEqual("mesosphere",   l.LayerAt("Kerbin", 60000)?.Name);
  }
}
