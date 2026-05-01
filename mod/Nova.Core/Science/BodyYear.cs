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
    if (string.IsNullOrEmpty(bodyName))
      throw new ArgumentException("bodyName required", nameof(bodyName));

    string parent = getParentBody(bodyName);
    if (parent == null) {
      // bodyName itself is the root (Sun). Degenerate case — return whatever
      // the caller's table says.
      return getOrbitPeriod(bodyName);
    }

    string current = bodyName;
    while (getParentBody(parent) != null) {
      // parent is itself an orbiter — current is a moon (or moon-of-moon).
      // Walk up until current is a planet (parent has no parent).
      current = parent;
      parent = getParentBody(current);
    }
    // parent has no parent → parent is the Sun → current orbits the Sun.
    return getOrbitPeriod(current);
  }
}
