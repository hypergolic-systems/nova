using System;
using Nova.Core.Utils;

namespace Nova.Core.Resources;

public static class ShadowCalculator {

  public struct ShadowState {
    public bool InSunlight;
    public double NextTransitionUT;
  }

  private const int SearchSteps = 200;
  private const double MaxSearchHorizon = 86400;
  private const double HyperbolicThreshold = 0.1;
  private const double PeriodFraction = 1e-6;

  public static bool IsInShadow(Vec3d vesselPos, Vec3d sunPos, double bodyRadius) {
    var sunDir = sunPos.Normalized;
    double dot = Vec3d.Dot(vesselPos, sunDir);
    if (dot >= 0) return false;
    var perp = vesselPos - dot * sunDir;
    return perp.Magnitude < bodyRadius;
  }

  public static ShadowState Compute(Func<double, Vec3d> vesselPosition,
      Func<double, Vec3d> sunDirection, double orbitPeriod,
      double bodyRadius, bool orbitingSun, double currentUT) {
    if (vesselPosition == null || orbitingSun)
      return new ShadowState { InSunlight = true, NextTransitionUT = double.PositiveInfinity };

    var vesselPos = vesselPosition(currentUT);
    var sunPos = sunDirection(currentUT);
    bool inShadow = IsInShadow(vesselPos, sunPos, bodyRadius);

    bool hyperbolic = double.IsPositiveInfinity(orbitPeriod);
    double horizon = hyperbolic ? MaxSearchHorizon : orbitPeriod;
    double step = horizon / SearchSteps;
    double threshold = hyperbolic ? HyperbolicThreshold : horizon * PeriodFraction;

    for (int i = 1; i <= SearchSteps; i++) {
      double t = currentUT + i * step;
      bool shadow = IsInShadow(
        vesselPosition(t),
        sunDirection(t),
        bodyRadius);

      if (shadow != inShadow) {
        double lo = t - step;
        double hi = t;
        while (hi - lo > threshold) {
          double mid = (lo + hi) / 2;
          if (IsInShadow(
              vesselPosition(mid),
              sunDirection(mid),
              bodyRadius) == inShadow)
            lo = mid;
          else
            hi = mid;
        }
        return new ShadowState {
          InSunlight = !inShadow,
          NextTransitionUT = (lo + hi) / 2,
        };
      }
    }

    return new ShadowState {
      InSunlight = !inShadow,
      NextTransitionUT = currentUT + horizon,
    };
  }
}
