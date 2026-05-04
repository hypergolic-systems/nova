using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nova.Core.Communications;
using Nova.Core.Components.Communications;
using Nova.Core.Utils;

namespace Nova.Tests.Communications;

[TestClass]
public class MotionTests {

  // Antenna whose knee sits at exactly RefDistance: SNR_ref = SNR(at
  // RefDistance) when both endpoints carry an identical antenna.
  // RefDistance = 100 makes the knee land at r = 100, which keeps the
  // arithmetic in tests legible.
  private static Antenna Knee100() => new() {
    TxPower = 1, Gain = 1, MaxRate = 1000, RefDistance = 100,
  };

  // Wide-aperture flat antenna identical to the existing test suite's
  // Flat — used wherever motion tests need above-knee, capacity-bound
  // links.
  private static Antenna Flat(double maxRate = 1000) => new() {
    TxPower = 1, Gain = 1, MaxRate = maxRate, RefDistance = 1e6,
  };

  private static Endpoint EndpointAt(string id, Func<double, Vec3d> positionAt,
      params Antenna[] antennas) {
    var ep = new Endpoint { Id = id, PositionAt = positionAt };
    ep.Antennas.AddRange(antennas);
    return ep;
  }

  private static Link Link(GraphSnapshot g, string fromId, string toId) =>
      g.Links.First(l => l.From.Id == fromId && l.To.Id == toId);

  [TestMethod]
  public void Solve_AtComputedHorizon_LinkLeavesAboveKneeBucket() {
    // Satellite recedes radially from a stationary ground station at
    // 1 m/s starting at r=90. Antennas: identical Knee100, so the knee
    // sits at r=100 — i.e. above-knee until t=10s, then bucket 9.
    var net = new CommunicationsNetwork();
    var ground = EndpointAt("G", Orbits.Stationary(Vec3d.Zero), Knee100());
    var sat    = EndpointAt("S", Orbits.Linear(new Vec3d(90, 0, 0), new Vec3d(1, 0, 0)), Knee100());
    net.AddEndpoint(ground); net.AddEndpoint(sat);

    var g0 = net.Solve(0);
    Assert.AreEqual(1000, Link(g0, "G", "S").RateBps, 1e-6,
        "above-knee link should report full ceiling");

    var dt = net.MaxTickDt();
    Assert.IsTrue(Math.Abs(dt - 10) < 1.0,
        $"horizon should land within 1s of t=10 (knee crossing); got {dt}");

    var g1 = net.Solve(dt);
    Assert.AreEqual(900, Link(g1, "G", "S").RateBps, 1e-6,
        "after crossing knee, link should drop to bucket 9 floor (0.9 · ceiling)");
  }

  [TestMethod]
  public void Allocator_StableBetweenEvents_ConstantRates() {
    // Solve at t=0, then again at half the next-event horizon: per-flow
    // rates must stay constant (rates are bucket-quantised so geometry
    // changes inside the bucket don't move them).
    var net = new CommunicationsNetwork();
    var ground = EndpointAt("G", Orbits.Stationary(Vec3d.Zero), Knee100());
    var sat    = EndpointAt("S", Orbits.Linear(new Vec3d(90, 0, 0), new Vec3d(1, 0, 0)), Knee100());
    net.AddEndpoint(ground); net.AddEndpoint(sat);

    var p = new Packet(ground, sat, 1_000_000);
    net.Submit(p);

    net.Solve(0);
    var rate0 = p.AllocatedRateBps;
    var dt = net.MaxTickDt();

    net.Solve(dt / 2);
    Assert.AreEqual(rate0, p.AllocatedRateBps, 1e-9,
        "rates must not change inside a bucket");
  }

