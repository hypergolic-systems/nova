namespace Nova.Core.Systems;

// Vessel-local simulation clock. One SimClock per VirtualVessel,
// shared across the vessel's systems and buffers — every Buffer holds
// a reference to it so `Buffer.Contents` can lerp from baseline to
// "now" without requiring callers to plumb UT explicitly.
//
// Why per-vessel rather than a global Planetarium.GetUniversalTime():
//   • DeltaVSimulation clones the vessel and ticks the clone's clock
//     forward independently — the game clock keeps real-time, the
//     simulator runs as fast as it likes.
//   • Background vessels can have their clock paused (no rate × dt
//     accumulating between visits) and resumed when the orbit
//     scheduler decides it's their turn.
//   • Tests construct vessels with a frozen clock and step it
//     deterministically without touching KSP globals.
public class SimClock {
  // Universal time, seconds. Driven by VirtualVessel.Tick during live
  // play (= simulationTime) and by DeltaVSimulation.Burn during dV
  // calc (= burn-clock relative to the cloned vessel's spawn time).
  public double UT;
}
