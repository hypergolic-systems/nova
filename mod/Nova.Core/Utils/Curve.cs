using System.Collections.Generic;

namespace Nova.Core.Utils;

/// <summary>
/// Piecewise-linear curve. Evaluates by interpolating between sorted keys.
/// Clamps to the first/last value outside the key range.
/// </summary>
public class Curve {
  private readonly SortedList<double, double> keys = new();

  public void AddKey(double t, double value) {
    keys[t] = value;
  }

  public double Evaluate(double t) {
    if (keys.Count == 0) return 0;
    if (keys.Count == 1) return keys.Values[0];

    if (t <= keys.Keys[0]) return keys.Values[0];
    if (t >= keys.Keys[keys.Count - 1]) return keys.Values[keys.Count - 1];

    for (int i = 0; i < keys.Count - 1; i++) {
      var k0 = keys.Keys[i];
      var k1 = keys.Keys[i + 1];
      if (t >= k0 && t <= k1) {
        var frac = (t - k0) / (k1 - k0);
        return keys.Values[i] + frac * (keys.Values[i + 1] - keys.Values[i]);
      }
    }

    return keys.Values[keys.Count - 1];
  }
}
