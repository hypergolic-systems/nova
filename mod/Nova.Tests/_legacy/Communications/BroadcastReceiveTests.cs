using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nova.Core.Communications;
using Nova.Core.Components.Communications;
using Nova.Core.Utils;

namespace Nova.Tests.Communications;

[TestClass]
public class BroadcastReceiveTests {

  private static Antenna Flat(double maxRate = 1000) => new() {
    TxPower = 1, Gain = 1, MaxRate = maxRate, RefDistance = 1e6,
  };

  private static Endpoint At(string id, Vec3d pos, params Antenna[] antennas) {
    var ep = new Endpoint { Id = id, PositionAt = _ => pos };
    ep.Antennas.AddRange(antennas);
    return ep;
  }

  [TestMethod]
  public void Broadcast_NoReceivers_NoTransmission() {
    var net = new CommunicationsNetwork();
    var src = At("src", Vec3d.Zero, Flat());
    net.AddEndpoint(src);

    var b = new Broadcast<string>(src, "telemetry", targetRateBps: 500);
    net.Submit(b);
    net.Solve(0);
    net.Solve(10);

    Assert.AreEqual(0, b.AllocatedRateBps);
    Assert.AreEqual(0, b.BytesSent);
  }

  [TestMethod]
  public void Broadcast_SingleReceiver_WillingnessBelowTarget_PushesWillingness() {
    var net = new CommunicationsNetwork();
    var src = At("src", Vec3d.Zero, Flat());
    var rx = At("rx", new Vec3d(10, 0, 0), Flat());
    net.AddEndpoint(src); net.AddEndpoint(rx);

    var b = new Broadcast<string>(src, "telemetry", targetRateBps: 500);
    var r = new Receive<string>(rx, "telemetry", maxRateBps: 100);
    net.Submit(b); net.Submit(r);

    net.Solve(0);
    Assert.AreEqual(100, b.AllocatedRateBps, 1e-6);
    Assert.AreEqual(100, r.AllocatedRateBps, 1e-6);
  }

  [TestMethod]
  public void Broadcast_SingleReceiver_WillingnessAboveTarget_PushesTarget() {
    var net = new CommunicationsNetwork();
    var src = At("src", Vec3d.Zero, Flat());
    var rx = At("rx", new Vec3d(10, 0, 0), Flat());
    net.AddEndpoint(src); net.AddEndpoint(rx);

    var b = new Broadcast<string>(src, "telemetry", targetRateBps: 200);
    var r = new Receive<string>(rx, "telemetry", maxRateBps: 1000);
    net.Submit(b); net.Submit(r);

    net.Solve(0);
    Assert.AreEqual(200, b.AllocatedRateBps, 1e-6);
    Assert.AreEqual(200, r.AllocatedRateBps, 1e-6);
  }

  [TestMethod]
  public void Broadcast_TwoReceivers_FairSplitOfTarget() {
    var net = new CommunicationsNetwork();
    var src = At("src", Vec3d.Zero, Flat());
    var r1 = At("r1", new Vec3d(10, 0, 0), Flat());
    var r2 = At("r2", new Vec3d(0, 10, 0), Flat());
    net.AddEndpoint(src); net.AddEndpoint(r1); net.AddEndpoint(r2);

    var b = new Broadcast<string>(src, "telemetry", targetRateBps: 300);
    var rx1 = new Receive<string>(r1, "telemetry", maxRateBps: 1000);
    var rx2 = new Receive<string>(r2, "telemetry", maxRateBps: 1000);
    net.Submit(b); net.Submit(rx1); net.Submit(rx2);

    net.Solve(0);
    Assert.AreEqual(150, rx1.AllocatedRateBps, 1e-6);
    Assert.AreEqual(150, rx2.AllocatedRateBps, 1e-6);
    Assert.AreEqual(300, b.AllocatedRateBps, 1e-6);
  }

  [TestMethod]
  public void Broadcast_TwoReceivers_OneCappedLow_OtherClaimsRest() {
    // Max-min: cap-limited receiver gets its cap; remaining target
    // budget goes to the other (up to its own ceiling).
    var net = new CommunicationsNetwork();
    var src = At("src", Vec3d.Zero, Flat());
    var r1 = At("r1", new Vec3d(10, 0, 0), Flat());
    var r2 = At("r2", new Vec3d(0, 10, 0), Flat());
    net.AddEndpoint(src); net.AddEndpoint(r1); net.AddEndpoint(r2);

    var b = new Broadcast<string>(src, "telemetry", targetRateBps: 1000);
    var rx1 = new Receive<string>(r1, "telemetry", maxRateBps: 100);
    var rx2 = new Receive<string>(r2, "telemetry", maxRateBps: 1000);
    net.Submit(b); net.Submit(rx1); net.Submit(rx2);

    net.Solve(0);
    Assert.AreEqual(100, rx1.AllocatedRateBps, 1e-6);
    Assert.AreEqual(900, rx2.AllocatedRateBps, 1e-6);
    Assert.AreEqual(1000, b.AllocatedRateBps, 1e-6);
  }

