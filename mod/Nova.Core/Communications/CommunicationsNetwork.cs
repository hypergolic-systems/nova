using System;
using System.Collections.Generic;
using System.Linq;
using Nova.Core.Components.Communications;
using Nova.Core.Utils;

namespace Nova.Core.Communications;

// Steady-state, event-driven communications graph plus a job queue.
// Singleton at runtime (one network per simulation) but instantiable
// here so tests own their own; the KSP-side driver hosts the
// canonical instance.
//
// Lifecycle: Add/RemoveEndpoint and Submit/Cancel mark the graph
// dirty. Solve(ut) integrates dt = ut - lastSolveUt into per-job
// byte counters, recomputes geometry, finds a max-rate path per
// active flow, and runs max-min fair allocation. Graph access lazy-
// resolves at the prior UT.
//
// The driver decides cadence: between solves, geometry is stale and
// jobs accrue bytes at their last-known rate. MaxTickDt() forecasts
// the next packet completion so the driver can clamp.
public class CommunicationsNetwork {

  private readonly List<Endpoint> endpoints = new();
  private readonly List<Job> jobs = new();
  private GraphSnapshot graph = GraphSnapshot.Empty;
  private bool needsSolve = true;
  private double? simulationTime;

  // Per-directed-link cache of the most recently computed
  // NextEventUT, keyed by (fromEndpointId, toEndpointId). Between
  // bucket transitions the analytical r(t) is deterministic, so once
  // a horizon is computed it stays valid until either the cached UT
  // is reached (current Solve will pass it) or a topology / Motion
  // change forces a re-solve via Invalidate().
  //
  // For a 100-vessel network with ~5000 directed links, only a
  // handful trigger transitions at any one Solve; the rest read from
  // cache and skip both pre-screen and bisection. This is what makes
  // event-driven scheduling actually save work at scale.
  private readonly Dictionary<(string, string), double> linkHorizonCache = new();

  public IReadOnlyList<Endpoint> Endpoints => endpoints;
  public IReadOnlyList<Job> Jobs => jobs;
  public bool NeedsSolve => needsSolve;

  public void AddEndpoint(Endpoint e) {
    endpoints.Add(e);
    Invalidate();
  }

  public bool RemoveEndpoint(Endpoint e) {
    var removed = endpoints.Remove(e);
    if (removed) Invalidate();
    return removed;
  }

  // Add a job to the network. The job's Endpoint must already be
  // registered. For Packets, the Destination must also be registered.
  public void Submit(Job job) {
    if (job == null) throw new ArgumentNullException(nameof(job));
    if (!endpoints.Contains(job.Endpoint))
      throw new InvalidOperationException("Job endpoint not registered with this network.");
    if (job is Packet p && !endpoints.Contains(p.Destination))
      throw new InvalidOperationException("Packet destination not registered with this network.");
    jobs.Add(job);
    Invalidate();
  }

  // Mark an active job as cancelled. No-op if already finished.
  // Returns true iff the call actually flipped the status.
  public bool Cancel(Job job) {
    if (job == null) throw new ArgumentNullException(nameof(job));
    if (job.Status != JobStatus.Active) return false;
    job.Status = JobStatus.Cancelled;
    job.AllocatedRateBps = 0;
    Invalidate();
    return true;
  }

  public void Invalidate() {
    needsSolve = true;
    linkHorizonCache.Clear();
  }

  // Last solved snapshot. If dirty and we have a previous solve UT,
  // re-solves at that UT before returning. Empty before the first
  // Solve call.
  public GraphSnapshot Graph {
    get {
      if (needsSolve && simulationTime.HasValue) Solve(simulationTime.Value);
      return graph;
    }
  }

  // Earliest forecasted state-change horizon, in seconds from the
  // last solve UT. Folds two event sources:
  //   - next active packet completion (remaining / rate)
  //   - next link bucket crossing (geometry-driven)
  // +∞ if neither has a finite event. The driver advances UT in
  // steps no larger than this so the next event lands on a Solve
  // boundary; allocator rates stay valid up to that boundary.
  public double MaxTickDt() {
    var min = double.PositiveInfinity;
    foreach (var j in jobs) {
      if (j.Status != JobStatus.Active) continue;
      if (j is Packet p && p.AllocatedRateBps > 0) {
        var t = p.RemainingBytes / p.AllocatedRateBps;
        if (t < min) min = t;
      }
    }
    if (simulationTime.HasValue) {
      foreach (var link in graph.Links) {
        var dt = link.NextEventUT - simulationTime.Value;
        if (dt > 0 && dt < min) min = dt;
      }
    }
    return min;
  }