  [TestMethod]
  public void MaxTickDt_GeometryHorizonFolded_WhenSoonerThanPacket() {
    // Geometry crossing at ~10s; a long packet at 1000bps would take
    // 1000s. MaxTickDt should pick the geometry crossing.
    var net = new CommunicationsNetwork();
    var ground = EndpointAt("G", Orbits.Stationary(Vec3d.Zero), Knee100());
    var sat    = EndpointAt("S", Orbits.Linear(new Vec3d(90, 0, 0), new Vec3d(1, 0, 0)), Knee100());
    net.AddEndpoint(ground); net.AddEndpoint(sat);

    var p = new Packet(ground, sat, 1_000_000); // ~1000s at 1000bps
    net.Submit(p);

    net.Solve(0);
    var dt = net.MaxTickDt();
    Assert.IsTrue(dt < 20, $"expected geometry-bound horizon ~10s, got {dt}");
  }

  [TestMethod]
  public void MaxTickDt_PacketHorizonFolded_WhenSoonerThanGeometry() {
    // All-stationary network, packet completes before any geometry
    // horizon. MaxTickDt should equal the packet completion time.
    var net = new CommunicationsNetwork();
    var a = EndpointAt("A", Orbits.Stationary(Vec3d.Zero), Flat(200));
    var b = EndpointAt("B", Orbits.Stationary(new Vec3d(10, 0, 0)), Flat(200));
    net.AddEndpoint(a); net.AddEndpoint(b);

    var p = new Packet(a, b, totalBytes: 1000); // 5s at 200bps
    net.Submit(p);
    net.Solve(0);

    Assert.AreEqual(5.0, net.MaxTickDt(), 1e-9);
  }

  [TestMethod]
  public void StationaryNetwork_GeometryHorizonIsHorizonCap() {
    // Pure-stationary endpoints → every link reports NextEventUT =
    // 0 + MaxHorizonSeconds (no crossing in the search window). That
    // doesn't dominate MaxTickDt unless packets/broadcasts also exceed
    // the horizon — but it should be present on every link.
    var net = new CommunicationsNetwork();
    var a = EndpointAt("A", Orbits.Stationary(Vec3d.Zero), Flat());
    var b = EndpointAt("B", Orbits.Stationary(new Vec3d(10, 0, 0)), Flat());
    net.AddEndpoint(a); net.AddEndpoint(b);

    var g = net.Solve(0);
    foreach (var link in g.Links) {
      Assert.AreEqual(CommunicationsParameters.MaxHorizonSeconds,
          link.NextEventUT, 1e-9,
          "stationary link's horizon should fall back to the cap");
    }
  }

  [TestMethod]
  public void TwoCircularSatellites_ProducePeriodicEvents() {
    // Two circular orbits in the same plane: r=1000 (period 1000s) and
    // r=2000 (period 4000s). Inter-satellite distance varies between
    // ~1000 (when aligned) and ~3000 (when opposite). Many bucket
    // crossings happen across one synodic period — the test just
    // confirms multiple distinct events fire across that window, which
    // is the qualitative motion-driven behavior we care about.
    var net = new CommunicationsNetwork();
    var s1 = EndpointAt("S1", Orbits.Circular(radius: 1000, period: 1000),
        new Antenna { TxPower = 1, Gain = 10, MaxRate = 1000, RefDistance = 1500 });
    var s2 = EndpointAt("S2", Orbits.Circular(radius: 2000, period: 4000),
        new Antenna { TxPower = 1, Gain = 10, MaxRate = 1000, RefDistance = 1500 });
    net.AddEndpoint(s1); net.AddEndpoint(s2);

    double t = 0;
    var endTime = 4000.0;          // one full period of the slower orbit
    int events = 0;
    int safety = 1000;
    while (t < endTime && safety-- > 0) {
      net.Solve(t);
      var dt = net.MaxTickDt();
      if (dt > endTime - t) break;
      if (dt < CommunicationsParameters.MaxHorizonSeconds - 1) events++;
      t += Math.Max(dt, 1e-3);
    }
    Assert.IsTrue(events >= 4,
        $"expected multiple geometry-driven events across the synodic window; got {events}");
  }

