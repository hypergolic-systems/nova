using System.Collections.Generic;

namespace Nova.Core.Communications;

// A celestial body in the comms simulation. Bodies form a tree (Sun is
// root; planets orbit Sun; moons orbit planets); each non-root body
// has a parent and a Kepler orbit around that parent. The chain is
// what the cross-SOI hierarchical-decomposition path will walk.
//
// Lives in Nova.Core (no KSP types). The KSP integration layer
// builds these from FlightGlobals.Bodies once per scene and caches
// them keyed by KSP CelestialBody.
public sealed class Body {

  // Stable identifier. KSP integration uses the celestial body's
  // bodyName; tests use a free-form string.
  public string Id;

  // Standard gravitational parameter (m³/s²). G·M of the body.
  // Drives Kepler mean motion: n = sqrt(Mu / a³).
  public double Mu;

  // Equatorial radius (m). Used for surface positioning and (later)
  // body-LOS occlusion checks.
  public double Radius;

  // Sidereal rotation period (s). For surface-fixed endpoints
  // (ground stations), the body-relative position rotates at
  // 2π/RotationPeriod in inertial coordinates.
  public double RotationPeriod;

  // Body's rotation phase at UT=0 (degrees). KSP convention:
  // rotationAngle(ut) = (initialRotation + 360°·ut/period) % 360°.
  // Matters for placing surface-fixed endpoints correctly in the
  // inertial frame the analytical solver shares with vessel orbits.
  public double InitialRotationDeg;

  // Parent body for the orbit chain (null = root, e.g. the Sun).
  public Body Parent;

  // This body's Kepler orbit around Parent. Null iff Parent is null
  // (the root body has no orbit). The cross-SOI hierarchical solver
  // walks Parent recursively, summing Kepler positions along the way.
  public KeplerMotion OrbitAroundParent;

  // Direct children in the SOI tree — bodies whose Parent is this
  // body. Populated by the KSP-side registry (NovaBodyRegistry) when
  // each child registers, and by hand-built test fixtures. Used by
  // OccluderSet to enumerate the subtree of a penultimate body.
  // Defaults to empty so existing fixtures that don't wire children
  // continue to work.
  public List<Body> Children = new();
}