  // Re-solve the graph and re-allocate jobs at the given UT. Steps
  // forward in time only — calling with an earlier UT than the last
  // solve skips integration (allocations still recompute).
  public GraphSnapshot Solve(double ut) {
    // Pre-settle: if jobs/endpoints changed since the last solve, re-
    // allocate at the prior UT before integrating, so the integration
    // interval uses up-to-date rates. Without this, a Cancel between
    // solves still credits the receiver as if its broadcast were live.
    // (Subtle inverse: a newly-submitted job slightly over-credits over
    // the same interval — call Solve right after Submit if that matters.)
    if (needsSolve && simulationTime.HasValue) {
      BuildGraph(simulationTime.Value);
      AllocateJobs();
    }

    if (simulationTime.HasValue) {
      var dt = ut - simulationTime.Value;
      if (dt > 0) Integrate(dt);
    }
    simulationTime = ut;

    BuildGraph(ut);
    ComputeLinkHorizons(ut);
    AllocateJobs();

    needsSolve = false;
    return graph;
  }

  private void Integrate(double dt) {
    foreach (var job in jobs) {
      if (job.Status != JobStatus.Active) continue;
      if (job.AllocatedRateBps <= 0) continue;
      var bytes = job.AllocatedRateBps * dt;
      switch (job) {
        case Packet p: {
          var add = (long)Math.Min(bytes, p.RemainingBytes);
          p.DeliveredBytes += add;
          if (p.DeliveredBytes >= p.TotalBytes) {
            p.DeliveredBytes = p.TotalBytes;
            p.Status = JobStatus.Completed;
            p.AllocatedRateBps = 0;
          }
          break;
        }
        case BroadcastJob b:
          b.BytesSent += (long)bytes;
          break;
        case ReceiveJob r:
          r.BytesReceived += (long)bytes;
          break;
      }
    }
  }

  private void BuildGraph(double ut) {
    var n = endpoints.Count;
    var positions = new Vec3d[n];
    for (int i = 0; i < n; i++) positions[i] = endpoints[i].PositionAt(ut);

    var links = new List<Link>();
    for (int i = 0; i < n; i++) {
      var from = endpoints[i];
      if (from.Antennas.Count == 0) continue;
      for (int j = 0; j < n; j++) {
        if (i == j) continue;
        var to = endpoints[j];
        if (to.Antennas.Count == 0) continue;
        var distance = (positions[j] - positions[i]).Magnitude;
        BestPair(from, to, distance, out var snr, out var rate);
        // Quantise against the link's hardware-max ceiling so bucket
        // boundaries are constant per link across pair switches and
        // distance changes. Reported RateBps is the bucket floor —
        // conservative; the allocator never over-grants what geometry
        // won't sustain until the next solve.
        var maxCeiling = LinkMaxCeiling(from, to);
        var quantised = RateBuckets.Quantize(rate, maxCeiling);
        links.Add(new Link(from, to, distance, snr, quantised));
      }
    }
    graph = new GraphSnapshot(links, ut);
  }

