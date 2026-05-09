namespace Nova.Core.Systems;

// Computation-event-driven simulation system. Owned by
// VirtualVessel; the vessel's runner calls `Solve()` whenever the
// system's solution needs refreshing (rates changed, topology
// changed, hysteresis fired, …) and reads `MaxTickDt()` to schedule
// the next forecasted re-solve.
//
// Systems do NOT have a per-frame Tick. State evolution between
// solves is whatever the system's data model encodes — for the
// resource solvers, that's the lerp Buffer model: Contents is a
// pure function of (BaselineContents, BaselineUT, Rate, clock.UT)
// evaluated lazily on read. The runner advances the shared SimClock
// directly; nothing accumulates per-tick work that requires every
// system to be poked.
//
// Implementations:
//   StagingFlowSystem  — water-fill solver for Topological resources.
//   ProcessFlowSystem  — LP solver for Uniform resources.
public abstract class BackgroundSystem {

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
  // call Solve() before the next clock advance.
  public void Invalidate() { needsSolve = true; }

  protected bool needsSolve = true;
  public bool NeedsSolve => needsSolve;
}
