using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nova.Core.Communications;

namespace Nova.Tests.Communications;

[TestClass]
public class LinkHorizonTests {

  // LinkHorizon owns only the search; the bucketAt closure encodes
  // whatever the link's geometry+antenna math produces. Tests stub
  // bucketAt directly, so these are pure search-algorithm tests.

  [TestMethod]
  public void Stationary_HorizonIsHorizonCap() {
    // Constant bucket → no crossing within search window → returns
    // currentUT + MaxHorizonSeconds.
    Func<double, int> bucketAt = _ => 5;
    var result = LinkHorizon.NextDiscreteChange(0, bucketAt);
    Assert.AreEqual(CommunicationsParameters.MaxHorizonSeconds, result, 1e-9);
  }

  [TestMethod]
  public void StepFunction_FindsCrossingNearStepUT() {
    // Bucket flips from 5 → 4 at UT = 1234.5. Bisection should land
    // within the bisection threshold (horizon · 1e-6 ≈ 0.086 s).
    const double crossingUT = 1234.5;
    Func<double, int> bucketAt = ut => ut < crossingUT ? 5 : 4;
    var result = LinkHorizon.NextDiscreteChange(0, bucketAt);
    var threshold = CommunicationsParameters.MaxHorizonSeconds * 1e-6;
    Assert.IsTrue(Math.Abs(result - crossingUT) < threshold,
      $"expected {crossingUT}, got {result} (threshold {threshold})");
  }

  [TestMethod]
  public void TwoCrossings_ReturnsFirstOnly() {
    // Bucket sequence: 5 (until 1000) → 4 (until 5000) → 3.
    // Should return the first crossing at UT ≈ 1000.
    Func<double, int> bucketAt = ut => ut < 1000 ? 5 : (ut < 5000 ? 4 : 3);
    var result = LinkHorizon.NextDiscreteChange(0, bucketAt);
    Assert.IsTrue(result < 2000, $"expected first crossing near 1000, got {result}");
    Assert.IsTrue(result > 999, $"expected first crossing near 1000, got {result}");
  }

  [TestMethod]
  public void CrossingPastHorizon_ReturnsHorizonCap() {
    // Crossing is at 100k seconds, past the 86400 default horizon.
    Func<double, int> bucketAt = ut => ut < 100_000 ? 5 : 4;
    var result = LinkHorizon.NextDiscreteChange(0, bucketAt);
    Assert.AreEqual(CommunicationsParameters.MaxHorizonSeconds, result, 1e-9);
  }

  [TestMethod]
  public void NonZeroCurrentUT_HorizonRelativeToIt() {
    // Crossing at absolute UT = 5000; currentUT = 1000. Should still
    // return absolute 5000, not 4000 or 6000.
    Func<double, int> bucketAt = ut => ut < 5000 ? 7 : 6;
    var result = LinkHorizon.NextDiscreteChange(1000, bucketAt);
    var threshold = CommunicationsParameters.MaxHorizonSeconds * 1e-6;
    Assert.IsTrue(Math.Abs(result - 5000) < threshold,
      $"expected ≈5000, got {result}");
  }

  [TestMethod]
  public void RisingBucket_AlsoTriggersCrossing() {
    // Approaching geometry: bucket 4 → 5 (rate increasing). Crossing
    // is still detected — direction doesn't matter.
    Func<double, int> bucketAt = ut => ut < 2000 ? 4 : 5;
    var result = LinkHorizon.NextDiscreteChange(0, bucketAt);
    var threshold = CommunicationsParameters.MaxHorizonSeconds * 1e-6;
    Assert.IsTrue(Math.Abs(result - 2000) < threshold,
      $"expected ≈2000, got {result}");
  }
}
