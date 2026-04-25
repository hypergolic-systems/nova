using System;
using Nova.Core.Utils;
using System.Collections.Generic;

namespace Nova.Core.Resources;

public static class SolarOptimizer {

  public struct Panel {
    public Vec3d Direction;
    public double ChargeRate;
    public bool IsTracking;
  }

  private const int SampleCount = 200;

  /// <summary>
  /// Find the maximum total solar power achievable by choosing an optimal
  /// sun direction for the given panel layout. Uses Fibonacci sphere
  /// sampling over the unit sphere.
  /// </summary>
  public static double ComputeOptimalRate(IReadOnlyList<Panel> panels) {
    if (panels.Count == 0) return 0;

    double bestTotal = 0;

    for (int i = 0; i < SampleCount; i++) {
      var d = FibonacciSphereDirection(i, SampleCount);
      double total = 0;

      for (int j = 0; j < panels.Count; j++) {
        var p = panels[j];
        double dot = Vec3d.Dot(d, p.Direction);

        if (p.IsTracking) {
          // Tracking panel rotates around its axis to face the sun.
          // Output is proportional to the sun component perpendicular to the axis.
          double perpSq = 1.0 - dot * dot;
          if (perpSq > 0)
            total += p.ChargeRate * Math.Sqrt(perpSq);
        } else {
          // Fixed panel: output proportional to cosine of angle with normal.
          if (dot > 0)
            total += p.ChargeRate * dot;
        }
      }

      if (total > bestTotal)
        bestTotal = total;
    }

    return bestTotal;
  }

  /// <summary>
  /// Generate a point on the unit sphere using the Fibonacci spiral method.
  /// Produces a nearly uniform distribution of directions.
  /// </summary>
  public static Vec3d FibonacciSphereDirection(int index, int count) {
    double goldenAngle = Math.PI * (3.0 - Math.Sqrt(5.0));
    double y = 1.0 - (2.0 * index + 1.0) / count;
    double radius = Math.Sqrt(1.0 - y * y);
    double theta = goldenAngle * index;
    return new Vec3d(Math.Cos(theta) * radius, y, Math.Sin(theta) * radius);
  }
}
