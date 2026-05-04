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
    Func<double, Vec3d> pos = ut => AnalyticalPosition.Of(k, ut);
    return (k, pos);
  }

  [TestMethod]
  public void AnalyticalPosition_EquatorialCircular_MatchesOrbitsCircular() {
    // Circular equatorial orbit at a=1, period=2π. AnalyticalPosition
    // and the test-fixture Orbits.Circular helper should agree on
    // position to floating-point precision.
    var body = UnitBody();
    var (k, _) = Circular(body, a: 1.0, inclinationRad: 0, lanRad: 0,
                          argPeRad: 0, meanAnomalyAtEpoch: 0);
    // Orbits.Circular: phase = ω·ut; here ω = sqrt(Mu/a³) = 1.
    var reference = Orbits.Circular(radius: 1.0, period: 2 * Math.PI);
    for (var ut = 0.0; ut <= 2 * Math.PI; ut += 0.5) {
      var p_eval = AnalyticalPosition.Of(k, ut);
      var p_ref = reference(ut);
      Assert.AreEqual(p_ref.X, p_eval.X, 1e-12, $"X mismatch at ut={ut}");
      Assert.AreEqual(p_ref.Y, p_eval.Y, 1e-12, $"Y mismatch at ut={ut}");
      Assert.AreEqual(p_ref.Z, p_eval.Z, 1e-12, $"Z mismatch at ut={ut}");
    }
  }

  [TestMethod]
  public void AnalyticalPosition_PolarOrbit_PositionsInXZPlane() {
    // 90° inclination, LAN=0 → orbital plane is the XZ plane.
    // At argument-of-latitude=0 the vessel sits at +X (ascending node);
    // at π/2 it's at +Z; at π it's at -X; at 3π/2 it's at -Z.
    var body = UnitBody();
    var (k, _) = Circular(body, a: 1.0, inclinationRad: Math.PI / 2,
                          lanRad: 0, argPeRad: 0, meanAnomalyAtEpoch: 0);
    var p0 = AnalyticalPosition.Of(k, 0);
    var p1 = AnalyticalPosition.Of(k, Math.PI / 2);
    var p2 = AnalyticalPosition.Of(k, Math.PI);
    var p3 = AnalyticalPosition.Of(k, 3 * Math.PI / 2);

    Assert.AreEqual(1.0, p0.X, 1e-12); Assert.AreEqual(0.0, p0.Y, 1e-12); Assert.AreEqual(0.0, p0.Z, 1e-12);
    Assert.AreEqual(0.0, p1.X, 1e-12); Assert.AreEqual(0.0, p1.Y, 1e-12); Assert.AreEqual(1.0, p1.Z, 1e-12);
    Assert.AreEqual(-1.0, p2.X, 1e-12); Assert.AreEqual(0.0, p2.Y, 1e-12); Assert.AreEqual(0.0, p2.Z, 1e-12);
    Assert.AreEqual(0.0, p3.X, 1e-12); Assert.AreEqual(0.0, p3.Y, 1e-12); Assert.AreEqual(-1.0, p3.Z, 1e-12);
  }

  [TestMethod]
  public void AnalyticalPosition_EllipticalOrbit_MatchesOrbitsElliptical() {
    // Elliptical orbit at a=1, e=0.5, period=2π. Validates the
    // Newton-Raphson Kepler solve against the test-fixture reference
    // implementation, which is independently derived.
    var body = UnitBody();
    var k = new KeplerMotion {
      Parent = body, SemiMajorAxis = 1.0, Eccentricity = 0.5,
      Inclination = 0, Lan = 0, ArgPe = 0,
      MeanAnomalyAtEpoch = 0, Epoch = 0,
    };
    // Orbits.Elliptical with same params: argPeriapsis=0, M₀=0, period=2π.
    var reference = Orbits.Elliptical(semiMajor: 1.0, eccentricity: 0.5,
                                       period: 2 * Math.PI);
    for (var ut = 0.0; ut <= 2 * Math.PI; ut += 0.3) {
      var p_eval = AnalyticalPosition.Of(k, ut);
      var p_ref = reference(ut);
      // Both implementations use 6 Newton-Raphson iterations; agree
      // to floating-point precision.
      Assert.AreEqual(p_ref.X, p_eval.X, 1e-10, $"X mismatch at ut={ut}");
      Assert.AreEqual(p_ref.Y, p_eval.Y, 1e-10, $"Y mismatch at ut={ut}");
      Assert.AreEqual(p_ref.Z, p_eval.Z, 1e-10, $"Z mismatch at ut={ut}");
    }
  }

  [TestMethod]
  public void AnalyticalPosition_Surface_RotatesEastwardWithBody() {
    // Body with rotation period 2π so omega = 1 rad/s. A point at
    // (lat=0, lon=0, alt=0) on a body of radius 1 starts at +X and
    // sweeps eastward (toward +Z by KSP convention: +Y rotation).
    var body = new Body {
      Id = "TestBody", Mu = 1, Radius = 1.0,
      RotationPeriod = 2 * Math.PI, InitialRotationDeg = 0,
    };
    var s = new SurfaceMotion {
      Parent = body, LatitudeDeg = 0, LongitudeDeg = 0, AltitudeM = 0,
    };
    var p0 = AnalyticalPosition.Of(s, 0);
    var p1 = AnalyticalPosition.Of(s, Math.PI / 2);
    var p2 = AnalyticalPosition.Of(s, Math.PI);

    Assert.AreEqual(1.0, p0.X, 1e-12); Assert.AreEqual(0.0, p0.Z, 1e-12);
    Assert.AreEqual(0.0, p1.X, 1e-12); Assert.AreEqual(1.0, p1.Z, 1e-12);
    Assert.AreEqual(-1.0, p2.X, 1e-12); Assert.AreEqual(0.0, p2.Z, 1e-12);
  }

  [TestMethod]
  public void AnalyticalPosition_Surface_HonoursInitialRotation() {
    // Body with InitialRotationDeg = 90°. A point at lon=0 should
    // appear rotated 90° eastward at ut=0 — i.e., at +Z (since
    // theta = 0 + 90° + 0 = 90°).
    var body = new Body {
      Id = "Spun", Mu = 1, Radius = 1.0,
      RotationPeriod = 2 * Math.PI, InitialRotationDeg = 90,
    };
    var s = new SurfaceMotion {
      Parent = body, LatitudeDeg = 0, LongitudeDeg = 0, AltitudeM = 0,
    };
    var p = AnalyticalPosition.Of(s, 0);
    Assert.AreEqual(0.0, p.X, 1e-12);
    Assert.AreEqual(1.0, p.Z, 1e-12);
  }

  [TestMethod]
  public void AnalyticalPosition_BodyChain_AddsParentOrbit() {
    // Two-level chain: a "moon" orbiting a "planet" at radius 10, and
    // a vessel orbiting the moon at radius 1. Vessel's absolute
    // position = vessel-relative-to-moon + moon-relative-to-planet.
    var planet = new Body { Id = "Planet", Mu = 1000, Radius = 5 };
    var moonOrbit = new KeplerMotion {
      Parent = planet, SemiMajorAxis = 10, Eccentricity = 0,
      Inclination = 0, Lan = 0, ArgPe = 0, MeanAnomalyAtEpoch = 0, Epoch = 0,
    };
    var moon = new Body {
      Id = "Moon", Mu = 1, Radius = 1,
      Parent = planet, OrbitAroundParent = moonOrbit,
    };
    var vesselOrbit = new KeplerMotion {
      Parent = moon, SemiMajorAxis = 1, Eccentricity = 0,
      Inclination = 0, Lan = 0, ArgPe = 0, MeanAnomalyAtEpoch = 0, Epoch = 0,
    };

    // At ut=0: moon is at (10, 0, 0) relative to planet. Vessel is at
    // (1, 0, 0) relative to moon. Total: (11, 0, 0) relative to planet
    // (which is the root in this scenario).
    var p = AnalyticalPosition.Of(vesselOrbit, 0);
    Assert.AreEqual(11.0, p.X, 1e-10);
    Assert.AreEqual(0.0, p.Y, 1e-10);
    Assert.AreEqual(0.0, p.Z, 1e-10);
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
  public void Dispatcher_EccentricCircularPair_AnalyticalMatchesNumerical() {
    // Mixed circular + elliptical pair on the same parent body. The
    // analytical path now handles eccentric via Newton-Raphson; verify
    // it agrees with the independent numerical reference (Orbits.*).
    var body = UnitBody();
    Func<double, Vec3d> posA = Orbits.Circular(radius: 1000, period: 2 * Math.PI * Math.Sqrt(1000.0 * 1000 * 1000));
    Func<double, Vec3d> posB = Orbits.Elliptical(semiMajor: 2000, eccentricity: 0.3,
                                                  period: 2 * Math.PI * Math.Sqrt(2000.0 * 2000 * 2000));
    var kA = new KeplerMotion {
      Parent = body, SemiMajorAxis = 1000, Eccentricity = 0,
      Inclination = 0, Lan = 0, ArgPe = 0, MeanAnomalyAtEpoch = 0, Epoch = 0,
    };
    var kB = new KeplerMotion {
      Parent = body, SemiMajorAxis = 2000, Eccentricity = 0.3,
      Inclination = 0, Lan = 0, ArgPe = 0, MeanAnomalyAtEpoch = 0, Epoch = 0,
    };
    var antenna = Knee(refDist: 1500, maxRate: 1000);

    var (analyticalNext, _) = ForecastForPair(antenna, posA, kA, posB, kB, currentUT: 0, withMotion: true);
    var (numericalNext, _) = ForecastForPair(antenna, posA, kA, posB, kB, currentUT: 0, withMotion: false);
    var threshold = CommunicationsParameters.MaxHorizonSeconds * 1e-6;
    Assert.AreEqual(numericalNext, analyticalNext, threshold,
        $"eccentric pair analytical={analyticalNext} numerical={numericalNext}");
  }

  [TestMethod]
  public void Dispatcher_SurfaceSurfaceSameBody_RotateTogether_HorizonCap() {
    // Two surface points on the same body — they co-rotate, so their
    // distance is constant. Bisection should find no bucket transition
    // → return horizon cap.
    var body = new Body {
      Id = "Body", Mu = 1, Radius = 1000,
      RotationPeriod = 86400, InitialRotationDeg = 0,
    };
    var sA = new SurfaceMotion { Parent = body, LatitudeDeg = 0, LongitudeDeg = 0, AltitudeM = 0 };
    var sB = new SurfaceMotion { Parent = body, LatitudeDeg = 0, LongitudeDeg = 30, AltitudeM = 0 };
    var antenna = Knee(refDist: 700, maxRate: 1000);

    var net = new CommunicationsNetwork();
    var epA = new Endpoint {
      Id = "A", Motion = sA,
      PositionAt = ut => AnalyticalPosition.Of(sA, ut),
    };
    epA.Antennas.Add(antenna);
    var epB = new Endpoint {
      Id = "B", Motion = sB,
      PositionAt = ut => AnalyticalPosition.Of(sB, ut),
    };
    epB.Antennas.Add(antenna);
    net.AddEndpoint(epA); net.AddEndpoint(epB);

    var graph = net.Solve(0);
    var ab = graph.Links.First(l => l.From.Id == "A" && l.To.Id == "B");
    Assert.AreEqual(0 + CommunicationsParameters.MaxHorizonSeconds, ab.NextEventUT, 1.0,
        "co-rotating surface pair should never see a bucket transition");
  }

  [TestMethod]
  public void Dispatcher_SurfaceKeplerSameBody_AnalyticalMatchesNumerical() {
    // Surface station + orbiting vessel around the same body.
    // Analytical path now covers SurfaceMotion + KeplerMotion via the
    // body-chain composition (both have the same Parent, BodyCentreAt
    // contributes 0 for the root, surface offset rotates with body,
    // Kepler offset orbits around it).
    var body = new Body {
      Id = "Body", Mu = 1, Radius = 100,
      RotationPeriod = 1000, InitialRotationDeg = 0,
    };
    var sStation = new SurfaceMotion {
      Parent = body, LatitudeDeg = 0, LongitudeDeg = 0, AltitudeM = 0,
    };
    var kVessel = new KeplerMotion {
      Parent = body, SemiMajorAxis = 200, Eccentricity = 0,
      Inclination = 0, Lan = 0, ArgPe = 0,
      MeanAnomalyAtEpoch = Math.PI / 4, Epoch = 0,
    };
    var antenna = Knee(refDist: 200, maxRate: 1000);

    Func<double, Vec3d> posStation = ut => AnalyticalPosition.Of(sStation, ut);
    Func<double, Vec3d> posVessel = ut => AnalyticalPosition.Of(kVessel, ut);

    var (anN, _) = ForecastFor(body, kVessel, posVessel, kVessel, posVessel, antenna, 0,
                               withMotion: true,
                               overrideMotionA: sStation, overridePosA: posStation);
    var (numN, _) = ForecastFor(body, kVessel, posVessel, kVessel, posVessel, antenna, 0,
                                withMotion: false,
                                overrideMotionA: sStation, overridePosA: posStation);
    var threshold = CommunicationsParameters.MaxHorizonSeconds * 1e-6;
    Assert.AreEqual(numN, anN, threshold,
        $"surface↔Kepler analytical={anN} numerical={numN}");
  }

  [TestMethod]
  public void Dispatcher_DifferentParentBodies_HierarchicalAnalyticalMatchesNumerical() {
    // Two-level chain: planet (root, Mu=1e6) with two moons orbiting
    // it; one vessel around each moon. Cross-moon link's r(t) needs
    // hierarchical body-chain composition.
    var planet = new Body { Id = "Planet", Mu = 1e6, Radius = 100 };
    var moonAOrbit = new KeplerMotion {
      Parent = planet, SemiMajorAxis = 1000, Eccentricity = 0,
      Inclination = 0, Lan = 0, ArgPe = 0, MeanAnomalyAtEpoch = 0, Epoch = 0,
    };
    var moonA = new Body {
      Id = "MoonA", Mu = 100, Radius = 10,
      Parent = planet, OrbitAroundParent = moonAOrbit,
    };
    var moonBOrbit = new KeplerMotion {
      Parent = planet, SemiMajorAxis = 1500, Eccentricity = 0,
      Inclination = 0, Lan = 0, ArgPe = 0, MeanAnomalyAtEpoch = Math.PI / 3, Epoch = 0,
    };
    var moonB = new Body {
      Id = "MoonB", Mu = 100, Radius = 10,
      Parent = planet, OrbitAroundParent = moonBOrbit,
    };
    var vesselA = new KeplerMotion {
      Parent = moonA, SemiMajorAxis = 30, Eccentricity = 0,
      Inclination = 0, Lan = 0, ArgPe = 0, MeanAnomalyAtEpoch = 0, Epoch = 0,
    };
    var vesselB = new KeplerMotion {
      Parent = moonB, SemiMajorAxis = 30, Eccentricity = 0,
      Inclination = 0, Lan = 0, ArgPe = 0, MeanAnomalyAtEpoch = 0, Epoch = 0,
    };
    // Closures use AnalyticalPosition's hierarchical composition for
    // the numerical reference too — they must, because Orbits.Circular
    // doesn't support body chains. Equivalence check here verifies
    // dispatcher routing, not Kepler math (covered separately above).
    Func<double, Vec3d> posA = ut => AnalyticalPosition.Of(vesselA, ut);
    Func<double, Vec3d> posB = ut => AnalyticalPosition.Of(vesselB, ut);
    var antenna = Knee(refDist: 600, maxRate: 1000);

    var (anN, _) = ForecastFor(planet, vesselA, posA, vesselB, posB, antenna, 0, withMotion: true);
    var (numN, _) = ForecastFor(planet, vesselA, posA, vesselB, posB, antenna, 0, withMotion: false);
    var threshold = CommunicationsParameters.MaxHorizonSeconds * 1e-6;
    Assert.AreEqual(numN, anN, threshold,
        $"cross-moon analytical={anN} numerical={numN}");
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
      Body body,
      MotionModel mA, Func<double, Vec3d> posA,
      MotionModel mB, Func<double, Vec3d> posB,
      Antenna antenna, double currentUT, bool withMotion,
      MotionModel overrideMotionA = null, Func<double, Vec3d> overridePosA = null) {
    var net = new CommunicationsNetwork();
    var actualMotionA = overrideMotionA ?? mA;
    var actualPosA = overridePosA ?? posA;
    var epA = new Endpoint {
      Id = "A",
      PositionAt = actualPosA,
      Motion = withMotion ? actualMotionA : null,
    };
    epA.Antennas.Add(antenna);
    var epB = new Endpoint {
      Id = "B",
      PositionAt = posB,
      Motion = withMotion ? mB : null,
    };
    epB.Antennas.Add(antenna);
    net.AddEndpoint(epA); net.AddEndpoint(epB);

    var graph = net.Solve(currentUT);
    var ab = graph.Links.First(l => l.From.Id == "A" && l.To.Id == "B");
    return (ab.NextEventUT, ab.RateBps);
  }

  [TestMethod]
  public void Unpredictable_Endpoint_SkipsBisection_PinsHorizonCap() {
    // Endpoint with IsPredictable=false (off-rails proxy). The pair's
    // NextEventUT must be set to the horizon cap regardless of the
    // closure's actual behaviour over time — bisection is suppressed.
    var net = new CommunicationsNetwork();
    var antenna = new Antenna { TxPower = 1, Gain = 1, MaxRate = 1000, RefDistance = 100 };
    var ground = new Endpoint { Id = "G", PositionAt = _ => Vec3d.Zero };
    ground.Antennas.Add(antenna);
    // Receding satellite — bisection would normally find a bucket
    // transition at t≈10s. With IsPredictable=false we skip the work.
    var sat = new Endpoint {
      Id = "S",
      PositionAt = ut => new Vec3d(90 + ut, 0, 0),
      IsPredictable = false,
    };
    sat.Antennas.Add(antenna);
    net.AddEndpoint(ground); net.AddEndpoint(sat);
    var graph = net.Solve(0);
    var gs = graph.Links.First(l => l.From.Id == "G" && l.To.Id == "S");
    Assert.AreEqual(0 + CommunicationsParameters.MaxHorizonSeconds, gs.NextEventUT, 1e-6,
        "unpredictable pair should pin to horizon cap, not the t≈10s transition");
  }

  [TestMethod]
  public void OffRailsCheck_NoBucketChange_ReportsFalse() {
    // Two stationary endpoints, no Motion (closure-only). After the
    // first solve, current bucket matches cached → no change.
    var net = new CommunicationsNetwork();
    var antenna = Knee(refDist: 100, maxRate: 1000);
    var a = new Endpoint { Id = "A", PositionAt = _ => Vec3d.Zero };
    a.Antennas.Add(antenna);
    var b = new Endpoint { Id = "B", PositionAt = _ => new Vec3d(50, 0, 0) };
    b.Antennas.Add(antenna);
    net.AddEndpoint(a); net.AddEndpoint(b);
    net.Solve(0);
    Assert.IsFalse(net.AnyLinkBucketDifference(a, 1));
    Assert.IsFalse(net.AnyLinkBucketDifference(a, 100));
  }

  [TestMethod]
  public void OffRailsCheck_BucketCrossed_ReportsTrue() {
    // Endpoint moves at 1 m/s; antenna knee at r=100 (Knee100). At
    // t=0 it's at r=90 (above-knee, full rate). At t=20 it's at
    // r=110 (below knee, lower bucket). Bucket has changed.
    var net = new CommunicationsNetwork();
    var antenna = new Antenna { TxPower = 1, Gain = 1, MaxRate = 1000, RefDistance = 100 };
    var ground = new Endpoint { Id = "G", PositionAt = _ => Vec3d.Zero };
    ground.Antennas.Add(antenna);
    var sat = new Endpoint {
      Id = "S",
      PositionAt = ut => new Vec3d(90 + ut, 0, 0),  // 1 m/s recede
    };
    sat.Antennas.Add(antenna);
    net.AddEndpoint(ground); net.AddEndpoint(sat);
    net.Solve(0);
    Assert.IsFalse(net.AnyLinkBucketDifference(sat, 0));
    // At t=20, distance is 110m → below knee. Bucket should have changed.
    Assert.IsTrue(net.AnyLinkBucketDifference(sat, 20));
  }

  [TestMethod]
  public void OffRailsCheck_Invalidate_ClearsCacheAndForcesRecompute() {
    // After detecting a bucket change, calling Invalidate() should
    // both set NeedsSolve and clear linkHorizonCache (so next Solve
    // recomputes horizons from scratch).
    var net = new CommunicationsNetwork();
    var antenna = new Antenna { TxPower = 1, Gain = 1, MaxRate = 1000, RefDistance = 100 };
    var ground = new Endpoint { Id = "G", PositionAt = _ => Vec3d.Zero };
    ground.Antennas.Add(antenna);
    var sat = new Endpoint {
      Id = "S",
      PositionAt = ut => new Vec3d(90 + ut, 0, 0),
    };
    sat.Antennas.Add(antenna);
    net.AddEndpoint(ground); net.AddEndpoint(sat);
    net.Solve(0);
    Assert.IsFalse(net.NeedsSolve);
    if (net.AnyLinkBucketDifference(sat, 20)) net.Invalidate();
    Assert.IsTrue(net.NeedsSolve);
    var graph = net.Solve(20);
    var gs = graph.Links.First(l => l.From.Id == "G" && l.To.Id == "S");
    // After re-solve at t=20, the link should reflect the new bucket
    // (recede past the knee → quantised below 1000).
    Assert.IsTrue(gs.RateBps < 1000);
  }

  // Convenience for tests that don't need the Body parameter (tests
  // already wired their own motion models).
  private static (double nextEventUT, double rate) ForecastForPair(
      Antenna antenna,
      Func<double, Vec3d> posA, MotionModel mA,
      Func<double, Vec3d> posB, MotionModel mB,
      double currentUT, bool withMotion) {
    return ForecastFor(body: null, mA, posA, mB, posB, antenna, currentUT, withMotion);
  }
}
