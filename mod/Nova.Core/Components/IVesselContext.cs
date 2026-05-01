using Nova.Core.Utils;

namespace Nova.Core.Components;

// Adapter over the live host vessel. Nova.Core can't reference KSP's
// Vessel / CelestialBody, so this interface exposes the vessel
// properties Nova.Core code needs (shadow calc, science). The mod
// side implements it as a thin wrapper over `vessel.mainBody.*`;
// tests provide a stub.
public interface IVesselContext {
  string                       BodyName        { get; }
  uint                         BodyId          { get; }
  Nova.Core.Science.Situation  Situation       { get; }
  double                       Altitude        { get; }
  double                       BodyYearSeconds { get; }
  double                       OrbitPeriod     { get; }
  double                       BodyRadius      { get; }
  bool                         OrbitingSun     { get; }

  // Time-dependent projections — used by ShadowCalculator. Take a
  // UT in seconds and return the vessel's / sun's relative position
  // at that time. Lets the shadow search look ahead for transitions.
  Vec3d VesselPositionAt(double ut);
  Vec3d SunDirectionAt(double ut);
}
