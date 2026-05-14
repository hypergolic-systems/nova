using Nova.Core.Components;
using Nova.Core.Science;
using Nova.Core.Utils;

namespace Nova.Sim.Universe;

// IVesselContext implementation for a simulated vessel. Reads body
// data from BodyData and derives orbital state on demand from a
// stored circular vessel orbit around `BodyName`.
//
// Simplifications for v1 of the sim:
//   - Vessel orbit is circular at `Altitude` above the body surface.
//     A future iteration can carry the full orbital elements per
//     vessel; today the only sim-side use cases (shadow / science /
//     comms range) read period + body radius.
//   - Situation defaults to InSpaceLow; the user can override via
//     UDP eval if a different situation is required.
//   - StaticPressureAtm is 0 (vacuum). Atmospheric experiments stay
//     dormant until that's wired up to a real atmosphere model.
public sealed class SimVesselContext : IVesselContext {
  public string                       BodyName        { get; set; } = "Kerbin";
  public uint                         BodyId          { get; set; }
  public Situation                    Situation       { get; set; } = Situation.InSpaceLow;
  public double                       Altitude        { get; set; } = 100_000;
  public double                       StaticPressureAtm { get; set; }
  public double                       BodyYearSeconds { get; private set; }
  public double                       OrbitPeriod     { get; private set; }
  public double                       BodyRadius      { get; private set; }
  public bool                         OrbitingSun     { get; private set; }
  public string                       SolarParentName { get; private set; } = "Kerbin";

  public SimVesselContext() {
    Refresh();
  }

  public void SetOrbit(string bodyName, double altitudeMeters) {
    BodyName = bodyName;
    Altitude = altitudeMeters;
    Refresh();
  }

  private void Refresh() {
    var body = BodyData.Get(BodyName);
    if (body == null) {
      BodyRadius = 0;
      OrbitPeriod = 0;
      BodyYearSeconds = 0;
      OrbitingSun = false;
      return;
    }
    BodyRadius      = body.Radius;
    BodyYearSeconds = body.YearSeconds > 0 ? body.YearSeconds : 0;
    OrbitingSun     = body.Parent == "Sun";
    SolarParentName = BodyData.SolarParentOf(BodyName);

    double semiMajor = body.Radius + Altitude;
    OrbitPeriod = OrbitMath.Period(body.GM, semiMajor);
  }

  // For the vessel-position-at-UT projection used by ShadowCalculator,
  // we treat the vessel as orbiting at `Altitude` in a prograde
  // circular orbit. The orbital plane lies in the body's equator;
  // good enough for shadow occlusion, where what matters is the
  // periodicity and the body's radius relative to the vessel orbit.
  public Vec3d VesselPositionAt(double ut) {
    var body = BodyData.Get(BodyName);
    if (body == null) return Vec3d.Zero;
    double r = body.Radius + Altitude;
    if (OrbitPeriod <= 0) return new Vec3d(r, 0, 0);
    double theta = 2.0 * System.Math.PI * (ut / OrbitPeriod);
    return new Vec3d(r * System.Math.Cos(theta), r * System.Math.Sin(theta), 0);
  }

  // Direction from vessel toward the Sun. The sun sits at the
  // heliocentric origin; vessel's heliocentric position is the body's
  // heliocentric position plus the vessel's local offset. For shadow
  // purposes the offset is negligible relative to AU-scale distances,
  // but we include it for completeness.
  public Vec3d SunDirectionAt(double ut) {
    var bodyHelio = OrbitMath.HeliocentricPosition(BodyName, ut);
    var vesselLocal = VesselPositionAt(ut);
    var vesselHelio = bodyHelio + vesselLocal;
    return (-vesselHelio).Normalized;
  }
}
