using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nova.Core.Communications;
using Nova.Core.Components.Communications;
using Nova.Core.Utils;

namespace Nova.Tests.Communications;

[TestClass]
public class AllocationTests {

  private static Antenna Flat(double maxRate = 1000) => new() {
    TxPower = 1, Gain = 1, MaxRate = maxRate, RefDistance = 1e6,
  };

  // Narrow-beam-style antenna: full MaxRate within RefDistance, falls
  // off beyond. Used for max-rate-path tests where direct A→C must be
  // measurably worse than the multi-hop route.
  private static Antenna Narrow() => new() {
    TxPower = 100, Gain = 100, MaxRate = 1000, RefDistance = 100,
  };

  private static Endpoint At(string id, Vec3d pos, params Antenna[] antennas) {
    var ep = new Endpoint { Id = id, PositionAt = _ => pos };
    ep.Antennas.AddRange(antennas);
    return ep;
  }

  private static Link FindLink(GraphSnapshot g, string fromId, string toId) {
    return g.Links.First(l => l.From.Id == fromId && l.To.Id == toId);
  }

  [TestMethod]
  public void TwoPacketsSharingEdge_FairSplit() {
    var net = new CommunicationsNetwork();
    var a = At("A", Vec3d.Zero, Flat(100));
    var b = At("B", new Vec3d(10, 0, 0), Flat(100));
    net.AddEndpoint(a); net.AddEndpoint(b);

    var p1 = new Packet(a, b, 100_000);
    var p2 = new Packet(a, b, 100_000);
    net.Submit(p1); net.Submit(p2);

    net.Solve(0);
    Assert.AreEqual(50, p1.AllocatedRateBps, 1e-6);
    Assert.AreEqual(50, p2.AllocatedRateBps, 1e-6);
  }

  [TestMethod]
  public void ThreePacketsSharingEdge_EqualSplit() {
    var net = new CommunicationsNetwork();
    var a = At("A", Vec3d.Zero, Flat(99));
    var b = At("B", new Vec3d(10, 0, 0), Flat(99));
    net.AddEndpoint(a); net.AddEndpoint(b);

    var p1 = new Packet(a, b, 100_000);
    var p2 = new Packet(a, b, 100_000);
    var p3 = new Packet(a, b, 100_000);
    net.Submit(p1); net.Submit(p2); net.Submit(p3);

    net.Solve(0);
    Assert.AreEqual(33, p1.AllocatedRateBps, 1e-6);
    Assert.AreEqual(33, p2.AllocatedRateBps, 1e-6);
    Assert.AreEqual(33, p3.AllocatedRateBps, 1e-6);
  }

  [TestMethod]
  public void EdgeUsage_FilledAfterAllocation() {
    var net = new CommunicationsNetwork();
    var a = At("A", Vec3d.Zero, Flat(100));
    var b = At("B", new Vec3d(10, 0, 0), Flat(100));
    net.AddEndpoint(a); net.AddEndpoint(b);

    var p1 = new Packet(a, b, 100_000);
    var p2 = new Packet(a, b, 100_000);
    net.Submit(p1); net.Submit(p2);
    var g = net.Solve(0);

    var ab = FindLink(g, "A", "B");
    Assert.AreEqual(100, ab.UsedBps, 1e-6);
    var ba = FindLink(g, "B", "A");
    Assert.AreEqual(0, ba.UsedBps); // no reverse-direction flow
  }

  [TestMethod]
  public void MultiHop_PathBeatsSlowDirect() {
    // A→C direct is slow because the distance is past Narrow()'s
    // RefDistance; A→B→C uses two short hops at full MaxRate. The
    // packet's allocated rate should reflect the multi-hop bottleneck.
    var net = new CommunicationsNetwork();
    var a = At("A", Vec3d.Zero, Narrow());
    var b = At("B", new Vec3d(100, 0, 0), Narrow());
    var c = At("C", new Vec3d(200, 0, 0), Narrow());
    net.AddEndpoint(a); net.AddEndpoint(b); net.AddEndpoint(c);

    var g = net.Solve(0);
    var ac = FindLink(g, "A", "C");
    var ab = FindLink(g, "A", "B");
    Assert.IsTrue(ac.RateBps < ab.RateBps,
      $"setup invalid: direct ({ac.RateBps}) should be slower than per-hop ({ab.RateBps})");

    var p = new Packet(a, c, 1_000_000);
    net.Submit(p);
    net.Solve(0);

    // Should match the per-hop full rate (bottleneck of two equal hops),
    // not the slower direct.
    Assert.AreEqual(ab.RateBps, p.AllocatedRateBps, 1e-6);
  }

