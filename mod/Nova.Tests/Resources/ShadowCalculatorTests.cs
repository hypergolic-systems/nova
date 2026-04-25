using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nova.Core.Resources;
using Nova.Core.Flight;
using Nova.Core.Utils;
namespace Nova.Tests.Resources;

[TestClass]
public class ShadowCalculatorTests {

  /// <summary>
  /// Circular orbit position function in the XZ plane. Position traces a circle of given radius
  /// with angular velocity 2pi/period. Phase 0 = +X axis (sub-solar when sun is at +X).
  /// </summary>
  private static Func<double, Vec3d> CircularOrbit(double radius, double period, double phase0 = 0) {
    return ut => {
      double angle = phase0 + 2 * Math.PI * ut / period;
      return new Vec3d(radius * Math.Cos(angle), 0, radius * Math.Sin(angle));
    };
  }

  /// <summary>
  /// Fixed sun position (does not move). Returns the same position for all times.
  /// </summary>
  private static Func<double, Vec3d> FixedSun(Vec3d position) {
    return _ => position;
  }

  /// <summary>
  /// Hyperbolic orbit: straight-line trajectory moving away from body along +X.
  /// </summary>
  private static Func<double, Vec3d> HyperbolicOrbit(double radius) {
    return ut => new Vec3d(radius + ut * 1000, 0, 0);
  }

  /// <summary>
  /// Sun that orbits around the body (simulating the body's orbital motion around the sun).
  /// </summary>
  private static Func<double, Vec3d> OrbitalSun(double distance, double period) {
    return ut => {
      double angle = 2 * Math.PI * ut / period;
      return new Vec3d(distance * Math.Cos(angle), 0, distance * Math.Sin(angle));
    };
  }

  // Kerbin-like: radius 600km, orbit at 700km, period ~2400s
  private const double BodyRadius = 600_000;
  private const double OrbitRadius = 700_000;
  private const double OrbitalPeriod = 2400;
  private static readonly Vec3d SunAtPlusX = new(1e10, 0, 0);

  [TestMethod]
  public void NullOrbits_AlwaysSunlit() {
    var state = ShadowCalculator.Compute(null, null, double.PositiveInfinity, BodyRadius, false, 0);
    Assert.IsTrue(state.InSunlight);
    Assert.AreEqual(double.PositiveInfinity, state.NextTransitionUT);
  }

  [TestMethod]
  public void OrbitingSun_AlwaysSunlit() {
    var orbit = CircularOrbit(OrbitRadius, OrbitalPeriod, phase0: Math.PI);
    var sun = FixedSun(SunAtPlusX);
    // Even at anti-solar phase, orbiting the sun means no shadow
    var state = ShadowCalculator.Compute(orbit, sun, OrbitalPeriod, BodyRadius, orbitingSun: true, 0);
    Assert.IsTrue(state.InSunlight);
    Assert.AreEqual(double.PositiveInfinity, state.NextTransitionUT);
  }

  [TestMethod]
  public void SubSolarPoint_InSunlight() {
    // Vessel at phase=0, directly between body and sun -> sun side -> sunlit
    var orbit = CircularOrbit(OrbitRadius, OrbitalPeriod, phase0: 0);
    var sun = FixedSun(SunAtPlusX);

    var state = ShadowCalculator.Compute(orbit, sun, OrbitalPeriod, BodyRadius, false, 0);
    Assert.IsTrue(state.InSunlight);
  }

  [TestMethod]
  public void AntiSolarPoint_InShadow() {
    // Vessel at phase=pi, directly behind body from sun -> in shadow
    var orbit = CircularOrbit(OrbitRadius, OrbitalPeriod, phase0: Math.PI);
    var sun = FixedSun(SunAtPlusX);

    var state = ShadowCalculator.Compute(orbit, sun, OrbitalPeriod, BodyRadius, false, 0);
    Assert.IsFalse(state.InSunlight);
  }

  [TestMethod]
  public void ShadowEntry_TransitionTime() {
    // Vessel starts at phase=0 (sunlit), should enter shadow at phase = pi - arcsin(R_body/R_orbit)
    var orbit = CircularOrbit(OrbitRadius, OrbitalPeriod, phase0: 0);
    var sun = FixedSun(SunAtPlusX);

    var state = ShadowCalculator.Compute(orbit, sun, OrbitalPeriod, BodyRadius, false, 0);
    Assert.IsTrue(state.InSunlight);

    double halfAngle = Math.Asin(BodyRadius / OrbitRadius);
    double entryPhase = Math.PI - halfAngle;
    double expectedEntryUT = entryPhase / (2 * Math.PI) * OrbitalPeriod;

    // Transition time should match within threshold
    double tolerance = OrbitalPeriod * 1e-5;
    Assert.AreEqual(expectedEntryUT, state.NextTransitionUT, tolerance);
  }