  [TestMethod]
  public void Broadcast_DifferentKeys_DontMatch() {
    var net = new CommunicationsNetwork();
    var src = At("src", Vec3d.Zero, Flat());
    var rx = At("rx", new Vec3d(10, 0, 0), Flat());
    net.AddEndpoint(src); net.AddEndpoint(rx);

    var b = new Broadcast<string>(src, "telemetry", 500);
    var r = new Receive<string>(rx, "science", 500);
    net.Submit(b); net.Submit(r);

    net.Solve(0);
    Assert.AreEqual(0, b.AllocatedRateBps);
    Assert.AreEqual(0, r.AllocatedRateBps);
  }

  [TestMethod]
  public void Broadcast_DifferentKeyTypes_DontMatch() {
    var net = new CommunicationsNetwork();
    var src = At("src", Vec3d.Zero, Flat());
    var rx = At("rx", new Vec3d(10, 0, 0), Flat());
    net.AddEndpoint(src); net.AddEndpoint(rx);

    var b = new Broadcast<int>(src, 5, 500);
    var r = new Receive<string>(rx, "5", 500);
    net.Submit(b); net.Submit(r);

    net.Solve(0);
    Assert.AreEqual(0, b.AllocatedRateBps);
    Assert.AreEqual(0, r.AllocatedRateBps);
  }

  [TestMethod]
  public void TwoBroadcasts_SameKey_BothFeedReceiver() {
    var net = new CommunicationsNetwork();
    var s1 = At("s1", Vec3d.Zero, Flat());
    var s2 = At("s2", new Vec3d(0, 10, 0), Flat());
    var rx = At("rx", new Vec3d(10, 0, 0), Flat());
    net.AddEndpoint(s1); net.AddEndpoint(s2); net.AddEndpoint(rx);

    var b1 = new Broadcast<string>(s1, "weather", 100);
    var b2 = new Broadcast<string>(s2, "weather", 200);
    var r = new Receive<string>(rx, "weather", maxRateBps: 1000);
    net.Submit(b1); net.Submit(b2); net.Submit(r);

    net.Solve(0);
    // Each broadcast's source-budget caps its push; receiver willing
    // to take both (cap 1000 ≥ 100 + 200). Receiver gets both streams.
    Assert.AreEqual(100, b1.AllocatedRateBps, 1e-6);
    Assert.AreEqual(200, b2.AllocatedRateBps, 1e-6);
    Assert.AreEqual(300, r.AllocatedRateBps, 1e-6);
  }

  [TestMethod]
  public void Cancel_Broadcast_StopsTransmission() {
    var net = new CommunicationsNetwork();
    var src = At("src", Vec3d.Zero, Flat());
    var rx = At("rx", new Vec3d(10, 0, 0), Flat());
    net.AddEndpoint(src); net.AddEndpoint(rx);

    var b = new Broadcast<string>(src, "k", 200);
    var r = new Receive<string>(rx, "k", 1000);
    net.Submit(b); net.Submit(r);

    net.Solve(0);
    net.Solve(5); // 200 × 5 = 1000 bytes
    Assert.AreEqual(1000, r.BytesReceived);

    net.Cancel(b);
    net.Solve(10);

    Assert.AreEqual(JobStatus.Cancelled, b.Status);
    Assert.AreEqual(0, r.AllocatedRateBps);
    Assert.AreEqual(1000, r.BytesReceived); // unchanged after cancellation
  }

  [TestMethod]
  public void Receive_ZeroWillingness_BroadcastIdle() {
    var net = new CommunicationsNetwork();
    var src = At("src", Vec3d.Zero, Flat());
    var rx = At("rx", new Vec3d(10, 0, 0), Flat());
    net.AddEndpoint(src); net.AddEndpoint(rx);

    var b = new Broadcast<string>(src, "k", 500);
    var r = new Receive<string>(rx, "k", 0);
    net.Submit(b); net.Submit(r);

    net.Solve(0);
    Assert.AreEqual(0, b.AllocatedRateBps);
    Assert.AreEqual(0, r.AllocatedRateBps);
  }

  [TestMethod]
  public void Broadcast_Integrates_BytesSent() {
    var net = new CommunicationsNetwork();
    var src = At("src", Vec3d.Zero, Flat());
    var rx = At("rx", new Vec3d(10, 0, 0), Flat());
    net.AddEndpoint(src); net.AddEndpoint(rx);

    var b = new Broadcast<string>(src, "k", 50);
    var r = new Receive<string>(rx, "k", 1000);
    net.Submit(b); net.Submit(r);

    net.Solve(0);
    net.Solve(7);

    Assert.AreEqual(350, b.BytesSent);
    Assert.AreEqual(350, r.BytesReceived);
  }
}
