using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nova.Core.Communications;

namespace Nova.Tests.Communications;

[TestClass]
public class RateBucketsTests {

  // The default BucketCount in CommunicationsParameters is 10; tests
  // assume that. If the default ever changes, the assertion arithmetic
  // here will need to track it.

  [TestMethod]
  public void Quantize_BelowKnee_RoundsDownToBucketFloor() {
    // shannon = 0.55, ceiling = 1000 → bucket index 5, floor = 500.
    Assert.AreEqual(500, RateBuckets.Quantize(550, 1000), 1e-9);
  }

  [TestMethod]
  public void Quantize_AboveKnee_ReturnsFullCeiling() {
    // rate ≥ ceiling lands in the above-knee bucket (index N), which
    // reports the full hardware ceiling — not (N-1)/N · ceiling.
    Assert.AreEqual(1000, RateBuckets.Quantize(1000, 1000), 1e-9);
    Assert.AreEqual(1000, RateBuckets.Quantize(1500, 1000), 1e-9);
    Assert.AreEqual(1000, RateBuckets.Quantize(1e9,  1000), 1e-9);
  }

  [TestMethod]
  public void Quantize_AtBoundary_FallsIntoUpperBucket() {
    // shannon exactly 0.7 → floor(7.0) = 7 → bucket 7, floor 700.
    // The boundary belongs to the higher-index bucket, not the lower.
    Assert.AreEqual(700, RateBuckets.Quantize(700, 1000), 1e-9);
  }

  [TestMethod]
  public void Quantize_Zero_ReturnsZero() {
    Assert.AreEqual(0, RateBuckets.Quantize(0, 1000));
  }

  [TestMethod]
  public void Quantize_NegativeRate_ReturnsZero() {
    Assert.AreEqual(0, RateBuckets.Quantize(-50, 1000));
  }

  [TestMethod]
  public void Quantize_DegenerateCeiling_ReturnsZero() {
    Assert.AreEqual(0, RateBuckets.Quantize(100, 0));
    Assert.AreEqual(0, RateBuckets.Quantize(100, -1));
  }

  [TestMethod]
  public void BucketIndex_AboveKnee_IsBucketCount() {
    // Above-knee bucket is N (one past the highest sub-knee index).
    var n = CommunicationsParameters.BucketCount;
    Assert.AreEqual(n, RateBuckets.BucketIndex(1000, 1000));
    Assert.AreEqual(n, RateBuckets.BucketIndex(2e9, 1000));
  }

  [TestMethod]
  public void BucketIndex_SubKneeMonotone() {
    // Bucket index is monotone non-decreasing in rate within sub-knee.
    int prev = -1;
    for (int i = 0; i <= 100; i++) {
      var idx = RateBuckets.BucketIndex(i * 9.99, 1000); // up to 999, all sub-knee
      Assert.IsTrue(idx >= prev, $"bucket regressed at rate {i*9.99}: {idx} < {prev}");
      prev = idx;
    }
  }
}
