using System;
using Nova.Core.Utils;

namespace Nova.Sim.Universe;

// Keplerian orbital propagator. Computes a body's (or vessel's)
// position vector in its parent's inertial frame at an arbitrary
// universal time. Composing through a parent chain gives heliocentric
// coordinates; subtracting two body positions gives relative position
// (e.g. sun direction from a vessel).
//
// Conventions:
//   - Inertial frame is right-handed, +X toward the reference
//     direction (longitude of ascending node = 0), +Z along the
//     reference plane normal (parent's equator). Stock KSP uses a
//     similar convention; for the sim, the only thing that matters
//     is that headings stay consistent across calls.
//   - Angles inputs in degrees; internal math in radians.
//   - Time inputs in seconds (UT).
//
// Algorithm:
//   1. Compute mean anomaly M(t) = M₀ + n·(t-0), where n = 2π/period.
//   2. Solve Kepler's equation M = E - e·sin(E) for the eccentric
//      anomaly E via Newton-Raphson (typically ≤6 iterations).
//   3. True anomaly ν from E: tan(ν/2) = √((1+e)/(1-e)) · tan(E/2).
//   4. Distance r = a·(1 - e·cos(E)).
//   5. Position in orbital plane: x' = r·cos(ν), y' = r·sin(ν).
//   6. Rotate to inertial frame by argPeriapsis → inclination → LAN.
public static class OrbitMath {
  public static Vec3d PositionAtUT(BodyData.OrbitElements e, double parentGM, double yearSeconds, double ut) {
    if (yearSeconds <= 0 || parentGM <= 0) return Vec3d.Zero;

    double n = 2.0 * Math.PI / yearSeconds; // mean motion (rad/s)
    double M = e.MeanAnomalyAtEpoch + n * ut;
    double E = SolveKepler(M, e.Eccentricity);

    double cosE = Math.Cos(E);
    double sinE = Math.Sin(E);
    double sqrt1m2 = Math.Sqrt(1.0 - e.Eccentricity * e.Eccentricity);

    // Position in orbital (perifocal) frame: +x = periapsis direction.
    double xPerifocal = e.SemiMajorAxis * (cosE - e.Eccentricity);
    double yPerifocal = e.SemiMajorAxis * sqrt1m2 * sinE;

    double argP = e.ArgPeriapsisDeg * Math.PI / 180.0;
    double inc  = e.InclinationDeg  * Math.PI / 180.0;
    double lan  = e.LongitudeOfAscDeg * Math.PI / 180.0;

    // Rotate perifocal → inertial via Rz(LAN) · Rx(inc) · Rz(argP).
    double cArgP = Math.Cos(argP), sArgP = Math.Sin(argP);
    double cInc  = Math.Cos(inc),  sInc  = Math.Sin(inc);
    double cLan  = Math.Cos(lan),  sLan  = Math.Sin(lan);

    double x1 =  cArgP * xPerifocal - sArgP * yPerifocal;
    double y1 =  sArgP * xPerifocal + cArgP * yPerifocal;
    // z1 = 0

    double x2 = x1;
    double y2 = cInc * y1;
    double z2 = sInc * y1;

    double x3 = cLan * x2 - sLan * y2;
    double y3 = sLan * x2 + cLan * y2;
    double z3 = z2;

    return new Vec3d(x3, y3, z3);
  }

  // Heliocentric position of a body: walk parent chain summing local
  // positions. The Sun sits at the origin.
  public static Vec3d HeliocentricPosition(string bodyName, double ut) {
    var body = BodyData.Get(bodyName);
    if (body == null || body.Parent == null) return Vec3d.Zero;
    var parent = BodyData.Get(body.Parent);
    if (parent == null) return Vec3d.Zero;
    var localPos = PositionAtUT(body.Orbit, parent.GM, body.YearSeconds, ut);
    return localPos + HeliocentricPosition(body.Parent, ut);
  }

  // Orbital period for a circular/elliptic orbit around a body with GM
  // and semi-major axis `a`. Returns 0 for non-positive or escape inputs.
  public static double Period(double parentGM, double semiMajorAxis) {
    if (parentGM <= 0 || semiMajorAxis <= 0) return 0;
    return 2.0 * Math.PI * Math.Sqrt(semiMajorAxis * semiMajorAxis * semiMajorAxis / parentGM);
  }

  // Newton-Raphson on Kepler's equation. Converges quadratically;
  // 6 iterations are plenty for e<0.95 — Kerbol-system bodies are
  // all far below that. Seed with M for low eccentricity, π for
  // high (helps when the iterate would otherwise wander).
  private static double SolveKepler(double M, double e) {
    // Normalise M to (-π, π].
    M = M % (2.0 * Math.PI);
    if (M >  Math.PI) M -= 2.0 * Math.PI;
    if (M < -Math.PI) M += 2.0 * Math.PI;

    double E = e < 0.8 ? M : Math.PI;
    for (int i = 0; i < 12; i++) {
      double f  = E - e * Math.Sin(E) - M;
      double fp = 1.0 - e * Math.Cos(E);
      double dE = f / fp;
      E -= dE;
      if (Math.Abs(dE) < 1e-12) break;
    }
    return E;
  }
}