  [TestMethod]
  public void ShadowExit_TransitionTime() {
    // Vessel starts at phase=pi (in shadow), should exit at phase = pi + arcsin(R_body/R_orbit)
    var orbit = CircularOrbit(OrbitRadius, OrbitalPeriod, phase0: Math.PI);
    var sun = FixedSun(SunAtPlusX);

    var state = ShadowCalculator.Compute(orbit, sun, OrbitalPeriod, BodyRadius, false, 0);
    Assert.IsFalse(state.InSunlight);

    double halfAngle = Math.Asin(BodyRadius / OrbitRadius);
    double exitPhase = Math.PI + halfAngle;
    // Phase from pi to exit = halfAngle
    double expectedExitUT = halfAngle / (2 * Math.PI) * OrbitalPeriod;

    double tolerance = OrbitalPeriod * 1e-5;
    Assert.AreEqual(expectedExitUT, state.NextTransitionUT, tolerance);
  }

  [TestMethod]
  public void HighOrbit_NarrowShadow_StillDetected() {
    // Orbit at 10x body radius -- narrow shadow arc (~5.7 degrees)
    double highOrbitRadius = BodyRadius * 10;
    double highPeriod = 20000;
    var orbit = CircularOrbit(highOrbitRadius, highPeriod, phase0: 0);
    var sun = FixedSun(SunAtPlusX);

    var state = ShadowCalculator.Compute(orbit, sun, highPeriod, BodyRadius, false, 0);
    Assert.IsTrue(state.InSunlight);
    // Should find a transition (shadow entry) within one period
    Assert.IsTrue(state.NextTransitionUT < highPeriod);
  }

  [TestMethod]
  public void HyperbolicOrbit_FiniteHorizon() {
    // Hyperbolic orbit with infinite period -- should use capped horizon
    var orbit = HyperbolicOrbit(OrbitRadius);
    var sun = FixedSun(SunAtPlusX);

    var state = ShadowCalculator.Compute(orbit, sun, double.PositiveInfinity, BodyRadius, false, 0);
    // Should not hang or return infinity -- horizon is capped
    Assert.IsTrue(state.InSunlight || !state.InSunlight); // just verify it returns
    Assert.IsTrue(state.NextTransitionUT <= 86400 + 1);
  }

  [TestMethod]
  public void MovingSun_AffectsTransitionTime() {
    // Sun orbits the body (simulating body's orbital motion around the sun).
    // Compare transition time with fixed sun vs moving sun -- they should differ.
    var orbit = CircularOrbit(OrbitRadius, OrbitalPeriod, phase0: 0);
    var fixedSunFunc = FixedSun(SunAtPlusX);
    var movingSunFunc = OrbitalSun(1e10, OrbitalPeriod * 100);

    var fixedState = ShadowCalculator.Compute(orbit, fixedSunFunc, OrbitalPeriod, BodyRadius, false, 0);
    var movingState = ShadowCalculator.Compute(orbit, movingSunFunc, OrbitalPeriod, BodyRadius, false, 0);

    Assert.IsTrue(fixedState.InSunlight);
    Assert.IsTrue(movingState.InSunlight);
    // Moving sun should shift the transition time
    Assert.AreNotEqual(fixedState.NextTransitionUT, movingState.NextTransitionUT, 1.0);
  }

  [TestMethod]
  public void IsInShadow_SunSide_NotInShadow() {
    var vesselPos = new Vec3d(1000, 0, 0);
    var sunPos = new Vec3d(1e10, 0, 0);
    Assert.IsFalse(ShadowCalculator.IsInShadow(vesselPos, sunPos, 600));
  }

  [TestMethod]
  public void IsInShadow_DarkSideInCylinder_InShadow() {
    var vesselPos = new Vec3d(-1000, 0, 100);
    var sunPos = new Vec3d(1e10, 0, 0);
    Assert.IsTrue(ShadowCalculator.IsInShadow(vesselPos, sunPos, 600));
  }

  [TestMethod]
  public void IsInShadow_DarkSideOutsideCylinder_NotInShadow() {
    var vesselPos = new Vec3d(-1000, 0, 700);
    var sunPos = new Vec3d(1e10, 0, 0);
    Assert.IsFalse(ShadowCalculator.IsInShadow(vesselPos, sunPos, 600));
  }
}