  // Per-link bucket-crossing forecast. Iterates UNORDERED endpoint
  // pairs (one pass per {a, b}) so the pair r_max calc and pre-screen
  // are shared between A→B and B→A links. Two filters apply:
  //
  //   (1) Per-pair MaxUsefulRange: distance beyond which NO antenna
  //       combination on this edge produces a bucket ≥ 1. Skip the
  //       full bisection for pairs that stay beyond this threshold
  //       across the horizon — those links sit permanently at bucket
  //       0 and report NextEventUT at the horizon cap.
  //
  //   (2) Symmetric iteration: A→B and B→A share the same r(t); the
  //       pre-screen result is reused. The bisection still runs per
  //       direction (asymmetric antennas can have different bucket
  //       transitions on the same r(t)).
  private void ComputeLinkHorizons(double ut) {
    var horizonCapUT = ut + CommunicationsParameters.MaxHorizonSeconds;

    for (int i = 0; i < endpoints.Count; i++) {
      var a = endpoints[i];
      if (a.Antennas.Count == 0) continue;
      for (int j = i + 1; j < endpoints.Count; j++) {
        var b = endpoints[j];
        if (b.Antennas.Count == 0) continue;

        var linkAB = FindLink(a, b);
        var linkBA = FindLink(b, a);
        if (linkAB == null && linkBA == null) continue;

        // Cache hit: if both directions have a cached NextEventUT
        // still in the future, skip pre-screen + bisection entirely.
        // This is the dominant path at scale — only pairs whose
        // events have actually arrived (or new pairs) recompute.
        var abCached = linkAB != null
                    && linkHorizonCache.TryGetValue((linkAB.From.Id, linkAB.To.Id), out var cAB)
                    && cAB > ut;
        var baCached = linkBA != null
                    && linkHorizonCache.TryGetValue((linkBA.From.Id, linkBA.To.Id), out var cBA)
                    && cBA > ut;
        if ((linkAB == null || abCached) && (linkBA == null || baCached)) {
          if (linkAB != null) linkAB.NextEventUT = linkHorizonCache[(linkAB.From.Id, linkAB.To.Id)];
          if (linkBA != null) linkBA.NextEventUT = linkHorizonCache[(linkBA.From.Id, linkBA.To.Id)];
          continue;
        }

        // (1) Per-pair filter: max distance at which any antenna pair
        // could deliver a bucket-≥1 link. Beyond this, both directions
        // are permanently bucket 0.
        var pairRMax = PairMaxUsefulRange(a, b);

        if (PrescreenAlwaysOutOfRange(a, b, ut, pairRMax)) {
          if (linkAB != null) SetAndCache(linkAB, horizonCapUT);
          if (linkBA != null) SetAndCache(linkBA, horizonCapUT);
          continue;
        }

        // Detailed bisection per direction. Asymmetric antennas mean
        // each direction can transition at a different UT, so we can't
        // share the bisection result. A cache hit on one direction
        // doesn't help the other (independent UTs).
        if (linkAB != null) SetAndCache(linkAB, abCached ? linkHorizonCache[(linkAB.From.Id, linkAB.To.Id)] : HorizonForLink(linkAB, ut));
        if (linkBA != null) SetAndCache(linkBA, baCached ? linkHorizonCache[(linkBA.From.Id, linkBA.To.Id)] : HorizonForLink(linkBA, ut));
      }
    }
  }

  private void SetAndCache(Link link, double nextEventUT) {
    link.NextEventUT = nextEventUT;
    linkHorizonCache[(link.From.Id, link.To.Id)] = nextEventUT;
  }

  private double HorizonForLink(Link link, double ut) {
    var from = link.From;
    var to = link.To;
    var maxCeiling = LinkMaxCeiling(from, to);

    // Analytical path: both endpoints expose a structured MotionModel
    // (KeplerMotion or SurfaceMotion). The bisection loop is unchanged,
    // but each position eval becomes closed-form arithmetic via
    // AnalyticalPosition.Of — no iterative Kepler propagator. Cross-SOI
    // is handled by the recursive body-chain composition inside
    // AnalyticalPosition; the dispatch condition is intentionally
    // broad ("both non-null") rather than same-parent only.
    if (from.Motion != null && to.Motion != null) {
      var fm = from.Motion;
      var tm = to.Motion;
      Func<double, int> bucketAtFast = t => {
        var pa = AnalyticalPosition.Of(fm, t);
        var pb = AnalyticalPosition.Of(tm, t);
        var d = (pb - pa).Magnitude;
        BestPair(from, to, d, out _, out var r);
        return RateBuckets.BucketIndex(r, maxCeiling);
      };
      return LinkHorizon.NextBucketCrossing(ut, bucketAtFast);
    }

    // Numerical fallback: opaque PositionAt closures. Used when one
    // or both endpoints have no MotionModel (active flight, ad-hoc
    // test fixtures).
    Func<double, int> bucketAt = t => {
      var pa = from.PositionAt(t);
      var pb = to.PositionAt(t);
      var d = (pb - pa).Magnitude;
      BestPair(from, to, d, out _, out var r);
      return RateBuckets.BucketIndex(r, maxCeiling);
    };
    return LinkHorizon.NextBucketCrossing(ut, bucketAt);
  }

  private Link FindLink(Endpoint from, Endpoint to) {
    foreach (var link in graph.Links) {
      if (link.From == from && link.To == to) return link;
    }
    return null;
  }

