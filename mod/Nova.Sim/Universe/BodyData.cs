using System.Collections.Generic;

namespace Nova.Sim.Universe;

// Hardcoded Kerbol-system body catalogue. Values sourced from the
// stock KSP body definitions (KSP wiki / Assembly-CSharp body configs).
//
// Only the fields Nova.Core consumes via IVesselContext + the orbit
// propagator are populated:
//   - GM        — gravitational parameter (m³/s²)
//   - Radius    — body radius (m)
//   - YearSeconds — sidereal orbital period around parent (s)
//   - Parent    — orbital parent body name; null for the Sun
//   - Orbit     — (SMA, ECC, INC, LAN, AoP, MNA0) at epoch UT=0
//
// Bodies orbit their parent on a Keplerian ellipse. Composing
// positions: walk parent chain summing positions. The Sun sits at
// the origin of the heliocentric frame.
public static class BodyData {
  public sealed class Body {
    public string Name;
    public string Parent;        // null = top-level (Sun)
    public double GM;            // m^3/s^2
    public double Radius;        // m
    public double YearSeconds;   // sidereal orbital period around Parent
    public OrbitElements Orbit;  // at epoch (UT=0)
  }

  public struct OrbitElements {
    public double SemiMajorAxis;       // m
    public double Eccentricity;
    public double InclinationDeg;
    public double LongitudeOfAscDeg;   // longitude of ascending node
    public double ArgPeriapsisDeg;
    public double MeanAnomalyAtEpoch;  // radians at UT=0
  }

  private static readonly Dictionary<string, Body> _byName = BuildCatalog();

  public static Body Get(string name) {
    return _byName.TryGetValue(name, out var b) ? b : null;
  }

  public static IEnumerable<Body> All => _byName.Values;

  // Walk parent chain to the body whose own parent is the Sun (or
  // returns the body itself if it already orbits the Sun, or null).
  // For Mun/Minmus → Kerbin; for Kerbin → Kerbin; for Sun → Sun.
  public static string SolarParentOf(string bodyName) {
    var b = Get(bodyName);
    if (b == null) return bodyName;
    if (b.Parent == null) return b.Name; // the Sun itself
    var p = Get(b.Parent);
    if (p == null) return b.Name;
    if (p.Parent == null) return b.Name; // already orbits the Sun
    return SolarParentOf(b.Parent);
  }

  private static Dictionary<string, Body> BuildCatalog() {
    var d = new Dictionary<string, Body>();

    // Kerbol (the Sun) — origin of the heliocentric frame.
    d["Sun"] = new Body {
      Name = "Sun", Parent = null,
      GM = 1.1723328e18, Radius = 261_600_000,
      YearSeconds = 0,
      Orbit = default,
    };

    // Kerbin — main playable planet. Circular orbit, equatorial.
    d["Kerbin"] = new Body {
      Name = "Kerbin", Parent = "Sun",
      GM = 3.5316e12, Radius = 600_000,
      YearSeconds = 9_203_544.6,
      Orbit = new OrbitElements {
        SemiMajorAxis      = 13_599_840_256,
        Eccentricity       = 0,
        InclinationDeg     = 0,
        LongitudeOfAscDeg  = 0,
        ArgPeriapsisDeg    = 0,
        MeanAnomalyAtEpoch = 3.14, // KSP's stock starting phase
      },
    };

    d["Mun"] = new Body {
      Name = "Mun", Parent = "Kerbin",
      GM = 6.5138398e10, Radius = 200_000,
      YearSeconds = 138_984.38,
      Orbit = new OrbitElements {
        SemiMajorAxis      = 12_000_000,
        Eccentricity       = 0,
        InclinationDeg     = 0,
        LongitudeOfAscDeg  = 0,
        ArgPeriapsisDeg    = 0,
        MeanAnomalyAtEpoch = 1.7,
      },
    };

    d["Minmus"] = new Body {
      Name = "Minmus", Parent = "Kerbin",
      GM = 1.7658e9, Radius = 60_000,
      YearSeconds = 1_077_311,
      Orbit = new OrbitElements {
        SemiMajorAxis      = 47_000_000,
        Eccentricity       = 0,
        InclinationDeg     = 6.0,
        LongitudeOfAscDeg  = 78.0,
        ArgPeriapsisDeg    = 38.0,
        MeanAnomalyAtEpoch = 0.9,
      },
    };

    d["Duna"] = new Body {
      Name = "Duna", Parent = "Sun",
      GM = 3.0136321e11, Radius = 320_000,
      YearSeconds = 17_315_400,
      Orbit = new OrbitElements {
        SemiMajorAxis      = 20_726_155_264,
        Eccentricity       = 0.051,
        InclinationDeg     = 0.06,
        LongitudeOfAscDeg  = 135.5,
        ArgPeriapsisDeg    = 0,
        MeanAnomalyAtEpoch = 3.14,
      },
    };

    d["Ike"] = new Body {
      Name = "Ike", Parent = "Duna",
      GM = 1.8568369e10, Radius = 130_000,
      YearSeconds = 65_517.86,
      Orbit = new OrbitElements {
        SemiMajorAxis      = 3_200_000,
        Eccentricity       = 0.03,
        InclinationDeg     = 0.2,
        LongitudeOfAscDeg  = 0,
        ArgPeriapsisDeg    = 0,
        MeanAnomalyAtEpoch = 1.7,
      },
    };

    d["Eve"] = new Body {
      Name = "Eve", Parent = "Sun",
      GM = 8.1717302e12, Radius = 700_000,
      YearSeconds = 5_657_995,
      Orbit = new OrbitElements {
        SemiMajorAxis      = 9_832_684_544,
        Eccentricity       = 0.01,
        InclinationDeg     = 2.1,
        LongitudeOfAscDeg  = 15.0,
        ArgPeriapsisDeg    = 0,
        MeanAnomalyAtEpoch = 3.14,
      },
    };

    // Add more bodies (Jool, Eeloo, Moho, Dres, etc.) as the sim needs
    // them. Today only Kerbin-system + a couple neighbours are loaded;
    // the sim refuses to set a vessel orbit around a body not in this
    // catalogue.

    return d;
  }
}
