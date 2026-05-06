using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nova.Core.Communications;

namespace Nova.Tests.Communications;

// Validates OccluderSet.For against the LCA + penultimate-subtree
// rule: which celestial bodies could potentially block the chord
// between two endpoints, given their PrimaryBody fields and the SOI
// hierarchy populated via Body.Children.
[TestClass]
public class OccluderSetTests {

  // Hand-built stock-shaped tree: Sun → {Moho, Eve→Gilly, Kerbin→{Mun, Minmus}, Duna→Ike, Jool→{Laythe, Vall, Tylo, Bop, Pol}}.
  // Returns the bodies the tests need by name.
  private sealed class System {
    public Body Sun, Moho, Eve, Gilly, Kerbin, Mun, Minmus, Duna, Ike,
                Jool, Laythe, Vall, Tylo, Bop, Pol;
  }

  private static System BuildStockShape() {
    Body Make(string id, Body parent) {
      var b = new Body { Id = id, Mu = 1, Radius = 1, Parent = parent };
      if (parent != null) parent.Children.Add(b);
      return b;
    }
    var sys = new System();
    sys.Sun = Make("Sun", null);
    sys.Moho = Make("Moho", sys.Sun);
    sys.Eve = Make("Eve", sys.Sun);
    sys.Gilly = Make("Gilly", sys.Eve);
    sys.Kerbin = Make("Kerbin", sys.Sun);
    sys.Mun = Make("Mun", sys.Kerbin);
    sys.Minmus = Make("Minmus", sys.Kerbin);
    sys.Duna = Make("Duna", sys.Sun);
    sys.Ike = Make("Ike", sys.Duna);
    sys.Jool = Make("Jool", sys.Sun);
    sys.Laythe = Make("Laythe", sys.Jool);
    sys.Vall = Make("Vall", sys.Jool);
    sys.Tylo = Make("Tylo", sys.Jool);
    sys.Bop = Make("Bop", sys.Jool);
    sys.Pol = Make("Pol", sys.Jool);
    return sys;
  }

  private static Endpoint Ep(string id, Body primary) =>
    new() { Id = id, PrimaryBody = primary };

  [TestMethod]
  public void SameSOI_KSCAndKerbinOrbit_OnlyKerbin() {
    var s = BuildStockShape();
    var set = OccluderSet.For(Ep("KSC", s.Kerbin), Ep("Sat", s.Kerbin));
    CollectionAssert.AreEquivalent(new[] { s.Kerbin }, set.ToArray());
  }

  [TestMethod]
  public void CrossSOI_MunOrbitAndKerbinOrbit_KerbinAndMun() {
    var s = BuildStockShape();
    var set = OccluderSet.For(Ep("MunSat", s.Mun), Ep("KerbinSat", s.Kerbin));
    CollectionAssert.AreEquivalent(new[] { s.Kerbin, s.Mun }, set.ToArray());
  }

  [TestMethod]
  public void SameSOI_TwoMunOrbiters_OnlyMun() {
    var s = BuildStockShape();
    var set = OccluderSet.For(Ep("A", s.Mun), Ep("B", s.Mun));
    CollectionAssert.AreEquivalent(new[] { s.Mun }, set.ToArray());
  }

  [TestMethod]
  public void Interplanetary_KerbinToMoho_KerbinSubtreePlusMohoPlusSun() {
    var s = BuildStockShape();
    var set = OccluderSet.For(Ep("Kerbin", s.Kerbin), Ep("Moho", s.Moho));
    var ids = set.Select(b => b.Id).OrderBy(x => x).ToArray();
    CollectionAssert.AreEquivalent(
      new[] { "Kerbin", "Minmus", "Moho", "Mun", "Sun" }, ids);
  }

  [TestMethod]
  public void Interplanetary_KerbinToMoho_ExcludesEveAndGilly() {
    var s = BuildStockShape();
    var set = OccluderSet.For(Ep("Kerbin", s.Kerbin), Ep("Moho", s.Moho));
    Assert.IsFalse(set.Contains(s.Eve), "Eve is not in either penultimate subtree");
    Assert.IsFalse(set.Contains(s.Gilly), "Gilly is not in either penultimate subtree");
  }

  [TestMethod]
  public void Interplanetary_KerbinToLaythe_TenBodies() {
    var s = BuildStockShape();
    var set = OccluderSet.For(Ep("Kerbin", s.Kerbin), Ep("Laythe", s.Laythe));
    var ids = set.Select(b => b.Id).OrderBy(x => x).ToArray();
    CollectionAssert.AreEquivalent(
      new[] { "Bop", "Jool", "Kerbin", "Laythe", "Minmus", "Mun", "Pol", "Sun", "Tylo", "Vall" },
      ids);
  }

  [TestMethod]
  public void NullPrimaryBody_EitherSide_ReturnsEmpty() {
    var s = BuildStockShape();
    Assert.AreEqual(0, OccluderSet.For(Ep("A", null), Ep("B", s.Kerbin)).Count);
    Assert.AreEqual(0, OccluderSet.For(Ep("A", s.Kerbin), Ep("B", null)).Count);
    Assert.AreEqual(0, OccluderSet.For(Ep("A", null), Ep("B", null)).Count);
  }
}
