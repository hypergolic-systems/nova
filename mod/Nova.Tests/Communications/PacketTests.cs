using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nova.Core.Communications;
using Nova.Core.Components.Communications;
using Nova.Core.Utils;

namespace Nova.Tests.Communications;

[TestClass]
public class PacketTests {

  // FlatAntenna: tuned so any distance up to ~RefDistance delivers
  // exactly MaxRate. Lets allocation tests assert against integer
  // capacities without Shannon-scaling arithmetic.
  private static Antenna Flat(double maxRate = 1000) => new() {
    TxPower = 1, Gain = 1, MaxRate = maxRate, RefDistance = 1e6,
  };

  private static Endpoint At(string id, Vec3d pos, params Antenna[] antennas) {
    var ep = new Endpoint { Id = id, PositionAt = _ => pos };
    ep.Antennas.AddRange(antennas);
    return ep;
  }

  [TestMethod]
  public void Submit_ThenSolve_AllocatesFullEdgeBandwidth() {
    var net = new CommunicationsNetwork();
    var a = At("A", Vec3d.Zero, Flat(500));
    var b = At("B", new Vec3d(10, 0, 0), Flat(500));
    net.AddEndpoint(a);
    net.AddEndpoint(b);

    var p = new Packet(a, b, totalBytes: 10_000);
    net.Submit(p);

    net.Solve(0);
    Assert.AreEqual(JobStatus.Active, p.Status);
    Assert.AreEqual(500, p.AllocatedRateBps, 1e-6);
  }

  [TestMethod]
  public void Solve_AcrossDt_DeliversBytes() {
    var net = new CommunicationsNetwork();
    var a = At("A", Vec3d.Zero, Flat(100));
    var b = At("B", new Vec3d(10, 0, 0), Flat(100));
    net.AddEndpoint(a);
    net.AddEndpoint(b);

    var p = new Packet(a, b, totalBytes: 10_000);
    net.Submit(p);

    net.Solve(0);
    net.Solve(5);

    Assert.AreEqual(500, p.DeliveredBytes); // 100 bps × 5 s
    Assert.AreEqual(JobStatus.Active, p.Status);
  }

  [TestMethod]
  public void Packet_Completes_WhenAllBytesDelivered() {
    var net = new CommunicationsNetwork();
    var a = At("A", Vec3d.Zero, Flat(100));
    var b = At("B", new Vec3d(10, 0, 0), Flat(100));
    net.AddEndpoint(a);
    net.AddEndpoint(b);

    var p = new Packet(a, b, totalBytes: 1000);
    net.Submit(p);

    net.Solve(0);
    net.Solve(20); // 100 bps × 20 s = 2000 bytes worth, capped at 1000

    Assert.AreEqual(JobStatus.Completed, p.Status);
    Assert.AreEqual(1000, p.DeliveredBytes);
    Assert.AreEqual(0, p.RemainingBytes);
    Assert.AreEqual(0, p.AllocatedRateBps);
  }

  [TestMethod]
  public void Cancel_Packet_StopsAccruingBytes() {
    var net = new CommunicationsNetwork();
    var a = At("A", Vec3d.Zero, Flat(100));
    var b = At("B", new Vec3d(10, 0, 0), Flat(100));
    net.AddEndpoint(a);
    net.AddEndpoint(b);

    var p = new Packet(a, b, totalBytes: 10_000);
    net.Submit(p);

    net.Solve(0);
    net.Solve(5);
    Assert.AreEqual(500, p.DeliveredBytes);

    Assert.IsTrue(net.Cancel(p));
    net.Solve(10);

    Assert.AreEqual(JobStatus.Cancelled, p.Status);
    Assert.AreEqual(500, p.DeliveredBytes);
    Assert.AreEqual(0, p.AllocatedRateBps);
  }

  [TestMethod]
  public void Cancel_AlreadyCancelled_ReturnsFalse() {
    var net = new CommunicationsNetwork();
    var a = At("A", Vec3d.Zero, Flat()); var b = At("B", new Vec3d(10, 0, 0), Flat());
    net.AddEndpoint(a); net.AddEndpoint(b);

    var p = new Packet(a, b, 1000);
    net.Submit(p);
    Assert.IsTrue(net.Cancel(p));
    Assert.IsFalse(net.Cancel(p));
  }

  [TestMethod]
  [ExpectedException(typeof(InvalidOperationException))]
  public void Submit_Packet_WithUnregisteredDestination_Throws() {
    var net = new CommunicationsNetwork();
    var a = At("A", Vec3d.Zero, Flat());
    var b = At("B", new Vec3d(10, 0, 0), Flat());
    net.AddEndpoint(a);
    // b is NOT registered.

    net.Submit(new Packet(a, b, 1000));
  }

