using System;
using System.Linq;
using Nova.Core.Components;
using Nova.Core.Science;
using Nova.Core.Utils;

namespace Nova.Components;

// Live adapter from KSP's Vessel + CelestialBody onto Nova.Core's
// IVesselContext. Each property reads through to KSP — no caching,
// no sync hazard. NovaVesselModule attaches one of these to the
// VirtualVessel on first FixedUpdate.
internal sealed class KspVesselContext : IVesselContext {
  private readonly Vessel vessel;

  public KspVesselContext(Vessel vessel) {
    this.vessel = vessel;
  }

  public string    BodyName => vessel.mainBody.bodyName;
  public uint      BodyId   => (uint)vessel.mainBody.flightGlobalsIndex;
  public Situation Situation => (Situation)(int)ScienceUtil.GetExperimentSituation(vessel);
  public double    Altitude  => vessel.altitude;
  // KSP exposes static pressure in kPa; convert to standard atmospheres
  // (1 atm = 101.325 kPa) so the science layer computes coverage in
  // physically meaningful units.
  public double    StaticPressureAtm => vessel.staticPressurekPa / 101.325;

  public double BodyYearSeconds => ResolveBodyYear(vessel.mainBody);

  public double OrbitPeriod => vessel.orbit != null && vessel.orbit.eccentricity < 1.0
      ? vessel.orbit.period
      : double.PositiveInfinity;

  public double BodyRadius  => vessel.mainBody.Radius;
  public bool   OrbitingSun => vessel.mainBody == FlightGlobals.Bodies[0];

  public string SolarParentName => ResolveSolarParent(vessel.mainBody);

  public Vec3d VesselPositionAt(double ut) {
    var pos = vessel.orbit.getRelativePositionAtUT(ut).xzy;
    return new Vec3d(pos.x, pos.y, pos.z);
  }

  public Vec3d SunDirectionAt(double ut) {
    var sunPos  = FlightGlobals.Bodies[0].getTruePositionAtUT(ut);
    var bodyPos = vessel.mainBody.getTruePositionAtUT(ut);
    var rel = sunPos - bodyPos;
    return new Vec3d(rel.x, rel.y, rel.z);
  }

  private static double ResolveBodyYear(CelestialBody body) =>
      BodyYear.For(body.bodyName, ParentLookup(), PeriodLookup());

  private static string ResolveSolarParent(CelestialBody body) =>
      BodyYear.SolarParentOf(body.bodyName, ParentLookup());

  private static Func<string, string> ParentLookup() {
    var byName = FlightGlobals.Bodies.ToDictionary(b => b.bodyName, b => b);
    return bn => {
      var b = byName[bn];
      var rb = b.referenceBody;
      return rb == null || rb == b ? null : rb.bodyName;
    };
  }

  private static Func<string, double> PeriodLookup() {
    var byName = FlightGlobals.Bodies.ToDictionary(b => b.bodyName, b => b);
    return bn => byName[bn].orbit?.period ?? 0;
  }
}
