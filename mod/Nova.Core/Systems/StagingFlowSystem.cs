using System;
using System.Collections.Generic;
using System.Linq;
using Nova.Core.Resources;
using Buffer = Nova.Core.Resources.Buffer;

namespace Nova.Core.Systems;

// Water-fill solver for Topological resources (RP-1, LOX, LH2,
// Hydrazine, Xenon). Owns the vessel topology graph (nodes + edges)
// and a per-tick set of consumer demands, computes per-buffer drain
// rates that respect:
//
//   • DrainPriority: higher-DP pools drain first; lower-DP pools take
//     residual.
//   • Connected components: per (resource, drain priority) the active
//     pools partition by reach. Disconnected sub-networks solve
//     independently with no cross-contamination.
//   • Proportional-to-amount fairness: within one component, drain
//     splits proportional to current Buffer.Contents — symmetric pools
//     stay in lockstep, asymmetric pools converge in ratio.
//   • Per-buffer MaxRateOut: pools binding their physical drain cap
//     get pinned, the residual demand redistributes among the rest.
//
// Algorithm (per Solve, per (drain priority, resource, component)):
//
//   1. active = pools in component with Contents > 0 and MaxRateOut > 0
//   2. remaining = total demand in component
//   3. while active and remaining > 0:
//        sum_amount = Σ p.Contents over active
//        proposed_p = (p.Contents / sum_amount) × remaining
//        binding = pool with smallest α s.t. α × proposed_p ≥ headroom
//        apply α-step: rates_p += α × proposed_p; remaining -= α × remaining
//        if binding: pin it, drop from active, recurse
//        else: done
//
// The binding-pool detection makes this exact: a single iteration of
// proportional water-fill is fair, and removing-and-recursing on a
// rate-capped pool gives the next iteration the residual demand to
// rebalance among the unconstrained.
//
// Output: Buffer.Rate populated (negative = draining), Demand.Satisfied
// populated (consumer's actual delivered rate). Caller-side coupling
// (e.g. engine kerolox = min over RP-1 / LOX) is the caller's job —
// each Demand is solved independently here.
public class StagingFlowSystem : BackgroundSystem {

  // Smallest amount/rate magnitude treated as nonzero. Looser than the
  // LP's 1e-9 because we're doing arithmetic, not pivot-tolerance work.
  private const double Epsilon = 1e-12;

  // ── Public types ───────────────────────────────────────────────────

  public class Node {
    internal long id;
    public long Id { get => id; internal set => id = value; }
    public int DrainPriority;
    public bool Jettisoned;
    public double DryMass;
    public List<Buffer> Buffers = new();

    // Children-via-edges. Populated by StagingFlowSystem.AddEdge so
    // walkers can recurse the subtree (e.g. decoupler-driven jettison
    // marks every node below the decoupler).
    internal List<Node> children = new();

    // Shared with the owning system; passed down to every buffer
    // created here so lerp reads see the vessel's current sim UT.
    internal SimClock Clock;

    public Buffer AddBuffer(Resource resource, double capacity) {
      var b = new Buffer {
        Resource = resource,
        Capacity = capacity,
        Clock = Clock,
        BaselineContents = capacity,
        BaselineUT = Clock?.UT ?? 0,
      };
      Buffers.Add(b);
      return b;
    }

    // Self + all descendants reachable by walking child links transitively.
    public IEnumerable<Node> AllSubtreeNodes() {
      yield return this;
      foreach (var c in children)
        foreach (var n in c.AllSubtreeNodes())
          yield return n;
    }

    // Total mass = dry + Σ buffer mass (Density × Contents-at-now).
    public double Mass() {
      double m = DryMass;
      foreach (var b in Buffers)
        m += b.Contents * b.Resource.Density;
      return m;
    }

    // Time to the next event affecting this node's buffers (a buffer
    // empties or fills). +∞ when no rate is moving things. Reads
    // Contents at current clock UT — i.e., already accounts for any
    // lerp drift since the last solve.
    public double TimeToNextExpiry() {
      double earliest = double.PositiveInfinity;
      foreach (var b in Buffers) {
        var contents = b.Contents;
        if (b.Rate < 0 && contents > Epsilon) {
          var t = contents / -b.Rate;
          if (t < earliest) earliest = t;
        } else if (b.Rate > 0 && contents < b.Capacity - Epsilon) {
          var t = (b.Capacity - contents) / b.Rate;
          if (t < earliest) earliest = t;
        }
      }
      return earliest;
    }
  }

  public class Edge {
    public Node A;
    public Node B;
    // null = all resources flow through this edge.
    public HashSet<Resource> AllowedResources;
    // resources listed here flow A→B but not B→A (parent-up only).
    public HashSet<Resource> UpOnlyResources = new();
  }

