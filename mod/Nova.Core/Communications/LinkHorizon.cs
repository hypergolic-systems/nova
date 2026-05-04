using System;

namespace Nova.Core.Communications;

// Finds the next UT at which a link's quantised bucket index changes.
// The caller supplies a bucketAt(ut) callable that wraps the link's
// position + antenna math; this module owns only the search.
//
// Pattern mirrors ShadowCalculator.Compute: coarse uniform sweep over
// MaxHorizonSeconds, bisection to refine the crossing, conservative
// horizon-cap fallback if no crossing is seen. Two-craft separation
// has no orbital-period bound, so the horizon is a flat configurable.
public static class LinkHorizon {

  public static double NextBucketCrossing(double currentUT, Func<double, int> bucketAt) {
    int currentBucket = bucketAt(currentUT);

    double horizon = CommunicationsParameters.MaxHorizonSeconds;
    int steps = CommunicationsParameters.HorizonSearchSteps;
    double step = horizon / steps;
    double threshold = horizon * 1e-6;

    for (int i = 1; i <= steps; i++) {
      double t = currentUT + i * step;
      if (bucketAt(t) != currentBucket) {
        double lo = t - step;
        double hi = t;
        while (hi - lo > threshold) {
          double mid = (lo + hi) / 2;
          if (bucketAt(mid) == currentBucket) lo = mid;
          else hi = mid;
        }
        // Return hi, not the midpoint: hi is guaranteed to satisfy
        // bucketAt(hi) != currentBucket. The driver calling
        // Solve(NextEventUT) needs to land *past* the crossing, not
        // straddle it — otherwise the new solve sees the same bucket
        // and the event is a no-op.
        return hi;
      }
    }

    return currentUT + horizon;
  }
}
