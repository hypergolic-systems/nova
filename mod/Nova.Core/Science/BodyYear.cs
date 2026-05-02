using System;

namespace Nova.Core.Science;

// "How long is a year on this body, where 'year' = its solar orbital period."
//
// For a body that orbits the Sun directly (Kerbin, Eve, Duna): just its
// own orbital period. For a moon (Mun, Gilly): walk up the orbital
// hierarchy until we find the body whose parent IS the root, and return
// that body's period — i.e. a year on Mun = a year on Kerbin = ~9.2 Ms.
//
// Pure-data helper: callers (mod side) supply the parent + period
// lookups since Nova.Core can't reference KSP's CelestialBody.
public static class BodyYear {

  public static double For(
      string bodyName,
      Func<string, string> getParentBody,
      Func<string, double> getOrbitPeriod) {
    string solarChild = SolarParentOf(bodyName, getParentBody);
    return getOrbitPeriod(solarChild);
  }

  // Walks `bodyName` up its orbit hierarchy until the body whose parent
  // is the root (the Sun). For Mun → Kerbin; Gilly → Eve; Kerbin →
  // Kerbin; Sun → Sun.
  public static string SolarParentOf(
      string bodyName,
      Func<string, string> getParentBody) {
    if (string.IsNullOrEmpty(bodyName))
      throw new ArgumentException("bodyName required", nameof(bodyName));

    string parent = getParentBody(bodyName);
    if (parent == null) return bodyName;     // already the root

    string current = bodyName;
    while (getParentBody(parent) != null) {
      current = parent;
      parent = getParentBody(current);
    }
    return current;
  }
}