  // A consumer's request for one Topological resource at one node.
  // Caller mutates `Rate` per tick before Solve; reads `Satisfied`
  // after. `Activity = Satisfied / Rate ∈ [0, 1]`.
  public class Demand {
    public Node Node;
    public Resource Resource;
    public double Rate;
    public double Satisfied;
    public double Activity => Rate > Epsilon ? Satisfied / Rate : 0;
  }

  // ── State ──────────────────────────────────────────────────────────

  private List<Node> nodes = new();
  private List<Edge> edges = new();
  private List<Demand> demands = new();
  private long nextNodeId;
  private readonly SimClock clock;

  // Exposed so callers (including tests) can advance the simulation
  // clock directly. VirtualVessel and DeltaVSimulation drive this
  // through their own loops; tests construct a standalone system and
  // step the clock to drive Contents lerps.
  public SimClock Clock => clock;

  public StagingFlowSystem(SimClock clock = null) {
    this.clock = clock ?? new SimClock();
  }

  // ── Node / edge construction ───────────────────────────────────────

  public Node AddNode() {
    var n = new Node { id = nextNodeId++, Clock = clock };
    nodes.Add(n);
    return n;
  }

  public Edge AddEdge(Node a, Node b,
      HashSet<Resource> allowedResources = null,
      HashSet<Resource> upOnlyResources = null) {
    var e = new Edge {
      A = a,
      B = b,
      AllowedResources = allowedResources,
      UpOnlyResources = upOnlyResources ?? new HashSet<Resource>(),
    };
    edges.Add(e);
    a.children.Add(b);
    return e;
  }

  public IEnumerable<Node> Nodes => nodes;

  // All non-jettisoned nodes. The active set for mass / drain / etc.
  public IEnumerable<Node> ActiveNodes() {
    foreach (var n in nodes) if (!n.Jettisoned) yield return n;
  }

  // ── Demand registration ────────────────────────────────────────────

  public Demand RegisterDemand(Node node, Resource resource, double rate = 0) {
    var d = new Demand { Node = node, Resource = resource, Rate = rate };
    demands.Add(d);
    return d;
  }

  public void RemoveDemand(Demand d) => demands.Remove(d);
  public void ClearDemands() => demands.Clear();

  public IEnumerable<Demand> Demands => demands;

  // ── Reach ──────────────────────────────────────────────────────────

  // All buffers of `resource` on nodes reachable from `from`. Used by
  // telemetry to compute an engine's per-propellant fuel pool.
  public List<Buffer> ReachableBuffers(Node from, Resource resource) {
    var result = new List<Buffer>();
    foreach (var n in ReachableNodes(from, resource))
      foreach (var b in n.Buffers)
        if (ReferenceEquals(b.Resource, resource)) result.Add(b);
    return result;
  }

  // Walk edges from `from`, respecting AllowedResources and UpOnly-
  // Resources. Returns all reachable, non-jettisoned nodes (including
  // `from` itself if not jettisoned).
  public HashSet<Node> ReachableNodes(Node from, Resource resource) {
    var visited = new HashSet<Node>();
    if (from == null || from.Jettisoned) return visited;
    visited.Add(from);
    var stack = new Stack<Node>();
    stack.Push(from);
    while (stack.Count > 0) {
      var n = stack.Pop();
      foreach (var e in edges) {
        if (e.AllowedResources != null && !e.AllowedResources.Contains(resource)) continue;
        Node other;
        // Edges are A→B "parent-down". UpOnly blocks B→A traversal.
        if (e.A == n) {
          other = e.B;
        } else if (e.B == n) {
          if (e.UpOnlyResources.Contains(resource)) continue;
          other = e.A;
        } else continue;
        if (other.Jettisoned) continue;
        if (visited.Add(other)) stack.Push(other);
      }
    }
    return visited;
  }

  // ── BackgroundSystem implementation ────────────────────────────────

