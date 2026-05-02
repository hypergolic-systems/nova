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
  // Static atmospheric pressure at the vessel's current position, in
  // standard atmospheres (1 atm ≈ 101.325 kPa). 0 outside any
  // atmosphere. Used by AtmosphericProfile to compute layer-coverage
  // fidelity from the captured pressure range.
  double                       StaticPressureAtm { get; }
  double                       BodyYearSeconds { get; }
  double                       OrbitPeriod     { get; }
  double                       BodyRadius      { get; }
  bool                         OrbitingSun     { get; }
  // Body whose own parent is the Sun: walks `referenceBody` to root.
  // For Mun/Minmus → Kerbin; Gilly → Eve; Kerbin → Kerbin; Sun → Sun.
  // Used by the LTS orbit indicator to render the Sun-orbit ring whose
  // period gates the body-year (and hence the LTS slice cadence).
  string                       SolarParentName { get; }

  // Time-dependent projections — used by ShadowCalculator. Take a
  // UT in seconds and return the vessel's / sun's relative position
  // at that time. Lets the shadow search look ahead for transitions.
  Vec3d VesselPositionAt(double ut);
  Vec3d SunDirectionAt(double ut);
}
