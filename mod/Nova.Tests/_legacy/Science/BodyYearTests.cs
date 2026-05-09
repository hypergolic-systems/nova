using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nova.Core.Science;

namespace Nova.Tests.Science;

[TestClass]
public class BodyYearTests {

  // Stock-ish topology: Sun is root; Kerbin/Eve orbit Sun; Mun/Minmus
  // orbit Kerbin; Gilly orbits Eve.
  private static (System.Func<string, string>, System.Func<string, double>) Topology() {
    var parents = new Dictionary<string, string> {
      // value=null sentinels are encoded by absence
      { "Kerbin", "Sun" },
      { "Eve",    "Sun" },
      { "Mun",    "Kerbin" },
      { "Minmus", "Kerbin" },
      { "Gilly",  "Eve" },
    };
    var periods = new Dictionary<string, double> {
      { "Sun",    0          }, // root, degenerate
      { "Kerbin", 9_203_545  },
      { "Eve",    5_657_995  },
      { "Mun",    138_984    },
      { "Minmus", 1_077_311  },
      { "Gilly",  388_587    },
    };
    return (
      bn => parents.TryGetValue(bn, out var p) ? p : null,
      bn => periods[bn]
    );
  }

  [TestMethod]
  public void RootPlanet_ReturnsOwnPeriod() {
    var (parent, period) = Topology();
    Assert.AreEqual(9_203_545, BodyYear.For("Kerbin", parent, period), 1);
  }

  [TestMethod]
  public void Moon_WalksToParentPlanet() {
    var (parent, period) = Topology();
    Assert.AreEqual(9_203_545, BodyYear.For("Mun",    parent, period), 1);
    Assert.AreEqual(9_203_545, BodyYear.For("Minmus", parent, period), 1);
  }

  [TestMethod]
  public void EveMoon_WalksToEve() {
    var (parent, period) = Topology();
    Assert.AreEqual(5_657_995, BodyYear.For("Gilly", parent, period), 1);
    Assert.AreEqual(5_657_995, BodyYear.For("Eve",   parent, period), 1);
  }

  [TestMethod]
  public void SunItself_DegenerateReturnsOwn() {
    var (parent, period) = Topology();
    Assert.AreEqual(0, BodyYear.For("Sun", parent, period), 1);
  }
}