  public override void Solve() {
    // Re-baseline every buffer at the current clock UT, then zero
    // its rate. The Rate setter would auto-rebaseline too, but
    // doing it once via Refresh is clearer and avoids re-reading
    // ContentsAt for the same buffer in the per-resource solve below.
    foreach (var n in nodes) {
      foreach (var b in n.Buffers) {
        b.Refresh(clock.UT);
        b.Rate = 0;
      }
    }
    foreach (var d in demands)
      d.Satisfied = 0;

    // Active demands: Topological resource, non-zero rate, node not
    // jettisoned. Process resources separately — different resources
    // never share pools, so they decompose trivially.
    var activeDemands = demands.Where(d =>
        d.Rate > Epsilon
        && d.Resource.Domain == ResourceDomain.Topological
        && !d.Node.Jettisoned)
      .ToList();
    if (activeDemands.Count == 0) return;

    // Drain priorities, descending. We loop over the union of all
    // distinct DPs across non-jettisoned nodes that have any buffer of
    // any active resource — each DP round considers only pools at that
    // level.
    var demandResources = new HashSet<Resource>();
    foreach (var d in activeDemands) demandResources.Add(d.Resource);

    var dpLevels = nodes
      .Where(n => !n.Jettisoned && n.Buffers.Any(b => demandResources.Contains(b.Resource)))
      .Select(n => n.DrainPriority)
      .Distinct()
      .OrderByDescending(dp => dp)
      .ToList();

    foreach (var dp in dpLevels) {
      foreach (var resource in demandResources) {
        SolveResourceAtDp(resource, dp, activeDemands);
      }
    }

    needsSolve = false;
  }

  // Solve one (resource, drain-priority) round. Decompose demands by
  // connected pool overlap and water-fill each component.
  private void SolveResourceAtDp(Resource resource, int dp, List<Demand> activeDemands) {
    var demandsForR = activeDemands
      .Where(d => ReferenceEquals(d.Resource, resource) && d.Rate - d.Satisfied > Epsilon)
      .ToList();
    if (demandsForR.Count == 0) return;

    // Per demand: reach + active pools at this DP.
    // A "pool" here is a single Buffer; multi-buffer pools (multiple
    // RP-1 tanks on one node) we group by node afterwards. But for
    // reach, the unit is the node — all buffers on the same node share
    // its DrainPriority and reachability.
    var reachableNodesByDemand = new Dictionary<Demand, HashSet<Node>>();
    foreach (var d in demandsForR) {
      reachableNodesByDemand[d] = ReachableNodes(d.Node, resource);
    }

    // Build the pool list at this DP for this resource. A "pool" is
    // (node, resource) where node.DP == dp, has buffers of resource,
    // is non-jettisoned, has any nonzero contents and any nonzero
    // MaxRateOut.
    var allActivePools = new List<(Node node, List<Buffer> buffers,
                                    double totalAmount, double maxRateOut)>();
    foreach (var n in nodes) {
      if (n.Jettisoned) continue;
      if (n.DrainPriority != dp) continue;
      var bufs = n.Buffers.Where(b => ReferenceEquals(b.Resource, resource)).ToList();
      if (bufs.Count == 0) continue;
      double totalAmount = bufs.Sum(b => Math.Max(0, b.Contents));
      double maxRateOut = bufs.Sum(b => b.Contents > Epsilon ? b.MaxRateOut : 0);
      if (totalAmount < Epsilon || maxRateOut < Epsilon) continue;
      allActivePools.Add((n, bufs, totalAmount, maxRateOut));
    }
    if (allActivePools.Count == 0) return;

    // For each demand, find which active pools its reach covers.
    var demandPools = new Dictionary<Demand, List<int>>();
    var poolDemands = new Dictionary<int, List<Demand>>();
    for (int i = 0; i < allActivePools.Count; i++) {
      poolDemands[i] = new List<Demand>();
    }
    foreach (var d in demandsForR) {
      var reach = reachableNodesByDemand[d];
      var ix = new List<int>();
      for (int i = 0; i < allActivePools.Count; i++) {
        if (reach.Contains(allActivePools[i].node)) {
          ix.Add(i);
          poolDemands[i].Add(d);
        }
      }
      demandPools[d] = ix;
    }

    // Connected components via Union-Find. Two demands are in the same
    // component iff they share at least one pool. Demands with no
    // pools at this DP are skipped (residual carries to next DP).
    var demandRoot = new Dictionary<Demand, Demand>();
    foreach (var d in demandsForR) demandRoot[d] = d;
    Demand FindRoot(Demand d) {
      while (!ReferenceEquals(demandRoot[d], d)) {
        demandRoot[d] = demandRoot[demandRoot[d]];
        d = demandRoot[d];
      }
      return d;
    }
    void Union(Demand a, Demand b) {
      var ra = FindRoot(a);
      var rb = FindRoot(b);
      if (!ReferenceEquals(ra, rb)) demandRoot[ra] = rb;
    }
    foreach (var kv in poolDemands) {
      var sharers = kv.Value;
      for (int i = 1; i < sharers.Count; i++) Union(sharers[0], sharers[i]);
    }

    // Build components
    var componentDemands = new Dictionary<Demand, List<Demand>>();
    foreach (var d in demandsForR) {
      if (demandPools[d].Count == 0) continue; // no pools at this DP → carry to next
      var root = FindRoot(d);
      if (!componentDemands.TryGetValue(root, out var list))
        componentDemands[root] = list = new List<Demand>();
      list.Add(d);
    }

    // Water-fill each component.
    foreach (var compKv in componentDemands) {
      var compDemands = compKv.Value;
      var poolIxSet = new HashSet<int>();
      foreach (var d in compDemands)
        foreach (var i in demandPools[d]) poolIxSet.Add(i);

      var compPools = poolIxSet.Select(i => allActivePools[i]).ToList();
      double totalDemand = compDemands.Sum(d => d.Rate - d.Satisfied);
      if (totalDemand < Epsilon) continue;

      var poolRates = WaterFill(compPools, totalDemand);

      // Distribute pool rates to per-buffer rates (proportional to contents).
      double delivered = 0;
      foreach (var pr in poolRates) {
        var rate = pr.rate;
        delivered += rate;
        if (rate < Epsilon) continue;
        var bufs = pr.pool.buffers;
        var totalAmt = pr.pool.totalAmount;
        foreach (var buf in bufs) {
          var share = totalAmt > Epsilon ? buf.Contents / totalAmt : 0;
          buf.Rate += -rate * share;
        }
      }

      // Distribute delivered amount to demands in proportion to their
      // residual demand share.
      foreach (var d in compDemands) {
        var residual = d.Rate - d.Satisfied;
        var share = residual / totalDemand;
        d.Satisfied += delivered * share;
      }
    }
  }

