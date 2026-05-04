using System;
using Nova.Core.Utils;

namespace Nova.Core.Communications;

// Closed-form position evaluator for any MotionModel — the
// drop-in replacement for KSP's iterative Orbit.getTruePositionAtUT
// inside the analytical horizon path.
//
// Composes recursively through the body chain so cross-SOI endpoints
// land in the same absolute frame: vessel-around-Mun-around-Kerbin-
// around-Sun yields the sum of Kepler positions along the chain.
//
// Per evaluation cost: O(depth) trig ops plus, per elliptical orbit,
// 6 Newton-Raphson iterations of Kepler's equation (matches the
// engine's test-side Orbits.Elliptical helper). Still many orders of
// magnitude cheaper than KSP's per-call cost.
public static class AnalyticalPosition {

  // World-frame position at UT for the given motion model. Returns
  // Vec3d.Zero if the model is null (caller must dispatch elsewhere).
  public static Vec3d Of(MotionModel m, double ut) {
    return m switch {
      KeplerMotion k => KeplerAt(k, ut) + BodyCentreAt(k.Parent, ut),
      SurfaceMotion s => SurfaceAt(s, ut) + BodyCentreAt(s.Parent, ut),
      _ => Vec3d.Zero,
    };
  }

  // Kepler position relative to the parent body's centre, in the
  // parent's inertial frame. Handles both circular (e=0) and
  // elliptical (0 < e < 1) orbits.
  private static Vec3d KeplerAt(KeplerMotion k, double ut) {
    var n = Math.Sqrt(k.Parent.Mu / (k.SemiMajorAxis * k.SemiMajorAxis * k.SemiMajorAxis));
    var meanAnomaly = k.MeanAnomalyAtEpoch + n * (ut - k.Epoch);

    double xPlane, yPlane;
    if (k.Eccentricity == 0) {
      // Circular: true anomaly = mean anomaly. Position has constant
      // radius `a`.
      xPlane = k.SemiMajorAxis * Math.Cos(meanAnomaly);
      yPlane = k.SemiMajorAxis * Math.Sin(meanAnomaly);
    } else {
      // Elliptical: solve Kepler's equation E - e·sin(E) = M for
      // eccentric anomaly E, then convert to perifocal coords.
      var E = SolveKepler(meanAnomaly, k.Eccentricity);
      xPlane = k.SemiMajorAxis * (Math.Cos(E) - k.Eccentricity);
      yPlane = k.SemiMajorAxis * Math.Sqrt(1 - k.Eccentricity * k.Eccentricity) * Math.Sin(E);
    }

    // Rotate from perifocal frame (periapsis along +x) into the
    // parent's inertial frame: R_z(Ω) · R_x(i) · R_z(ω).
    var cosArg = Math.Cos(k.ArgPe);
    var sinArg = Math.Sin(k.ArgPe);
    var cosI = Math.Cos(k.Inclination);
    var sinI = Math.Sin(k.Inclination);
    var cosLan = Math.Cos(k.Lan);
    var sinLan = Math.Sin(k.Lan);

    // Apply R_z(ω): rotate periapsis-aligned coords into the orbit's
    // ascending-node frame.
    var xN = cosArg * xPlane - sinArg * yPlane;
    var yN = sinArg * xPlane + cosArg * yPlane;
    // Apply R_x(i): tilt the orbital plane by inclination.
    var xT = xN;
    var yT = cosI * yN;
    var zT = sinI * yN;
    // Apply R_z(Ω): rotate the line of nodes to the inertial X axis.
    return new Vec3d(
      cosLan * xT - sinLan * yT,
      sinLan * xT + cosLan * yT,
      zT);
  }

  // Surface point relative to the parent body's centre, in the
  // parent's inertial frame, accounting for the body's rotation.
  // Spin axis is +Y (KSP convention). Body rotates eastward, i.e.
  // +ut moves +longitude.
  //
  // Body's rotation at UT: theta = initialRotation + omega·UT.
  private static Vec3d SurfaceAt(SurfaceMotion s, double ut) {
    var omega = 2 * Math.PI / s.Parent.RotationPeriod;
    var initialRotRad = s.Parent.InitialRotationDeg * Math.PI / 180.0;
    var lonRad = s.LongitudeDeg * Math.PI / 180.0;
    var latRad = s.LatitudeDeg * Math.PI / 180.0;
    var radius = s.Parent.Radius + s.AltitudeM;

    var phase = lonRad + initialRotRad + omega * ut;
    var cosLat = Math.Cos(latRad);
    return new Vec3d(
      radius * cosLat * Math.Cos(phase),
      radius * Math.Sin(latRad),
      radius * cosLat * Math.Sin(phase));
  }

  // Recursively walk the body chain. The root body (no parent) sits
  // at the origin. Each non-root body's position = its Kepler offset
  // around its parent + its parent's centre.
  private static Vec3d BodyCentreAt(Body body, double ut) {
    if (body == null || body.Parent == null || body.OrbitAroundParent == null)
      return Vec3d.Zero;
    return KeplerAt(body.OrbitAroundParent, ut) + BodyCentreAt(body.Parent, ut);
  }

  // Newton-Raphson on Kepler's equation: E - e·sin(E) = M. Six
  // iterations is plenty for e ≤ 0.9; converges quadratically.
  // Mirrors the test-fixture solver in Nova.Tests/Communications/Orbits.cs.
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