  // Coarse distance sweep used by the pair-level pre-screen. Returns
  // true iff the current distance AND every sampled future distance
  // exceeds pairRMax — i.e. the link is permanently bucket 0 across
  // the search window (modulo brief encounters shorter than the
  // sample spacing, which the pre-screen accepts as an approximation).
  private static bool PrescreenAlwaysOutOfRange(Endpoint a, Endpoint b, double ut, double pairRMax) {
    if (pairRMax <= 0) return true;
    var pa0 = a.PositionAt(ut);
    var pb0 = b.PositionAt(ut);
    if ((pb0 - pa0).Magnitude <= pairRMax) return false;

    var horizon = CommunicationsParameters.MaxHorizonSeconds;
    var samples = CommunicationsParameters.PrescreenSamples;
    var step = horizon / samples;
    for (int k = 1; k <= samples; k++) {
      var t = ut + k * step;
      var pa = a.PositionAt(t);
      var pb = b.PositionAt(t);
      if ((pb - pa).Magnitude <= pairRMax) return false;
    }
    return true;
  }

  // Maximum range, over all (txA, rxB) and (txB, rxA) antenna combos,
  // at which the link could transition out of bucket 0. Computed from
  // the per-direction Shannon-rate formula at the bucket-1 threshold:
  //   shannon = 1/N  ⇒  SNR = (1 + SNR_ref)^(1/N) − 1
  //   r²       = TxPower · Gain_tx · Gain_rx / (N₀ · SNR_threshold)
  // Beyond the max over all pairs, both directions are permanently 0.
  private static double PairMaxUsefulRange(Endpoint a, Endpoint b) {
    double maxR2 = 0;
    var n = CommunicationsParameters.BucketCount;
    var n0 = CommunicationsParameters.NoiseFloor;

    foreach (var x in a.Antennas) {
      var snrRefX = x.RefSnr(n0);
      if (snrRefX <= 0) continue;
      var snrThreshX = Math.Pow(1 + snrRefX, 1.0 / n) - 1;
      if (snrThreshX <= 0) continue;
      foreach (var y in b.Antennas) {
        // X→Y direction: TX = x, RX = y, ref-SNR = SNR_ref(x).
        var rXtoY2 = x.TxPower * x.Gain * y.Gain / (n0 * snrThreshX);
        if (rXtoY2 > maxR2) maxR2 = rXtoY2;
      }
    }

    foreach (var y in b.Antennas) {
      var snrRefY = y.RefSnr(n0);
      if (snrRefY <= 0) continue;
      var snrThreshY = Math.Pow(1 + snrRefY, 1.0 / n) - 1;
      if (snrThreshY <= 0) continue;
      foreach (var x in a.Antennas) {
        // Y→X direction: TX = y, RX = x, ref-SNR = SNR_ref(y).
        var rYtoX2 = y.TxPower * y.Gain * x.Gain / (n0 * snrThreshY);
        if (rYtoX2 > maxR2) maxR2 = rYtoX2;
      }
    }

    return Math.Sqrt(maxR2);
  }

  // The largest possible hardware ceiling for any antenna pair on this
  // directed edge. Used as the bucketing denominator so quantised rate
  // is comparable across pair switches and distance variation.
  private static double LinkMaxCeiling(Endpoint from, Endpoint to) {
    double max = 0;
    foreach (var tx in from.Antennas) {
      foreach (var rx in to.Antennas) {
        var c = Math.Min(tx.MaxRate, rx.MaxRate);
        if (c > max) max = c;
      }
    }
    return max;
  }

