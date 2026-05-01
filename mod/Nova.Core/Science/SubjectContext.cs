namespace Nova.Core.Science;

// Primitive-only runtime snapshot. Built mod-side from live KSP types
// (Vessel, CelestialBody) and passed to experiments to ask "what subject
// am I observing right now?". No KSP types here — Nova.Core stays clean.
public readonly struct SubjectContext {
  public uint      BodyId          { get; }
  public string    BodyName        { get; }
  public Situation Situation       { get; }
  public double    Altitude        { get; }     // meters above sea level
  public double    Pressure        { get; }     // atm (1.0 = surface Kerbin)
  public double    UT              { get; }     // universal time, seconds
  public double    BodyYearSeconds { get; }     // resolved via BodyYear walk-to-root

  public SubjectContext(
      uint bodyId, string bodyName, Situation situation,
      double altitude, double pressure, double ut, double bodyYearSeconds) {
    BodyId          = bodyId;
    BodyName        = bodyName;
    Situation       = situation;
    Altitude        = altitude;
    Pressure        = pressure;
    UT              = ut;
    BodyYearSeconds = bodyYearSeconds;
  }
}