  // Water-fill a single component. Drain proportional to amount,
  // clipped per-pool by MaxRateOut. Returns (pool, rate≥0) entries.
  private List<(
      (Node node, List<Buffer> buffers, double totalAmount, double maxRateOut) pool,
      double rate)>
    WaterFill(
        List<(Node node, List<Buffer> buffers, double totalAmount, double maxRateOut)> pools,
        double demand) {

    var rates = new Dictionary<int, double>();
    for (int i = 0; i < pools.Count; i++) rates[i] = 0;
    var active = new List<int>();
    for (int i = 0; i < pools.Count; i++) active.Add(i);
    double remaining = demand;

    while (active.Count > 0 && remaining > Epsilon) {
      double sumAmount = active.Sum(i => pools[i].totalAmount);
      if (sumAmount < Epsilon) break;

      // For each active pool: proposed_step = (amount/sum) × remaining.
      // Compute α s.t. α × proposed_step ≤ headroom for all pools, with
      // α capped at 1 (full demand fill).
      double alpha = 1.0;
      int bindingIdx = -1;
      foreach (var i in active) {
        var p = pools[i];
        var proposed = (p.totalAmount / sumAmount) * remaining;
        var headroom = p.maxRateOut - rates[i];
        if (proposed > headroom + Epsilon) {
          var ratio = headroom / proposed;
          if (ratio < alpha) { alpha = ratio; bindingIdx = i; }
        }
      }

      // Apply the α-step.
      double stepDelivered = alpha * remaining;
      foreach (var i in active) {
        var p = pools[i];
        rates[i] += alpha * (p.totalAmount / sumAmount) * remaining;
      }
      remaining -= stepDelivered;

      if (bindingIdx < 0) break;     // α=1; demand met (modulo rounding)
      active.Remove(bindingIdx);     // pin and recurse
    }

    var result = new List<(
        (Node node, List<Buffer> buffers, double totalAmount, double maxRateOut),
        double)>();
    for (int i = 0; i < pools.Count; i++) result.Add((pools[i], rates[i]));
    return result;
  }

  // Time horizon over which the current solve remains valid. The first
  // pool to empty (drain rate × time = contents) or fill (fill rate ×
  // time = capacity − contents) bounds the next re-solve. Since the
  // current StagingFlowSystem only produces drains, the fill side is
  // moot until accumulator-driven fills land.
  public override double MaxTickDt() {
    double earliest = double.PositiveInfinity;
    foreach (var n in nodes) {
      if (n.Jettisoned) continue;
      foreach (var b in n.Buffers) {
        if (b.Resource.Domain != ResourceDomain.Topological) continue;
        var contents = b.Contents;
        if (b.Rate < -Epsilon && contents > Epsilon) {
          var t = contents / -b.Rate;
          if (t < earliest) earliest = t;
        } else if (b.Rate > Epsilon && contents < b.Capacity - Epsilon) {
          var t = (b.Capacity - contents) / b.Rate;
          if (t < earliest) earliest = t;
        }
      }
    }
    return earliest;
  }

  // Buffers are lerp-based now — Contents lazily computes from
  // baseline + Rate × elapsed. This Tick exists to satisfy the
  // BackgroundSystem contract; the actual time advancement is the
  // shared clock's job (driven by VirtualVessel.Tick or
  // DeltaVSimulation). No per-tick buffer mutation needed.
  public override void Tick(double dt) {
  }
}
