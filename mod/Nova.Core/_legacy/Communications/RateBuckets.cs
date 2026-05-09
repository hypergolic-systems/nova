using System;

namespace Nova.Core.Communications;

// Quantises continuous link rates onto a fixed bucket grid so that
// allocator and routing decisions remain stable across small geometry
// changes. The Shannon factor (rate/ceiling) ∈ [0, 1] is partitioned
// into N = CommunicationsParameters.BucketCount equal slices, plus a
// distinguished above-knee bucket (index N) for rates clamped to the
// hardware ceiling.
//
//   Bucket k ∈ [0, N-1]:  shannon ∈ [k/N, (k+1)/N),  floor = ceiling · k/N
//   Bucket N:             rate ≥ ceiling,            floor = ceiling
//
// Reported rate is the bucket floor — conservative within each
// sub-knee bucket so the allocator never over-grants what geometry
// won't sustain until the next solve. The above-knee bucket reports
// full ceiling because rate is hardware-clamped, not geometry-limited.
public static class RateBuckets {

  // Bucket index for a continuous rate against the link ceiling.
  //   0           if ceiling ≤ 0 (degenerate link)
  //   N           if rate ≥ ceiling (above-knee)
  //   floor(N·r/c) otherwise, clamped to [0, N-1]
  public static int BucketIndex(double rate, double ceiling) {
    if (ceiling <= 0) return 0;
    var n = CommunicationsParameters.BucketCount;
    if (rate >= ceiling) return n;
    if (rate <= 0) return 0;
    var idx = (int)Math.Floor(rate / ceiling * n);
    if (idx < 0) return 0;
    if (idx >= n) return n - 1;  // floor of a sub-knee shannon never exceeds N-1
    return idx;
  }

  // Bucket-floor rate. Above-knee returns full ceiling (rate is
  // hardware-clamped); sub-knee returns ceiling · k/N.
  public static double Quantize(double rate, double ceiling) {
    if (ceiling <= 0) return 0;
    var n = CommunicationsParameters.BucketCount;
    var idx = BucketIndex(rate, ceiling);
    if (idx >= n) return ceiling;
    return ceiling * idx / n;
  }
}
