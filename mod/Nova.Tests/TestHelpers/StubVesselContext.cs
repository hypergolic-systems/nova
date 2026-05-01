using Nova.Core.Components;
using Nova.Core.Science;
using Nova.Core.Utils;

namespace Nova.Tests.TestHelpers;

// Plain-data stub of IVesselContext for tests. Mutable fields so tests
// can flip situation / body mid-test (e.g. simulating liftoff).
public class StubVesselContext : IVesselContext {
  public string    BodyName        { get; set; } = "Kerbin";
  public uint      BodyId          { get; set; } = 1;
  public Situation Situation       { get; set; } = Situation.SrfLanded;
  public double    Altitude        { get; set; }
  public double    BodyYearSeconds { get; set; } = 9_203_545;
  public double    OrbitPeriod     { get; set; } = double.PositiveInfinity;
  public double    BodyRadius      { get; set; } = 600_000;
  public bool      OrbitingSun     { get; set; }

  public Vec3d VesselPositionAt(double ut) => new Vec3d(0, 0, 0);
  public Vec3d SunDirectionAt(double ut)   => new Vec3d(1, 0, 0);
}