  [TestMethod]
  public void MultiHop_TwoPacketsCompetingOnSharedHop() {
    // P1: A→C via A→B→C
    // P2: A→D via A→B→D
    // A→B is the shared hop; max-min splits its capacity.
    var net = new CommunicationsNetwork();
    var a = At("A", Vec3d.Zero, Narrow());
    var b = At("B", new Vec3d(100, 0, 0), Narrow());
    var c = At("C", new Vec3d(200, 0, 0), Narrow());
    var d = At("D", new Vec3d(100, 100, 0), Narrow());
    net.AddEndpoint(a); net.AddEndpoint(b); net.AddEndpoint(c); net.AddEndpoint(d);

    var g = net.Solve(0);
    var abRate = FindLink(g, "A", "B").RateBps; // 1000

    var p1 = new Packet(a, c, 1_000_000);
    var p2 = new Packet(a, d, 1_000_000);
    net.Submit(p1); net.Submit(p2);
    net.Solve(0);

    Assert.AreEqual(abRate / 2, p1.AllocatedRateBps, 1e-6);
    Assert.AreEqual(abRate / 2, p2.AllocatedRateBps, 1e-6);
  }

  [TestMethod]
  public void MaxMinFairness_LowCeilingFlow_FreesCapacityForOthers() {
    // Three flows on one edge of capacity 300:
    //   F1 ceiling 50, F2 unlimited, F3 unlimited.
    // F1 saturates at 50; F2 and F3 split the remaining 250 evenly.
    var net = new CommunicationsNetwork();
    var src = At("src", Vec3d.Zero, Flat(300));
    var d1 = At("d1", new Vec3d(10, 0, 0), Flat(300));
    var d2 = At("d2", new Vec3d(0, 10, 0), Flat(300));
    var d3 = At("d3", new Vec3d(0, 0, 10), Flat(300));
    net.AddEndpoint(src); net.AddEndpoint(d1); net.AddEndpoint(d2); net.AddEndpoint(d3);

    // Use Broadcast/Receive to put a per-flow ceiling on F1.
    var b = new Broadcast<string>(src, "fan-out", targetRateBps: 1e9);
    var r1 = new Receive<string>(d1, "fan-out", maxRateBps: 50);
    var r2 = new Receive<string>(d2, "fan-out", maxRateBps: double.PositiveInfinity);
    var r3 = new Receive<string>(d3, "fan-out", maxRateBps: double.PositiveInfinity);
    net.Submit(b); net.Submit(r1); net.Submit(r2); net.Submit(r3);

    // Each receiver hangs off a separate edge; src→r1 capacity 300, etc.
    // No edge sharing, so the only shared resource is the broadcast budget
    // (1e9, effectively infinite for this test). r1 saturates at its
    // ceiling 50; r2/r3 saturate at the source edge capacity 300.
    net.Solve(0);
    Assert.AreEqual(50, r1.AllocatedRateBps, 1e-6);
    Assert.AreEqual(300, r2.AllocatedRateBps, 1e-6);
    Assert.AreEqual(300, r3.AllocatedRateBps, 1e-6);
  }

  [TestMethod]
  public void PacketAndBroadcast_CompeteForSameEdge() {
    var net = new CommunicationsNetwork();
    var a = At("A", Vec3d.Zero, Flat(200));
    var b = At("B", new Vec3d(10, 0, 0), Flat(200));
    net.AddEndpoint(a); net.AddEndpoint(b);

    var packet = new Packet(a, b, 1_000_000);
    var bcast = new Broadcast<string>(a, "live", targetRateBps: 1000);
    var rx = new Receive<string>(b, "live", maxRateBps: 1000);
    net.Submit(packet); net.Submit(bcast); net.Submit(rx);

    net.Solve(0);
    // Two flows on edge A→B: packet (uncapped) + broadcast→rx (cap 1000
    // by both rx and broadcast). Edge cap is 200; max-min gives 100 each.
    Assert.AreEqual(100, packet.AllocatedRateBps, 1e-6);
    Assert.AreEqual(100, bcast.AllocatedRateBps, 1e-6);
    Assert.AreEqual(100, rx.AllocatedRateBps, 1e-6);
  }

  [TestMethod]
  public void NoFlowsActive_GraphLinksHaveZeroUsage() {
    var net = new CommunicationsNetwork();
    var a = At("A", Vec3d.Zero, Flat());
    var b = At("B", new Vec3d(10, 0, 0), Flat());
    net.AddEndpoint(a); net.AddEndpoint(b);

    var g = net.Solve(0);
    Assert.AreEqual(2, g.Links.Count);
    foreach (var l in g.Links) Assert.AreEqual(0, l.UsedBps);
  }

  [TestMethod]
  public void GraphRecomputed_UsedBps_ResetsBetweenSolves() {
    // After cancelling all jobs, a fresh Solve should not carry over
    // stale UsedBps from the previous solve.
    var net = new CommunicationsNetwork();
    var a = At("A", Vec3d.Zero, Flat(100));
    var b = At("B", new Vec3d(10, 0, 0), Flat(100));
    net.AddEndpoint(a); net.AddEndpoint(b);

    var p = new Packet(a, b, 100_000);
    net.Submit(p);
    var g1 = net.Solve(0);
    Assert.AreEqual(100, FindLink(g1, "A", "B").UsedBps, 1e-6);

    net.Cancel(p);
    var g2 = net.Solve(1);
    Assert.AreEqual(0, FindLink(g2, "A", "B").UsedBps);
  }
}
