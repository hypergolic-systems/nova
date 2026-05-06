using System;
using System.Collections.Generic;
using Nova.Core.Communications;

namespace Nova.Communications;

// Cache: KSP CelestialBody → Nova.Core Body. Built lazily on demand.
// Each Body's Parent and OrbitAroundParent are populated recursively
// so the body chain (Sun → planet → moon) is intact, ready for the
// future cross-SOI hierarchical analytical solver.
//
// One registry per flight scene; rebuilt each scene load.
public static class NovaBodyRegistry {

  private static readonly Dictionary<CelestialBody, Body> cache = new();

  public static void Reset() {
    cache.Clear();
  }

  // Returns the Nova Body wrapping `kspBody`. Fills in the chain
  // upward to the root (Sun) on first call.
  public static Body For(CelestialBody kspBody) {
    if (kspBody == null) return null;
    if (cache.TryGetValue(kspBody, out var existing)) return existing;

    var body = new Body {
      Id = kspBody.bodyName,
      Mu = kspBody.gravParameter,
      Radius = kspBody.Radius,
      RotationPeriod = kspBody.rotationPeriod,
      InitialRotationDeg = kspBody.initialRotation,
    };
    cache[kspBody] = body;  // insert before recursing — handles any cycle defensively

    if (kspBody.referenceBody != null && kspBody.referenceBody != kspBody) {
      body.Parent = For(kspBody.referenceBody);
      body.OrbitAroundParent = ExtractKepler(kspBody.orbit, body.Parent);
      // Mirror the parent linkage in the children index so OccluderSet
      // can walk subtrees. Idempotent — guard against double-add when
      // a body is touched again via a sibling's chain.
      if (body.Parent != null && !body.Parent.Children.Contains(body))
        body.Parent.Children.Add(body);
    }
    return body;
  }

  // Walk every body in FlightGlobals.Bodies once so the children index
  // is fully populated before any link is built. The lazy For() path
  // only fills bodies on already-discovered chains, which is fine for
  // the analytical position solver but leaves siblings unlinked under
  // their parent — OccluderSet's subtree enumeration needs the whole
  // tree. Cheap one-shot pass; safe to call repeatedly.
  public static void WarmAll() {
    if (FlightGlobals.Bodies == null) return;
    foreach (var b in FlightGlobals.Bodies) For(b);
  }

  // Snapshot a KSP Orbit into a Nova KeplerMotion. Used both for body
  // chain construction and for vessel orbital element extraction.
  public static KeplerMotion ExtractKepler(Orbit orbit, Body parent) {
    if (orbit == null || parent == null) return null;
    return new KeplerMotion {
      Parent = parent,
      SemiMajorAxis = orbit.semiMajorAxis,
      Eccentricity = orbit.eccentricity,
      Inclination = orbit.inclination * Math.PI / 180.0,
      Lan = orbit.LAN * Math.PI / 180.0,
      ArgPe = orbit.argumentOfPeriapsis * Math.PI / 180.0,
      MeanAnomalyAtEpoch = orbit.meanAnomalyAtEpoch,
      Epoch = orbit.epoch,
    };
  }
}
