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
  public double RateBps { get; }

  // Total demand allocated on this edge after the per-Solve max-min
  // fair allocation. 0 if no jobs use the edge; never exceeds RateBps.
  public double UsedBps { get; internal set; }

  // Forecast UT at which this link's quantised bucket changes — i.e.
  // the next geometry-driven state-change horizon for this edge.
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
