namespace Nova.Core.Communications;

// One directed edge in the comms graph: From transmits to To.
// A symmetric bidirectional link surfaces as two Link records (rate
// may differ in each direction when antenna specs differ).
//
// Capacity (RateBps) is set at Solve time from the best (tx, rx)
// antenna-pair geometry; UsedBps is filled in afterwards by the
// allocator so callers can read both off the same snapshot.
public class Link {

  public Endpoint From { get; }
  public Endpoint To { get; }
  public double DistanceM { get; }
  public double Snr { get; }

  // Effective rate after both bucket quantisation and occlusion
  // gating. 0 when Blocked is true (line-of-sight obstructed by some
  // body in the link's occluder set), else the bucket-floor rate.
  // MaxRatePath filters edges with RateBps <= 0, so blocked links
  // automatically drop out of routing without allocator changes.
  public double RateBps { get; internal set; }

  // True iff some occluder body in this link's set is currently
  // intersecting the chord between endpoints. Surfaced for telemetry
  // / UI; routing already excludes the edge via the RateBps filter.
  public bool Blocked { get; internal set; }

  // Total demand allocated on this edge after the per-Solve max-min
  // fair allocation. 0 if no jobs use the edge; never exceeds RateBps.
  public double UsedBps { get; internal set; }

  // Forecast UT at which this link's effective state changes — the
  // earliest of (next bucket transition, next occlusion enter/exit).
  // +∞ when the link is rate-stable across the full search horizon
  // (e.g. stationary endpoints). The network folds the min over all
  // link NextEventUTs into MaxTickDt.
  public double NextEventUT { get; internal set; } = double.PositiveInfinity;

  public Link(Endpoint from, Endpoint to, double distanceM, double snr, double rateBps) {
    From = from;
    To = to;
    DistanceM = distanceM;
    Snr = snr;
    RateBps = rateBps;
  }
}
