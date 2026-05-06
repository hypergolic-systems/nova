using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nova.Core.Communications;
using Nova.Core.Utils;

namespace Nova.Tests.Communications;

// Pure-geometry tests: chord-blocked-by-sphere predicate. Decoupled
// from any orbital motion or body chain — Vec3d positions in, bool
// out. Edge cases: foot-on-segment vs foot-outside-segment, grazing,
// degenerate coincident endpoints, multi-occluder short-circuit.
[TestClass]
public class OcclusionTests {

  [TestMethod]
  public void Foot_OnSegment_DistanceLessThanRadius_Blocks() {
    // Chord A=(-10,0,0) to B=(10,0,0); body at origin, radius 2.
    // Foot is the origin, distance 0 < 2 → blocked.
    var a = new Vec3d(-10, 0, 0);
    var b = new Vec3d(10, 0, 0);
    var c = new Vec3d(0, 0, 0);
    Assert.IsTrue(Occlusion.IsBlocked(a, b, c, 2));
  }

  [TestMethod]
  public void Foot_OnSegment_DistanceGreaterThanRadius_Clear() {
    // Body offset +3 in Y; perpendicular distance is 3, radius 2.
    var a = new Vec3d(-10, 0, 0);
    var b = new Vec3d(10, 0, 0);
    var c = new Vec3d(0, 3, 0);
    Assert.IsFalse(Occlusion.IsBlocked(a, b, c, 2));
  }

  [TestMethod]
  public void Foot_OutsideSegment_PastEndpointB_Clear() {
    // Foot lies at t=1.5, beyond B. Even with body close to the
    // line and small radius, the segment-distance check rejects it.
    var a = new Vec3d(-10, 0, 0);
    var b = new Vec3d(10, 0, 0);
    var c = new Vec3d(15, 0.1, 0);
    Assert.IsFalse(Occlusion.IsBlocked(a, b, c, 2));
  }

  [TestMethod]
  public void Foot_OutsideSegment_BeforeEndpointA_Clear() {
    var a = new Vec3d(-10, 0, 0);
    var b = new Vec3d(10, 0, 0);
    var c = new Vec3d(-15, 0.1, 0);
    Assert.IsFalse(Occlusion.IsBlocked(a, b, c, 2));
  }

  [TestMethod]
  public void Grazing_DistanceEqualsRadius_Clear() {
    // Convention: distance >= radius → clear. Pin it.
    var a = new Vec3d(-10, 0, 0);
    var b = new Vec3d(10, 0, 0);
    var c = new Vec3d(0, 2, 0);
    Assert.IsFalse(Occlusion.IsBlocked(a, b, c, 2));
  }

  [TestMethod]
  public void CoincidentEndpoints_ReturnsClear() {
    // Degenerate: A == B; no chord. Defined as not blocked even if
    // the body engulfs the point (no link to obstruct anyway).
    var a = new Vec3d(0, 0, 0);
    var b = new Vec3d(0, 0, 0);
    var c = new Vec3d(0, 0, 0);
    Assert.IsFalse(Occlusion.IsBlocked(a, b, c, 5));
  }

  [TestMethod]
  public void IsAnyBlocked_EmptyOccluders_ReturnsFalse() {
    var a = new Vec3d(-10, 0, 0);
    var b = new Vec3d(10, 0, 0);
    Assert.IsFalse(Occlusion.IsAnyBlocked(new List<Body>(), a, b, 0));
    Assert.IsFalse(Occlusion.IsAnyBlocked(null, a, b, 0));
  }

  [TestMethod]
  public void IsAnyBlocked_OnlyOneOccluderBlocks_ReturnsTrue() {
    // Two-body fixture: only the second is on the chord. We need a
    // body sitting at origin with radius covering the chord. Use
    // root bodies (Parent=null) so BodyCentreAt returns Vec3d.Zero.
    var clear = new Body { Id = "Far", Radius = 1, Parent = null };
    var blocker = new Body { Id = "Block", Radius = 5, Parent = null };
    var a = new Vec3d(-10, 0, 0);
    var b = new Vec3d(10, 0, 0);
    Assert.IsTrue(Occlusion.IsAnyBlocked(new List<Body> { clear, blocker }, a, b, 0));
    Assert.IsTrue(Occlusion.IsAnyBlocked(new List<Body> { blocker, clear }, a, b, 0));
  }

  [TestMethod]
  public void IsAnyBlocked_NoOccluderBlocks_ReturnsFalse() {
    // Both bodies at origin but radii too small to reach the chord
    // (chord passes through origin so this case actually blocks if
    // radius > 0 — use offset chord instead).
    var bodyA = new Body { Id = "A", Radius = 0.5, Parent = null };
    var bodyB = new Body { Id = "B", Radius = 0.5, Parent = null };
    var a = new Vec3d(-10, 5, 0);
    var b = new Vec3d(10, 5, 0);
    Assert.IsFalse(Occlusion.IsAnyBlocked(new List<Body> { bodyA, bodyB }, a, b, 0));
  }
}
