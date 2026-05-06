using System.Collections.Generic;
using Nova.Core.Utils;

namespace Nova.Core.Communications;

// Line-of-sight blocking geometry. A link from A to B is blocked by
// body C at time t when:
//   - the foot of the perpendicular from C to the line through A and
//     B falls within the segment (parametric t ∈ [0, 1]), AND
//   - the perpendicular distance is strictly less than C.Radius.
//
// Grazing convention: distance ≥ radius → clear. Pick once and keep
// — the bisection in LinkHorizon needs a stable definition for the
// state-change boundary.
public static class Occlusion {

  public static bool IsBlocked(Vec3d posA, Vec3d posB, Vec3d occluderCentre, double radius) {
    if (radius <= 0) return false;

    var ab = posB - posA;
    var lenSq = ab.SqrMagnitude;
    if (lenSq < 1e-18) return false;

    var ap = occluderCentre - posA;
    var t = Vec3d.Dot(ap, ab) / lenSq;
    if (t < 0 || t > 1) return false;

    var foot = posA + t * ab;
    var distSq = (occluderCentre - foot).SqrMagnitude;
    return distSq < radius * radius;
  }

  public static bool IsAnyBlocked(IReadOnlyList<Body> occluders, Vec3d posA, Vec3d posB, double ut) {
    if (occluders == null || occluders.Count == 0) return false;
    for (int i = 0; i < occluders.Count; i++) {
      var c = occluders[i];
      if (c == null) continue;
      var centre = AnalyticalPosition.BodyCentreAt(c, ut);
      if (IsBlocked(posA, posB, centre, c.Radius)) return true;
    }
    return false;
  }
}
