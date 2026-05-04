using System;
using Nova.Core.Utils;

namespace Nova.Core.Communications;

// Closed-form position(t) evaluators for Kepler orbits. Used by the
// analytical horizon solver as a drop-in replacement for KSP's
// iterative Orbit.getTruePositionAtUT — same answer for circular
// orbits, ~1700× cheaper per call.
//
// v1: circular only (Eccentricity == 0). Eccentric requires solving
// Kepler's equation per call; falls back to numerical bisection.
public static class KeplerEvaluator {

  // Position relative to the parent body's centre, in the parent's
  // inertial frame, at time `ut`. Formula: rotate the in-plane
  // (cosθ, sinθ, 0) vector by R_z(Ω) · R_x(i) and scale by `a`.
  // For circular orbits, true anomaly θ equals mean anomaly modulo
  // a constant offset (ArgPe + M₀ at epoch).
  //
  // Throws if Eccentricity != 0 — caller must dispatch elliptic
  // orbits to the numerical fallback.
  public static Vec3d PositionAt(KeplerMotion k, double ut) {
    if (k.Eccentricity != 0)
      throw new InvalidOperationException(
        "KeplerEvaluator only handles circular orbits (e=0); " +
        "elliptic must dispatch to numerical bisection.");
    var n = Math.Sqrt(k.Parent.Mu / (k.SemiMajorAxis * k.SemiMajorAxis * k.SemiMajorAxis));
    var theta = k.ArgPe + k.MeanAnomalyAtEpoch + n * (ut - k.Epoch);
    var cosT = Math.Cos(theta);
    var sinT = Math.Sin(theta);
    var cosOm = Math.Cos(k.Lan);
    var sinOm = Math.Sin(k.Lan);
    var cosI = Math.Cos(k.Inclination);
    var sinI = Math.Sin(k.Inclination);
    var a = k.SemiMajorAxis;
    return new Vec3d(
      a * (cosOm * cosT - sinOm * cosI * sinT),
      a * (sinOm * cosT + cosOm * cosI * sinT),
      a * (sinI * sinT));
  }
}