  [TestMethod]
  [ExpectedException(typeof(InvalidOperationException))]
  public void Submit_Packet_WithUnregisteredSource_Throws() {
    var net = new CommunicationsNetwork();
    var a = At("A", Vec3d.Zero, Flat());
    var b = At("B", new Vec3d(10, 0, 0), Flat());
    net.AddEndpoint(b);
    // a is NOT registered.

    net.Submit(new Packet(a, b, 1000));
  }

  [TestMethod]
  public void Packet_ToEndpointWithoutAntennas_GetsZeroRate() {
    var net = new CommunicationsNetwork();
    var a = At("A", Vec3d.Zero, Flat());
    var silent = new Endpoint { Id = "silent", PositionAt = _ => new Vec3d(10, 0, 0) };
    net.AddEndpoint(a);
    net.AddEndpoint(silent);

    var p = new Packet(a, silent, 1000);
    net.Submit(p);
    net.Solve(0);

    Assert.AreEqual(0, p.AllocatedRateBps);
    Assert.AreEqual(0, p.DeliveredBytes);
  }

  [TestMethod]
  public void MaxTickDt_ForecastsCompletion() {
    var net = new CommunicationsNetwork();
    var a = At("A", Vec3d.Zero, Flat(200));
    var b = At("B", new Vec3d(10, 0, 0), Flat(200));
    net.AddEndpoint(a); net.AddEndpoint(b);

    var p = new Packet(a, b, totalBytes: 1000);
    net.Submit(p);
    net.Solve(0);

    // 1000 bytes at 200 bps → 5 s.
    Assert.AreEqual(5.0, net.MaxTickDt(), 1e-9);
  }

  [TestMethod]
  public void MaxTickDt_PacketsAtDifferentRates_ReturnsEarliestCompletion() {
    var net = new CommunicationsNetwork();
    var a = At("A", Vec3d.Zero, Flat(1000));
    var b = At("B", new Vec3d(10, 0, 0), Flat(1000));
    var c = At("C", new Vec3d(0, 10, 0), Flat(1000));
    net.AddEndpoint(a); net.AddEndpoint(b); net.AddEndpoint(c);

    // A→B fully owns its edge (1000 bps); A→C fully owns its edge (1000 bps).
    // Different total bytes → different completion times.
    var pSlow = new Packet(a, b, 5000); // 5 s
    var pFast = new Packet(a, c, 1000); // 1 s
    net.Submit(pSlow); net.Submit(pFast);

    net.Solve(0);
    Assert.AreEqual(1.0, net.MaxTickDt(), 1e-9);
  }

  [TestMethod]
  public void MaxTickDt_NoActivePackets_IsInfinity() {
    var net = new CommunicationsNetwork();
    Assert.AreEqual(double.PositiveInfinity, net.MaxTickDt());
  }

  [TestMethod]
  public void Packet_DriverHonoursMaxTickDt_NoCapacityWaste() {
    // Two packets share an edge. P1 finishes at t=5; P2 alone after.
    // If the driver uses MaxTickDt, the second packet receives full
    // bandwidth from t=5 onwards instead of half-rate "phantom" sharing.
    var net = new CommunicationsNetwork();
    var a = At("A", Vec3d.Zero, Flat(100));
    var b = At("B", new Vec3d(10, 0, 0), Flat(100));
    net.AddEndpoint(a); net.AddEndpoint(b);

    var p1 = new Packet(a, b, 250);  // 5 s at 50 bps (fair share)
    var p2 = new Packet(a, b, 10000);
    net.Submit(p1); net.Submit(p2);

    net.Solve(0);
    Assert.AreEqual(50, p1.AllocatedRateBps, 1e-6);
    Assert.AreEqual(50, p2.AllocatedRateBps, 1e-6);

    var step = net.MaxTickDt();
    Assert.AreEqual(5.0, step, 1e-9);

    net.Solve(step);              // t = 5
    Assert.AreEqual(JobStatus.Completed, p1.Status);
    Assert.AreEqual(250, p2.DeliveredBytes); // p2 got 50 bps × 5 s

    Assert.AreEqual(100, p2.AllocatedRateBps, 1e-6); // now alone on the edge

    net.Solve(step + 5);          // t = 10
    Assert.AreEqual(750, p2.DeliveredBytes); // 250 + 100 × 5
  }
}
