using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nova.Core.Communications;
using Nova.Core.Components.Communications;
using Nova.Core.Utils;

namespace Nova.Tests.Communications;

[TestClass]
public class CommunicationsNetworkTests {

  // Standard antenna for symmetric tests.
  // RefSnr (with N_0=1) = 100·10² / 10² = 100.
  private static Antenna Std() => new() {
    TxPower = 100, Gain = 10, MaxRate = 1000, RefDistance = 10,
  };

  private static Endpoint At(string id, Vec3d pos, params Antenna[] antennas) {
    var ep = new Endpoint { Id = id, PositionAt = _ => pos };
    ep.Antennas.AddRange(antennas);
    return ep;
  }

  private static Link FindLink(GraphSnapshot graph, string fromId, string toId) {
    return graph.Links.First(l => l.From.Id == fromId && l.To.Id == toId);
  }

  [TestMethod]
  public void Solve_TwoEndpoints_SymmetricAntennas_SymmetricRates() {
    var net = new CommunicationsNetwork();
    net.AddEndpoint(At("A", Vec3d.Zero, Std()));
    net.AddEndpoint(At("B", new Vec3d(20, 0, 0), Std()));

    var g = net.Solve(0);

    Assert.AreEqual(2, g.Links.Count);
    var ab = FindLink(g, "A", "B");
    var ba = FindLink(g, "B", "A");
    Assert.AreEqual(20, ab.DistanceM, 1e-9);
    Assert.AreEqual(ab.RateBps, ba.RateBps, 1e-9);
    Assert.AreEqual(ab.Snr, ba.Snr, 1e-9);

    // Hand-calc: SNR = 100·10·10 / 20² = 25. RefSnr = 100. Ratio = log(26)/log(101) ≈ 0.706.
    // Continuous rate ≈ 705.96 bps; quantised to bucket 7 floor = 700 bps.
    var continuousRate = 1000 * Math.Log(26) / Math.Log(101);
    Assert.AreEqual(700, ab.RateBps, 1e-6);
    Assert.AreEqual(7, RateBuckets.BucketIndex(continuousRate, 1000));
    Assert.AreEqual(25, ab.Snr, 1e-9);
  }

  [TestMethod]
  public void Solve_AsymmetricAntennas_CollapseToWeakerDirection() {
    // A has the lower-RefSnr (more generous) antenna; without
    // symmetrisation A→B scales against 100, B→A against 1600 — yielding
    // 800 vs 700 bps. BuildGraph collapses to the weaker direction so
    // the player sees one link bandwidth, matching the gameplay model
    // where you can't command faster than telemetry confirms.
    var net = new CommunicationsNetwork();
    var aAnt = new Antenna { TxPower = 100, Gain = 10, MaxRate = 1000, RefDistance = 10 };
    var bAnt = new Antenna { TxPower = 400, Gain = 20, MaxRate = 1000, RefDistance = 10 };
    net.AddEndpoint(At("A", Vec3d.Zero, aAnt));
    net.AddEndpoint(At("B", new Vec3d(20, 0, 0), bAnt));

    var g = net.Solve(0);
    var ab = FindLink(g, "A", "B");
    var ba = FindLink(g, "B", "A");

    // Both directions report the slower (B→A) bucketed rate.
    Assert.AreEqual(700, ab.RateBps, 1e-6);
    Assert.AreEqual(700, ba.RateBps, 1e-6);
    Assert.AreEqual(ab.RateBps, ba.RateBps, 1e-9);

    // SNR likewise collapses to the smaller of the two (per-direction
    // SNRs are 50 and 200 respectively).
    Assert.AreEqual(50, ab.Snr, 1e-9);
    Assert.AreEqual(50, ba.Snr, 1e-9);
  }

  [TestMethod]
  public void Solve_CloserThanDesign_RateCappedAtHardwareCeiling() {
    // Identical antennas at r << RefDistance: Shannon ratio > 1, capped.
    var net = new CommunicationsNetwork();
    net.AddEndpoint(At("A", Vec3d.Zero, Std()));
    net.AddEndpoint(At("B", new Vec3d(1, 0, 0), Std()));

    var g = net.Solve(0);
    var ab = FindLink(g, "A", "B");

    Assert.AreEqual(1000, ab.RateBps, 1e-9);
    // SNR = 100·10·10 / 1 = 10000 (well above RefSnr = 100), confirming the cap kicked in.
    Assert.IsTrue(ab.Snr > 100);
  }

