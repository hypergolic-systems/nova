namespace Nova.Core.Communications;

// Solver hint metadata attached to an Endpoint. The endpoint's
// PositionAt closure remains the universal evaluation path (used by
// numerical bisection, distance logging, and any caller that just
// wants a position at a UT); MotionModel is what the analytical
// solver inspects to decide whether it can compute relative motion
// in closed form.
//
// Discriminated-style hierarchy: Subclasses are inspected via
// pattern matching. A null MotionModel forces the numerical
// fallback path — always safe.
public abstract class MotionModel {
}

// Endpoint follows a Kepler orbit around `Parent`. Standard six
// orbital elements + epoch. Angles in radians (matches the
// engine's internal convention; KSP-side conversion happens at the
// addon boundary).
public sealed class KeplerMotion : MotionModel {
  public Body Parent;
  public double SemiMajorAxis;        // a (m)
  public double Eccentricity;         // e
  public double Inclination;          // i (rad)
  public double Lan;                  // Ω, longitude of ascending node (rad)
  public double ArgPe;                // ω, argument of periapsis (rad)
  public double MeanAnomalyAtEpoch;   // M₀ (rad)
  public double Epoch;                // UT at which M₀ is defined (s)
}

// Endpoint sits at a fixed point on the surface of `Parent`, rotating
// with the body. v1 dispatcher does NOT route SurfaceMotion to the
// analytical solver yet — it falls back to numerical via the closure.
// The data is captured day-one so the v2 surface↔Kepler solver can
// land without re-plumbing endpoints.
public sealed class SurfaceMotion : MotionModel {
  public Body Parent;
  public double LatitudeDeg;
  public double LongitudeDeg;
  public double AltitudeM;
}