  private void AllocateJobs() {
    // Reset link utilisation and per-job rates that would otherwise
    // hold stale values from the previous solve.
    foreach (var link in graph.Links) link.UsedBps = 0;
    foreach (var j in jobs) {
      if (j.Status == JobStatus.Active) j.AllocatedRateBps = 0;
    }

    var allocFlows = new List<AllocFlow>();
    var linkEdges = new Dictionary<Link, AllocEdge>();
    var broadcastEdges = new Dictionary<BroadcastJob, AllocEdge>();

    AllocEdge GetLinkEdge(Link link) {
      if (!linkEdges.TryGetValue(link, out var ae)) {
        ae = new AllocEdge { Capacity = link.RateBps, BackingLink = link };
        linkEdges[link] = ae;
      }
      return ae;
    }

    AllocEdge GetBroadcastEdge(BroadcastJob b) {
      if (!broadcastEdges.TryGetValue(b, out var ae)) {
        ae = new AllocEdge { Capacity = b.TargetRateBps };
        broadcastEdges[b] = ae;
      }
      return ae;
    }

    void AddFlowEdge(AllocFlow f, AllocEdge e) {
      f.Edges.Add(e);
      e.Flows.Add(f);
    }

    // Packet flows.
    foreach (var p in jobs.OfType<Packet>()) {
      if (p.Status != JobStatus.Active) continue;
      var path = MaxRatePath.Find(graph, p.Source, p.Destination);
      if (path == null) continue;
      var flow = new AllocFlow { Packet = p };
      foreach (var link in path) AddFlowEdge(flow, GetLinkEdge(link));
      allocFlows.Add(flow);
    }

    // Broadcast → Receive flows: one per matching (broadcast, receive)
    // pair, keyed by (TKey, value). Each broadcast contributes a single
    // shared budget edge so the source-side ceiling is enforced.
    var activeBroadcasts = jobs.OfType<BroadcastJob>().Where(b => b.Status == JobStatus.Active).ToList();
    var activeReceives = jobs.OfType<ReceiveJob>().Where(r => r.Status == JobStatus.Active).ToList();

    foreach (var b in activeBroadcasts) {
      foreach (var r in activeReceives) {
        if (b.KeyType != r.KeyType) continue;
        if (!Equals(b.KeyAsObject, r.KeyAsObject)) continue;
        var path = MaxRatePath.Find(graph, b.Endpoint, r.Endpoint);
        if (path == null) continue;
        var flow = new AllocFlow {
          Broadcast = b, Receive = r,
          Ceiling = r.MaxRateBps,
        };
        AddFlowEdge(flow, GetBroadcastEdge(b));
        foreach (var link in path) AddFlowEdge(flow, GetLinkEdge(link));
        allocFlows.Add(flow);
      }
    }

    if (allocFlows.Count == 0) return;

    var allEdges = new List<AllocEdge>(linkEdges.Count + broadcastEdges.Count);
    allEdges.AddRange(linkEdges.Values);
    allEdges.AddRange(broadcastEdges.Values);
    BandwidthAllocator.Allocate(allocFlows, allEdges);

    // Map flow rates back to jobs.
    foreach (var flow in allocFlows) {
      if (flow.Packet != null) {
        flow.Packet.AllocatedRateBps = flow.Rate;
      }
    }
    // Aggregate per Broadcast (sum across all receive flows it feeds)
    // and per Receive (sum across all broadcasts feeding it).
    foreach (var b in activeBroadcasts) b.AllocatedRateBps = 0;
    foreach (var r in activeReceives) r.AllocatedRateBps = 0;
    foreach (var flow in allocFlows) {
      if (flow.Broadcast != null) flow.Broadcast.AllocatedRateBps += flow.Rate;
      if (flow.Receive != null) flow.Receive.AllocatedRateBps += flow.Rate;
    }
  }

  // Pick the (tx, rx) antenna pair maximising achievable rate, using
  // the (snr, ref-snr, ceiling) trio in the directional formula:
  //   rate = min(MaxRate_tx, MaxRate_rx) · min(1, log(1+SNR) / log(1+SNR_ref(tx)))
  // Returns the SNR *of the winning pair* alongside its rate (not
  // the global max-SNR, which can correspond to a different pair).
  private static void BestPair(Endpoint from, Endpoint to, double distance,
                               out double bestSnr, out double bestRate) {
    bestSnr = 0;
    bestRate = 0;
    var n0 = CommunicationsParameters.NoiseFloor;
    var r2n0 = distance * distance * n0;

    foreach (var tx in from.Antennas) {
      var refSnr = tx.RefSnr(n0);
      if (refSnr <= 0) continue;
      var logRef = Math.Log(1 + refSnr);
      foreach (var rx in to.Antennas) {
        var snr = tx.TxPower * tx.Gain * rx.Gain / r2n0;
        var ceiling = Math.Min(tx.MaxRate, rx.MaxRate);
        var ratio = Math.Log(1 + snr) / logRef;
        var rate = ceiling * Math.Min(1, ratio);
        if (rate > bestRate) {
          bestRate = rate;
          bestSnr = snr;
        }
      }
    }
  }
}