  [TestMethod]
  public void Solve_MultiAntennaEndpoint_BestPairWins() {
    // A holds a weak antenna (rate ~1) and a strong one (rate ~600);
    // B holds one standard antenna. The directed-edge rate must reflect
    // the strong pair, not be diluted by the weak one.
    var weak = new Antenna { TxPower = 1, Gain = 1, MaxRate = 10, RefDistance = 10 };
    var strong = new Antenna { TxPower = 10000, Gain = 100, MaxRate = 10000, RefDistance = 10 };
    var net = new CommunicationsNetwork();
    net.AddEndpoint(At("A", Vec3d.Zero, weak, strong));
    net.AddEndpoint(At("B", new Vec3d(100, 0, 0), Std()));

    var g = net.Solve(0);
    var ab = FindLink(g, "A", "B");

    // Strong-pair rate is in the hundreds; weak-pair is order-of-1.
    Assert.IsTrue(ab.RateBps > 100, $"expected strong-pair rate > 100, got {ab.RateBps}");

    // Sanity: with only the weak antenna, the rate would collapse.
    var net2 = new CommunicationsNetwork();
    net2.AddEndpoint(At("A", Vec3d.Zero, weak));
    net2.AddEndpoint(At("B", new Vec3d(100, 0, 0), Std()));
    var g2 = net2.Solve(0);
    var weakOnly = FindLink(g2, "A", "B");
    Assert.IsTrue(weakOnly.RateBps < 10);
  }

  [TestMethod]
  public void AddEndpoint_AfterSolve_GraphLazilyResolves() {
    var net = new CommunicationsNetwork();
    net.AddEndpoint(At("A", Vec3d.Zero, Std()));
    net.AddEndpoint(At("B", new Vec3d(20, 0, 0), Std()));

    net.Solve(123);
    Assert.IsFalse(net.NeedsSolve);
    Assert.AreEqual(2, net.Graph.Links.Count);

    net.AddEndpoint(At("C", new Vec3d(0, 20, 0), Std()));
    Assert.IsTrue(net.NeedsSolve);

    // Graph access auto-resolves at the prior UT.
    var g = net.Graph;
    Assert.IsFalse(net.NeedsSolve);
    Assert.AreEqual(123, g.SolvedUt);
    Assert.AreEqual(6, g.Links.Count);
  }

  [TestMethod]
  public void RemoveEndpoint_RemovesAllItsEdges() {
    var net = new CommunicationsNetwork();
    var a = At("A", Vec3d.Zero, Std());
    var b = At("B", new Vec3d(20, 0, 0), Std());
    var c = At("C", new Vec3d(0, 20, 0), Std());
    net.AddEndpoint(a);
    net.AddEndpoint(b);
    net.AddEndpoint(c);

    net.Solve(0);
    Assert.AreEqual(6, net.Graph.Links.Count);

    Assert.IsTrue(net.RemoveEndpoint(b));
    var g = net.Solve(0);
    Assert.AreEqual(2, g.Links.Count);
    Assert.IsTrue(g.Links.All(l => l.From.Id != "B" && l.To.Id != "B"));
  }

  [TestMethod]
  public void RemoveEndpoint_NotPresent_ReturnsFalse_NoInvalidate() {
    var net = new CommunicationsNetwork();
    net.AddEndpoint(At("A", Vec3d.Zero, Std()));
    net.Solve(0);
    Assert.IsFalse(net.NeedsSolve);

    var ghost = At("ghost", Vec3d.Zero, Std());
    Assert.IsFalse(net.RemoveEndpoint(ghost));
    Assert.IsFalse(net.NeedsSolve);
  }

  [TestMethod]
  public void Solve_FivePointStar_AllPairsPresent() {
    var net = new CommunicationsNetwork();
    for (int i = 0; i < 5; i++) {
      var angle = 2 * Math.PI * i / 5;
      net.AddEndpoint(At($"E{i}",
        new Vec3d(50 * Math.Cos(angle), 0, 50 * Math.Sin(angle)),
        Std()));
    }

    var g = net.Solve(0);
    Assert.AreEqual(20, g.Links.Count); // 5 · 4 directed edges
    foreach (var l in g.Links) Assert.AreNotEqual(l.From.Id, l.To.Id);
  }

  [TestMethod]
  public void EndpointWithoutAntennas_HasNoLinks() {
    var net = new CommunicationsNetwork();
    net.AddEndpoint(At("A", Vec3d.Zero, Std()));
    net.AddEndpoint(new Endpoint { Id = "Silent", PositionAt = _ => new Vec3d(20, 0, 0) });

    var g = net.Solve(0);
    Assert.AreEqual(0, g.Links.Count);
  }

  [TestMethod]
  public void Solve_RedundantCall_StableGraph() {
    var net = new CommunicationsNetwork();
    net.AddEndpoint(At("A", Vec3d.Zero, Std()));
    net.AddEndpoint(At("B", new Vec3d(20, 0, 0), Std()));

    var g1 = net.Solve(42);
    var g2 = net.Solve(42);

    Assert.AreEqual(g1.Links.Count, g2.Links.Count);
    Assert.AreEqual(g1.SolvedUt, g2.SolvedUt);
    Assert.AreEqual(g1.Links[0].RateBps, g2.Links[0].RateBps, 1e-12);
    Assert.IsFalse(net.NeedsSolve);
  }
}
