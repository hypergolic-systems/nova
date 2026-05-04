using System;
using Nova.Core.Utils;

namespace Nova.Tests.Communications;

// Test-side position-over-time primitives. Returns Func<double, Vec3d>
// in the same shape Endpoint.PositionAt expects, so tests can plug in
// realistic satellite patterns without depending on KSP. Lives in
// Nova.Tests deliberately — production gets positions from KSP via
// IVesselContext.VesselPositionAt; the engine has no orbit propagator.
public static class Orbits {

  // Constant position. Trivially callable; used for ground stations
  // and stationary fixtures.
  public static Func<double, Vec3d> Stationary(Vec3d position) => _ => position;

  // Constant-velocity straight-line motion: r(t) = origin + velocity·t.
  // Closed-form distance(t) makes analytic test assertions easy.
  public static Func<double, Vec3d> Linear(Vec3d origin, Vec3d velocity) =>
      ut => origin + ut * velocity;

  // Circular orbit in the XY plane (Z = 0). Counterclockwise viewed
  // from +Z. `phase` shifts the t=0 position around the circle.
  public static Func<double, Vec3d> Circular(double radius, double period,
      double phase = 0, Vec3d? center = null) {
    if (period <= 0) throw new ArgumentException("period must be > 0", nameof(period));
    var omega = 2 * Math.PI / period;
    var c = center ?? Vec3d.Zero;
    return ut => {
      var theta = omega * ut + phase;
      return new Vec3d(c.X + radius * Math.Cos(theta),
                       c.Y + radius * Math.Sin(theta),
                       c.Z);
    };
  }

  // Elliptical orbit in the XY plane. Periapsis lies along the
  // direction (cos argPeri, sin argPeri). meanAnomalyAtT0 ∈ [0, 2π) is
  // the mean anomaly at UT=0; M = 0 puts the body at periapsis at t=0.
  // Solves Kepler's equation each call via Newton-Raphson.
  public static Func<double, Vec3d> Elliptical(double semiMajor, double eccentricity,
      double period, double argPeriapsis = 0, double meanAnomalyAtT0 = 0,
      Vec3d? center = null) {
    if (period <= 0) throw new ArgumentException("period must be > 0", nameof(period));
    if (semiMajor <= 0) throw new ArgumentException("semiMajor must be > 0", nameof(semiMajor));
    if (eccentricity < 0 || eccentricity >= 1)
      throw new ArgumentException("eccentricity must be in [0, 1)", nameof(eccentricity));

    var n = 2 * Math.PI / period;            // mean motion
    var a = semiMajor;
    var e = eccentricity;
    var c = center ?? Vec3d.Zero;
    var cosArg = Math.Cos(argPeriapsis);
    var sinArg = Math.Sin(argPeriapsis);

    return ut => {
      var meanAnomaly = meanAnomalyAtT0 + n * ut;
      var eccAnomaly = SolveKepler(meanAnomaly, e);
      var x_pf = a * (Math.Cos(eccAnomaly) - e);
      var y_pf = a * Math.Sqrt(1 - e * e) * Math.Sin(eccAnomaly);
      var x = cosArg * x_pf - sinArg * y_pf;
      var y = sinArg * x_pf + cosArg * y_pf;
      return new Vec3d(c.X + x, c.Y + y, c.Z);
    };
  }

  // Newton-Raphson on Kepler's equation: E - e·sin(E) = M.
  // 6 iterations is plenty for e ≤ 0.9; converges quadratically.
  private static double SolveKepler(double M, double e) {
    var E = e < 0.8 ? M : Math.PI;
    for (int i = 0; i < 6; i++) {
      var f = E - e * Math.Sin(E) - M;
      var fp = 1 - e * Math.Cos(E);
      E -= f / fp;
    }
    return E;
  }
}
