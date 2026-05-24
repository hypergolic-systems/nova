using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nova.Core.Communications;
using Nova.Core.Components.Communications;
using Nova.Core.Utils;

namespace Nova.Tests.Communications;

[TestClass]
public class MaxRatePathTests {

  private static Antenna Std() => new() {
    TxPower = 100, Gain = 10, MaxRate = 1000, RefDistance = 10,
  };

  private static Endpoint At(string id, Vec3d pos, params Antenna[] antennas) {
    var ep = new Endpoint { Id = id, PositionAt = _ => pos };
    ep.Antennas.AddRange(antennas);
    return ep;
  }

  [TestMethod]
  public void Find_EmptyGraph_ReturnsNull() {
    var source = new Endpoint { Id = "src" };
    var dest = new Endpoint { Id = "dst" };
    var path = MaxRatePath.Find(GraphSnapshot.Empty, source, dest);
    Assert.IsNull(path);
  }

  [TestMethod]
  public void Find_SourceEqualsDest_ReturnsNull() {
    var net = new CommunicationsNetwork();
    var a = At("A", Vec3d.Zero, Std());
    net.AddEndpoint(a);
    var g = net.Solve(0);
    Assert.IsNull(MaxRatePath.Find(g, a, a));
  }

  [TestMethod]
  public void Find_DirectLink_SingleHop() {
    var net = new CommunicationsNetwork();
    var a = At("A", Vec3d.Zero, Std());
    var b = At("B", new Vec3d(20, 0, 0), Std());
    net.AddEndpoint(a);
    net.AddEndpoint(b);

    var g = net.Solve(0);
    var path = MaxRatePath.Find(g, a, b);

    Assert.IsNotNull(path);
    Assert.AreEqual(1, path.Count);
    Assert.AreEqual("A", path[0].From.Id);
    Assert.AreEqual("B", path[0].To.Id);
  }

  [TestMethod]
  public void Find_RelayPreferredOverWeakDirect() {
    // Direct A→C edge exists but at a much weaker rate than the
    // two-hop A→B→C route. Max-bottleneck picks the relayed path.
    var net = new CommunicationsNetwork();
    var a = At("A", Vec3d.Zero, Std());
    var b = At("B", new Vec3d(20, 0, 0), Std());
    var c = At("C", new Vec3d(40, 0, 0), Std());
    net.AddEndpoint(a);
    net.AddEndpoint(b);
    net.AddEndpoint(c);

    var g = net.Solve(0);
    var direct = g.Links.First(l => l.From.Id == "A" && l.To.Id == "C");
    var relayHop = g.Links.First(l => l.From.Id == "A" && l.To.Id == "B");
    Assert.IsTrue(relayHop.RateBps > direct.RateBps,
      $"setup invariant: direct {direct.RateBps} should be weaker than relay-hop {relayHop.RateBps}");

    var path = MaxRatePath.Find(g, a, c);

    Assert.IsNotNull(path);
    Assert.AreEqual(2, path.Count);
    Assert.AreEqual("A", path[0].From.Id);
    Assert.AreEqual("B", path[0].To.Id);
    Assert.AreEqual("B", path[1].From.Id);
    Assert.AreEqual("C", path[1].To.Id);
  }

  [TestMethod]
  public void Find_BlockedLinkRoutesAround() {
    // A→C is blocked (RateBps = 0 simulating occlusion); the only
    // positive-rate route is A→B→C. MaxRatePath's RateBps>0 filter
    // drops the dead edge and finds the alternative.
    var a = At("A", Vec3d.Zero, Std());
    var b = At("B", new Vec3d(20, 0, 0), Std());
    var c = At("C", new Vec3d(40, 0, 0), Std());

    var ab = new Link(a, b, 20, 1, 100);
    var bc = new Link(b, c, 20, 1, 100);
    var ac = new Link(a, c, 40, 0.1, 0);  // RateBps == 0: filtered by Find

    var g = new GraphSnapshot(new[] { ab, bc, ac }, 0);
    var path = MaxRatePath.Find(g, a, c);

    Assert.IsNotNull(path);
    Assert.AreEqual(2, path.Count);
    Assert.AreEqual("A", path[0].From.Id);
    Assert.AreEqual("B", path[0].To.Id);
    Assert.AreEqual("B", path[1].From.Id);
    Assert.AreEqual("C", path[1].To.Id);
  }
}
