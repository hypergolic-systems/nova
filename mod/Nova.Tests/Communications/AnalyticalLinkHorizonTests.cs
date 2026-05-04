using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nova.Core.Communications;
using Nova.Core.Components.Communications;
using Nova.Core.Utils;

namespace Nova.Tests.Communications;

// Equivalence tests: the analytical horizon path (Endpoint.Motion =
// KeplerMotion) must produce the same NextEventUT as the numerical
// path (no Motion, opaque PositionAt closure) to within the bisection
// threshold. Also covers KeplerEvaluator math correctness against an
// independent reference (Orbits.Circular) and dispatcher routing.
[TestClass]
public class AnalyticalLinkHorizonTests {

  // Compact body for tests: Mu chosen so that mean motion at a=1 is 1
  // (i.e. period = 2π for unit semi-major axis), making numerical
  // checks trivial to hand-verify.
  private static Body UnitBody() => new() {
    Id = "Test",
    Mu = 1.0,
    Radius = 0.1,
    RotationPeriod = 86400,
    InitialRotationDeg = 0,
  };

  private static Antenna Knee(double refDist, double maxRate = 1000) => new() {
    TxPower = 1, Gain = 1, MaxRate = maxRate, RefDistance = refDist,
  };

  // Build a circular Kepler orbit and a matching position closure that
  // sources from KeplerEvaluator. Using the same evaluator on both
  // sides isolates the dispatcher equivalence check from any drift
  // between two independent r(t) implementations.
  private static (KeplerMotion, Func<double, Vec3d>) Circular(
      Body parent, double a, double inclinationRad, double lanRad,
      double argPeRad, double meanAnomalyAtEpoch, double epoch = 0) {
    var k = new KeplerMotion {
      Parent = parent,
      SemiMajorAxis = a,
      Eccentricity = 0,
      Inclination = inclinationRad,
      Lan = lanRad,
      ArgPe = argPeRad,
      MeanAnomalyAtEpoch = meanAnomalyAtEpoch,
      Epoch = epoch,
    };
    Func<double, Vec3d> pos = ut => KeplerEvaluator.PositionAt(k, ut);
    return (k, pos);
  }

  [TestMethod]
  public void KeplerEvaluator_EquatorialCircular_MatchesOrbitsCircular() {
    // Circular equatorial orbit at a=1, period=2π. KeplerEvaluator
    // and the test-fixture Orbits.Circular helper should agree on
    // position to floating-point precision.
    var body = UnitBody();
    var (k, _) = Circular(body, a: 1.0, inclinationRad: 0, lanRad: 0,
                          argPeRad: 0, meanAnomalyAtEpoch: 0);
    // Orbits.Circular: phase = ω·ut; here ω = sqrt(Mu/a³) = 1.
    var reference = Orbits.Circular(radius: 1.0, period: 2 * Math.PI);
    for (var ut = 0.0; ut <= 2 * Math.PI; ut += 0.5) {
      var p_eval = KeplerEvaluator.PositionAt(k, ut);
      var p_ref = reference(ut);
      Assert.AreEqual(p_ref.X, p_eval.X, 1e-12, $"X mismatch at ut={ut}");
      Assert.AreEqual(p_ref.Y, p_eval.Y, 1e-12, $"Y mismatch at ut={ut}");
      Assert.AreEqual(p_ref.Z, p_eval.Z, 1e-12, $"Z mismatch at ut={ut}");
    }
  }

  [TestMethod]
  public void KeplerEvaluator_PolarOrbit_PositionsInXZPlane() {
    // 90° inclination, LAN=0 → orbital plane is the XZ plane.
    // At argument-of-latitude=0 the vessel sits at +X (ascending node);
    // at π/2 it's at +Z; at π it's at -X; at 3π/2 it's at -Z.
    var body = UnitBody();
    var (k, _) = Circular(body, a: 1.0, inclinationRad: Math.PI / 2,
                          lanRad: 0, argPeRad: 0, meanAnomalyAtEpoch: 0);
    var p0 = KeplerEvaluator.PositionAt(k, 0);
    var p1 = KeplerEvaluator.PositionAt(k, Math.PI / 2);
    var p2 = KeplerEvaluator.PositionAt(k, Math.PI);
    var p3 = KeplerEvaluator.PositionAt(k, 3 * Math.PI / 2);

    Assert.AreEqual(1.0, p0.X, 1e-12); Assert.AreEqual(0.0, p0.Y, 1e-12); Assert.AreEqual(0.0, p0.Z, 1e-12);
    Assert.AreEqual(0.0, p1.X, 1e-12); Assert.AreEqual(0.0, p1.Y, 1e-12); Assert.AreEqual(1.0, p1.Z, 1e-12);
    Assert.AreEqual(-1.0, p2.X, 1e-12); Assert.AreEqual(0.0, p2.Y, 1e-12); Assert.AreEqual(0.0, p2.Z, 1e-12);
    Assert.AreEqual(0.0, p3.X, 1e-12); Assert.AreEqual(0.0, p3.Y, 1e-12); Assert.AreEqual(-1.0, p3.Z, 1e-12);
  }

