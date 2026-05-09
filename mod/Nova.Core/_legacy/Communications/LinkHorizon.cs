using System;

namespace Nova.Core.Communications;

// Finds the next UT at which a discrete state changes. The caller
// supplies a stateAt(ut) callable returning an int — bucket index for
// rate quantisation, 0/1 for occlusion blocked/clear, or any other
// integer-valued state function. This module owns only the search.
//
// Pattern mirrors ShadowCalculator.Compute: coarse uniform sweep over
// MaxHorizonSeconds, bisection to refine the crossing, conservative
// horizon-cap fallback if no crossing is seen. Two-craft separation
// has no orbital-period bound, so the horizon is a flat configurable.
//
// The harness only finds the FIRST state change in the window —
// callers that need to track multiple parallel state functions
// (e.g. bucket transition + occlusion transition) should run the
// search per state and take the min, not OR-combine into one
// stateAt (which collapses adjacent transitions of distinct states
// into a single event).
public static class LinkHorizon {

  public static double NextDiscreteChange(double currentUT, Func<double, int> stateAt) {
    int currentState = stateAt(currentUT);

    double horizon = CommunicationsParameters.MaxHorizonSeconds;
    int steps = CommunicationsParameters.HorizonSearchSteps;
    double step = horizon / steps;
    double threshold = horizon * 1e-6;

    for (int i = 1; i <= steps; i++) {
      double t = currentUT + i * step;
      if (stateAt(t) != currentState) {
        double lo = t - step;
        double hi = t;
        while (hi - lo > threshold) {
          double mid = (lo + hi) / 2;
          if (stateAt(mid) == currentState) lo = mid;
          else hi = mid;
        }
        // Return hi, not the midpoint: hi is guaranteed to satisfy
        // stateAt(hi) != currentState. The driver calling
        // Solve(NextEventUT) needs to land *past* the crossing, not
        // straddle it — otherwise the new solve sees the same state
        // and the event is a no-op.
        return hi;
      }
    }

    return currentUT + horizon;
  }
}
