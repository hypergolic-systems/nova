namespace Nova.Core.Systems;

// ECS-style abstraction over the per-tick simulation work that
// `NovaVesselModule` orchestrates. Each system advances its own
// internal state at a cadence it controls — not all systems re-solve
// every physics tick, and the runner advances simulation time in
// dt-chunks bounded by the smallest `MaxTickDt()` across systems so
// nobody steps past a state change they need to react to.
//
// Implementations:
//   StagingFlowSystem  — water-fill solver for Topological resources.
//   ProcessFlowSystem  — LP solver for Uniform resources.
//   AccumulatorSystem  — hysteresis bridge between the two.
//
// Convention: a system's `Tick(dt)` must apply changes for exactly the
// requested dt. The runner is responsible for choosing dt; systems do
// not loop internally to "catch up" to wall-clock time.
public abstract class BackgroundSystem {

  // Apply this system's effects over a time step of `dt` seconds.
  // The current state (rates, activities, etc.) is assumed valid for
  // the entire interval — the runner has guaranteed it by clamping dt
  // to the minimum `MaxTickDt()` across systems.
  public abstract void Tick(double dt);

  // Maximum dt for which the current solved state is valid. Beyond
  // this horizon, a state change (buffer crossing a threshold, solar
  // shadow transition, accumulator flipping refill state, etc.)
  // requires re-solving. Default `+∞` means the system has no
  // forecasted state change.
  public virtual double MaxTickDt() => double.PositiveInfinity;

  // Re-solve called by the runner whenever a state change has
  // occurred that invalidates the current solution (e.g. demand
  // changed, topology changed, accumulator flipped). Idempotent.
  public abstract void Solve();

  // Mark the system's current solution as invalid; the runner will
  // call Solve() before the next Tick.
  public void Invalidate() { needsSolve = true; }

  protected bool needsSolve = true;
  public bool NeedsSolve => needsSolve;
}