  [TestMethod]
  public void KeplerEvaluator_EllipticOrbit_Throws() {
    var body = UnitBody();
    var k = new KeplerMotion {
      Parent = body, SemiMajorAxis = 1, Eccentricity = 0.1,
      Inclination = 0, Lan = 0, ArgPe = 0, MeanAnomalyAtEpoch = 0, Epoch = 0,
    };
    Assert.ThrowsException<InvalidOperationException>(
        () => KeplerEvaluator.PositionAt(k, 0));
  }

  [TestMethod]
  public void Dispatcher_TwoCoplanarCircular_AnalyticalMatchesNumerical() {
    // Two satellites, coplanar circular orbits at different sma and
    // phases. Their relative distance oscillates between |a1-a2| and
    // a1+a2 over the synodic period. With Knee antennas calibrated so
    // bucket boundaries fall inside that range, we expect bucket
    // transitions; analytical and numerical paths must agree on when.
    var body = UnitBody();
    var (kA, posA) = Circular(body, a: 1000, inclinationRad: 0,
                               lanRad: 0, argPeRad: 0, meanAnomalyAtEpoch: 0);
    var (kB, posB) = Circular(body, a: 2000, inclinationRad: 0,
                               lanRad: 0, argPeRad: 0,
                               meanAnomalyAtEpoch: Math.PI / 4);
    var antenna = Knee(refDist: 1500, maxRate: 1000);

    // Run with KeplerMotion attached → analytical path.
    AssertEquivalentEvent(body, kA, posA, kB, posB, antenna, currentUT: 0);
    AssertEquivalentEvent(body, kA, posA, kB, posB, antenna, currentUT: 100);
    AssertEquivalentEvent(body, kA, posA, kB, posB, antenna, currentUT: 1000);
  }

  [TestMethod]
  public void Dispatcher_InclinedCircular_AnalyticalMatchesNumerical() {
    // Same scenario, with inclined orbits (30° and 60°, different LANs).
    // The 3D distance has multi-frequency structure — bisection on
    // closed-form r(t) still works because the search loop doesn't
    // care about the analytical structure of r(t), just its evaluation.
    var body = UnitBody();
    var (kA, posA) = Circular(body, a: 1000, inclinationRad: Math.PI / 6,
                               lanRad: 0, argPeRad: 0, meanAnomalyAtEpoch: 0);
    var (kB, posB) = Circular(body, a: 2000, inclinationRad: Math.PI / 3,
                               lanRad: Math.PI / 4, argPeRad: 0,
                               meanAnomalyAtEpoch: Math.PI / 4);
    var antenna = Knee(refDist: 1500, maxRate: 1000);
    AssertEquivalentEvent(body, kA, posA, kB, posB, antenna, currentUT: 0);
    AssertEquivalentEvent(body, kA, posA, kB, posB, antenna, currentUT: 200);
  }

