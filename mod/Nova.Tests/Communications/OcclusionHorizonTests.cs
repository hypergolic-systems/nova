using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nova.Core.Communications;
using Nova.Core.Components.Communications;
using Nova.Core.Utils;

namespace Nova.Tests.Communications;

// End-to-end occlusion tests through CommunicationsNetwork: BuildGraph
// computes Blocked + zeroes RateBps; HorizonForLink folds the
// occlusion-transition UT into NextEventUT; AnyLinkBucketDifference
// catches transitions for off-rails endpoints.
[TestClass]
public class OcclusionHorizonTests {

  private static Antenna AlwaysAboveKnee() => new() {
    // RefDistance >> any test distance → ratio >> 1 → bucket pinned
    // at N. Keeps the bucket horizon at the cap so occlusion is the
    // only event the harness can find.
    TxPower = 1, Gain = 1, MaxRate = 1000, RefDistance = 100,
  };

  [TestMethod]
  public void Static_ChordPassingThroughBody_LinkBlocked_RateZero() {
    var body = new Body { Id = "Body", Mu = 1, Radius = 1 };
    var net = new CommunicationsNetwork();
    var ant = AlwaysAboveKnee();

    var a = new Endpoint {
      Id = "A", PrimaryBody = body,
      PositionAt = _ => new Vec3d(2, 0, 0),
    };
    a.Antennas.Add(ant);
    var b = new Endpoint {
      Id = "B", PrimaryBody = body,
      PositionAt = _ => new Vec3d(-2, 0, 0),
    };
    b.Antennas.Add(ant);
    net.AddEndpoint(a); net.AddEndpoint(b);

    var graph = net.Solve(0);
    var ab = graph.Links.First(l => l.From.Id == "A" && l.To.Id == "B");
    Assert.IsTrue(ab.Blocked, "chord through body centre should be blocked");
    Assert.AreEqual(0, ab.RateBps, 1e-6);
  }

  [TestMethod]
  public void Static_ChordSameSideOfBody_LinkClear() {
    var body = new Body { Id = "Body", Mu = 1, Radius = 1 };
    var net = new CommunicationsNetwork();
    var ant = AlwaysAboveKnee();

    var a = new Endpoint {
      Id = "A", PrimaryBody = body,
      PositionAt = _ => new Vec3d(2, 0, 0),
    };
    a.Antennas.Add(ant);
    var b = new Endpoint {
      Id = "B", PrimaryBody = body,
      PositionAt = _ => new Vec3d(5, 0, 0),
    };
    b.Antennas.Add(ant);
    net.AddEndpoint(a); net.AddEndpoint(b);

    var graph = net.Solve(0);
    var ab = graph.Links.First(l => l.From.Id == "A" && l.To.Id == "B");
    Assert.IsFalse(ab.Blocked);
    Assert.IsTrue(ab.RateBps > 0);
  }

  [TestMethod]
  public void Horizon_FoldsOcclusionEnter_IntoNextEventUT() {
    // Body radius 1 at origin. A stationary at (0, 5, 0). B moves
    // along the x-axis at 0.001 m/s: B(ut) = (ut·0.001 − 5, 0, 0).
    // With bx = ut·0.001 − 5, the chord is blocked iff |bx| <
    // sqrt(25/24) ≈ 1.0206 — derivation: foot of perp from origin
    // to chord is on the segment for all ut, with squared distance
    // 25·bx²/(bx²+25); distance < 1 ⇔ bx² < 25/24. The occlusion
    // window in UT is therefore (3979.4, 6020.6) — about 2041s
    // wide, several coarse-sweep steps (default step ≈432s).
    //
    // At UT=0 the link is clear; the next event is the occlusion
    // enter at ut ≈ 3979.4. Distance over the entire horizon stays
    // below the antenna's RefDistance (100), so the bucket sits
    // pinned at N and does not preempt the horizon.
    var body = new Body { Id = "Body", Mu = 1, Radius = 1 };
    var net = new CommunicationsNetwork();
    var ant = AlwaysAboveKnee();

    var a = new Endpoint {
      Id = "A", PrimaryBody = body,
      PositionAt = _ => new Vec3d(0, 5, 0),
    };
    a.Antennas.Add(ant);
    var b = new Endpoint {
      Id = "B", PrimaryBody = body,
      PositionAt = ut => new Vec3d(ut * 0.001 - 5, 0, 0),
    };
    b.Antennas.Add(ant);
    net.AddEndpoint(a); net.AddEndpoint(b);

    var graph = net.Solve(0);
    var ab = graph.Links.First(l => l.From.Id == "A" && l.To.Id == "B");
    Assert.IsFalse(ab.Blocked);
    var expected = (5 - Math.Sqrt(25.0 / 24)) * 1000;
    var threshold = CommunicationsParameters.MaxHorizonSeconds * 1e-6;
    Assert.AreEqual(expected, ab.NextEventUT, threshold,
        $"NextEventUT should fold in occlusion enter at {expected:F4}");
  }