  [TestMethod]
  public void MultiHop_Reroutes_WhenRelayDropsOut() {
    // A and C are far apart; relay B sits between them with strong
    // antennas. Packet A→C routes via B initially. We then move B
    // far away (linear motion) so its hops drop bucket. After re-Solve,
    // routing falls back to whatever direct A→C path remains.
    var net = new CommunicationsNetwork();
    // Strong relay antennas keep B's hops above-knee at start.
    Antenna Strong() => new() { TxPower = 100, Gain = 100, MaxRate = 1000, RefDistance = 200 };
    var a = EndpointAt("A", Orbits.Stationary(Vec3d.Zero), Strong());
    // C starts at distance 300 — direct A↔C is sub-knee.
    var c = EndpointAt("C", Orbits.Stationary(new Vec3d(300, 0, 0)), Strong());
    // B starts midway, then drifts outward.
    var b = EndpointAt("B",
        Orbits.Linear(new Vec3d(150, 0, 0), new Vec3d(50, 0, 0)),
        Strong());
    net.AddEndpoint(a); net.AddEndpoint(b); net.AddEndpoint(c);

    var p = new Packet(a, c, 10_000_000);
    net.Submit(p);

    net.Solve(0);
    var initialRate = p.AllocatedRateBps;

    // Advance well past B's relay-useful range. With B at x = 150 +
    // 50t, by t=200 B is at x=10150 — far enough that direct A↔C wins
    // again and B's hops have collapsed to low buckets.
    net.Solve(200);
    var laterRate = p.AllocatedRateBps;

    // Sanity: packet still has some allocated rate (direct A↔C is
    // alive even if slower) and either rate or path changed compared
    // to the relay-assisted t=0 state.
    Assert.IsTrue(laterRate > 0, "direct A↔C should remain alive");
    Assert.AreNotEqual(initialRate, laterRate,
        "rate should change as B's relay drops out");
  }

  [TestMethod]
  public void ExistingFlatNetwork_RatesUnchangedByQuantization() {
    // Sanity that pre-quantization tests' core invariants hold: a
    // Flat-antenna pair at distance 10 has rate >> ceiling, so the
    // link sits in the above-knee bucket and reports full ceiling.
    var net = new CommunicationsNetwork();
    var a = EndpointAt("A", Orbits.Stationary(Vec3d.Zero), Flat(500));
    var b = EndpointAt("B", Orbits.Stationary(new Vec3d(10, 0, 0)), Flat(500));
    net.AddEndpoint(a); net.AddEndpoint(b);

    var g = net.Solve(0);
    Assert.AreEqual(500, Link(g, "A", "B").RateBps, 1e-6);
    Assert.AreEqual(500, Link(g, "B", "A").RateBps, 1e-6);
  }

  [TestMethod]
  public void EllipticalOrbiter_LinkDropsThenRecovers() {
    // Eccentric orbit around a stationary ground station. At periapsis
    // the link is fast; at apoapsis it's slow. Between solves the
    // bucket should change at least once (we just count events across
    // one orbital period).
    var net = new CommunicationsNetwork();
    var ground = EndpointAt("G", Orbits.Stationary(Vec3d.Zero),
        new Antenna { TxPower = 1, Gain = 10, MaxRate = 1000, RefDistance = 200 });
    var sat = EndpointAt("S", Orbits.Elliptical(
            semiMajor: 500, eccentricity: 0.6, period: 2000),
        new Antenna { TxPower = 1, Gain = 10, MaxRate = 1000, RefDistance = 200 });
    net.AddEndpoint(ground); net.AddEndpoint(sat);

    double t = 0;
    int events = 0;
    int safety = 500;
    while (t < 2000 && safety-- > 0) {
      net.Solve(t);
      var dt = net.MaxTickDt();
      if (dt < CommunicationsParameters.MaxHorizonSeconds - 1) events++;
      if (dt > 2000 - t) break;
      t += Math.Max(dt, 1e-3);
    }
    Assert.IsTrue(events >= 4,
        $"elliptical orbit should produce several bucket events per period; got {events}");
  }
}