  [TestMethod]
  public void Dispatcher_EccentricEndpoint_FallsBackToNumerical() {
    // KeplerMotion with eccentricity != 0 is not handled by
    // KeplerEvaluator. Dispatcher must route to numerical.
    // Verified by ensuring no exception is thrown (the analytical
    // path would throw via KeplerEvaluator).
    var body = UnitBody();
    var (_, posA) = Circular(body, a: 1000, inclinationRad: 0,
                              lanRad: 0, argPeRad: 0, meanAnomalyAtEpoch: 0);
    var kA = new KeplerMotion {
      Parent = body, SemiMajorAxis = 1000, Eccentricity = 0.0,  // circular A
      Inclination = 0, Lan = 0, ArgPe = 0, MeanAnomalyAtEpoch = 0, Epoch = 0,
    };
    var kB = new KeplerMotion {
      Parent = body, SemiMajorAxis = 2000, Eccentricity = 0.1,  // eccentric B
      Inclination = 0, Lan = 0, ArgPe = 0, MeanAnomalyAtEpoch = 0, Epoch = 0,
    };
    var antenna = Knee(refDist: 1500, maxRate: 1000);

    var net = new CommunicationsNetwork();
    var epA = new Endpoint { Id = "A", PositionAt = posA, Motion = kA };
    epA.Antennas.Add(antenna);
    var epB = new Endpoint {
      Id = "B",
      PositionAt = ut => new Vec3d(2000 * Math.Cos(ut), 2000 * Math.Sin(ut), 0),
      Motion = kB,
    };
    epB.Antennas.Add(antenna);
    net.AddEndpoint(epA); net.AddEndpoint(epB);

    // Should not throw — eccentric pair routes through numerical.
    var graph = net.Solve(0);
    Assert.AreEqual(2, graph.Links.Count);
  }

  [TestMethod]
  public void Dispatcher_DifferentParentBodies_FallsBackToNumerical() {
    var bodyA = new Body { Id = "BodyA", Mu = 1, Radius = 0.1 };
    var bodyB = new Body { Id = "BodyB", Mu = 1, Radius = 0.1 };
    var (kA, posA) = Circular(bodyA, a: 1000, inclinationRad: 0,
                               lanRad: 0, argPeRad: 0, meanAnomalyAtEpoch: 0);
    var (kB, posB) = Circular(bodyB, a: 1000, inclinationRad: 0,
                               lanRad: 0, argPeRad: 0, meanAnomalyAtEpoch: 0);
    var antenna = Knee(refDist: 1500, maxRate: 1000);

    var net = new CommunicationsNetwork();
    var epA = new Endpoint { Id = "A", PositionAt = posA, Motion = kA };
    epA.Antennas.Add(antenna);
    var epB = new Endpoint { Id = "B", PositionAt = posB, Motion = kB };
    epB.Antennas.Add(antenna);
    net.AddEndpoint(epA); net.AddEndpoint(epB);

    // Should solve cleanly via numerical fallback (different bodies
    // means the analytical path doesn't apply).
    var graph = net.Solve(0);
    Assert.AreEqual(2, graph.Links.Count);
  }

  // Build the same scenario twice — once with Motion attached
  // (analytical), once without (numerical) — and assert the link's
  // forecast NextEventUT matches within the bisection threshold.
  private static void AssertEquivalentEvent(
      Body body, KeplerMotion kA, Func<double, Vec3d> posA,
      KeplerMotion kB, Func<double, Vec3d> posB,
      Antenna antenna, double currentUT) {
    var threshold = CommunicationsParameters.MaxHorizonSeconds * 1e-6;

    var (analyticalNext, _) = ForecastFor(body, kA, posA, kB, posB, antenna, currentUT, withMotion: true);
    var (numericalNext, _) = ForecastFor(body, kA, posA, kB, posB, antenna, currentUT, withMotion: false);

    Assert.AreEqual(numericalNext, analyticalNext, threshold,
        $"NextEventUT mismatch at currentUT={currentUT}: analytical={analyticalNext}, numerical={numericalNext}");
  }

  private static (double nextEventUT, double rate) ForecastFor(
      Body body, KeplerMotion kA, Func<double, Vec3d> posA,
      KeplerMotion kB, Func<double, Vec3d> posB,
      Antenna antenna, double currentUT, bool withMotion) {
    var net = new CommunicationsNetwork();
    var epA = new Endpoint { Id = "A", PositionAt = posA, Motion = withMotion ? kA : null };
    epA.Antennas.Add(antenna);
    var epB = new Endpoint { Id = "B", PositionAt = posB, Motion = withMotion ? kB : null };
    epB.Antennas.Add(antenna);
    net.AddEndpoint(epA); net.AddEndpoint(epB);

    var graph = net.Solve(currentUT);
    var ab = graph.Links.First(l => l.From.Id == "A" && l.To.Id == "B");
    return (ab.NextEventUT, ab.RateBps);
  }
}