  [TestMethod]
  public void OffRails_OcclusionEnter_AnyLinkBucketDifferenceFires() {
    // Same geometry, but with IsPredictable=false on both endpoints
    // → bisection is suppressed, NextEventUT is pinned at the
    // horizon cap. The reactive watch is the only thing that can
    // catch the transition. At UT=5 the chord passes through the
    // body centre; effective rate must be 0 vs cached nonzero.
    var body = new Body { Id = "Body", Mu = 1, Radius = 1 };
    var net = new CommunicationsNetwork();
    var ant = AlwaysAboveKnee();

    var a = new Endpoint {
      Id = "A", PrimaryBody = body,
      PositionAt = _ => new Vec3d(0, 5, 0),
      IsPredictable = false,
    };
    a.Antennas.Add(ant);
    var b = new Endpoint {
      Id = "B", PrimaryBody = body,
      PositionAt = ut => new Vec3d(ut - 5, 0, 0),
      IsPredictable = false,
    };
    b.Antennas.Add(ant);
    net.AddEndpoint(a); net.AddEndpoint(b);

    var initial = net.Solve(0);
    var ab0 = initial.Links.First(l => l.From.Id == "A" && l.To.Id == "B");
    Assert.IsFalse(ab0.Blocked);
    Assert.IsTrue(ab0.RateBps > 0);

    Assert.IsFalse(net.AnyLinkBucketDifference(a, 0),
        "no change yet at UT=0");
    Assert.IsTrue(net.AnyLinkBucketDifference(a, 5),
        "mid-occlusion: effective rate flips to 0 vs cached nonzero");
  }

  [TestMethod]
  public void Resolve_AfterOcclusion_LinkClearsAndRateRecovers() {
    // Walk the same scenario across the occlusion window: solve
    // before, during, and after. Effective state should match the
    // geometry at each step.
    var body = new Body { Id = "Body", Mu = 1, Radius = 1 };
    var net = new CommunicationsNetwork();
    var ant = AlwaysAboveKnee();

    var a = new Endpoint {
      Id = "A", PrimaryBody = body,
      PositionAt = _ => new Vec3d(0, 5, 0),
    };
    a.Antennas.Add(ant);
    var b = new Endpoint {
      Id = "B", PrimaryBody = body,
      PositionAt = ut => new Vec3d(ut - 5, 0, 0),
    };
    b.Antennas.Add(ant);
    net.AddEndpoint(a); net.AddEndpoint(b);

    var g0 = net.Solve(0);
    Assert.IsFalse(g0.Links.First(l => l.From.Id == "A" && l.To.Id == "B").Blocked);

    var g5 = net.Solve(5);
    Assert.IsTrue(g5.Links.First(l => l.From.Id == "A" && l.To.Id == "B").Blocked,
        "chord through origin at UT=5 should be blocked");

    var g10 = net.Solve(10);
    Assert.IsFalse(g10.Links.First(l => l.From.Id == "A" && l.To.Id == "B").Blocked,
        "past occlusion exit at ≈6.02; should be clear at UT=10");
  }
}
