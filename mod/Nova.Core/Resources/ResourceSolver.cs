using System;
using System.Collections.Generic;
using System.Linq;
using Google.OrTools.LinearSolver;

namespace Nova.Core.Resources;

public class ResourceSolver {

  // ── Public API types ─────────────────────────────────────────────────

  public enum Priority { Critical, High, Low }
  private const int PriorityCount = 3;

  public class Node {
    internal long id;
    public long Id => id;
    internal List<Device> devices = new();
    internal List<Buffer> buffers = new();

    public double DryMass;
    public bool Jettisoned;
    public int DrainPriority;

    public IReadOnlyList<Device> Devices => devices;
    public IReadOnlyList<Buffer> Buffers => buffers;

    public Device AddDevice(Priority priority) {
      var device = new Device { node = this, priority = priority };
      devices.Add(device);
      return device;
    }

    public Buffer AddBuffer(Resource resource, double capacity) {
      var buffer = new Buffer {
        Resource = resource,
        Capacity = capacity,
        Contents = capacity,
        MaxRateIn = 0,
        MaxRateOut = 0,
      };
      buffers.Add(buffer);
      return buffer;
    }

    public void IntegrateBuffers(double dt) {
      foreach (var buffer in buffers)
        buffer.Integrate(dt);
    }

    public double TimeToNextExpiry() {
      var earliest = double.PositiveInfinity;
      foreach (var buffer in buffers) {
        if (buffer.Rate < 0 && buffer.Contents > 1e-9)
          earliest = Math.Min(earliest, buffer.Contents / (-buffer.Rate));
        else if (buffer.Rate > 0 && buffer.Contents < buffer.Capacity - 1e-9)
          earliest = Math.Min(earliest, (buffer.Capacity - buffer.Contents) / buffer.Rate);
      }
      return earliest;
    }

    public double Mass() {
      double mass = DryMass;
      foreach (var buffer in buffers)
        mass += buffer.Contents * buffer.Resource.Density;
      return mass;
    }

    internal List<Node> children = new();

    public IEnumerable<Node> AllNodes() {
      yield return this;
      foreach (var child in children)
        foreach (var node in child.AllNodes())
          yield return node;
    }
  }

  // Unified Device — handles both consumers (inputs only) and producers
  // (outputs only) and hybrids (both — fuel cells). Activity ∈ [0, demand]
  // is maximized by the priority sum-max objective. Optional `parent`
  // gates activity ≤ parent.activity for incidental producers (e.g. an
  // engine alternator: alt ≤ engine throttle, but lack of EC demand
  // doesn't pull engine throttle down because the constraint is one-way).
  public class Device {
    internal Node node;
    internal Priority priority;
    internal List<(Resource Resource, double MaxRate)> inputs = new();
    internal List<(Resource Resource, double MaxRate)> outputs = new();
    internal Device parent;
    // Persistent LP handle, set once in BuildLP.
    internal Variable Var;

    public double Demand;
    public double Activity;
    public double Satisfaction => Demand > 1e-9 ? Activity / Demand : 0;
    // Per-tick UB scaling: solar panels use this for binary deploy/shadow
    // gating without needing topology rebuild. Default 1 = full activity.
    public double MaxActivity = 1;
    // Hint for VirtualVessel.ComputeNextExpiry — when does this device's
    // production envelope change next? Used by solar for shadow transition.
    public double ValidUntil = double.PositiveInfinity;

    public void AddInput(Resource resource, double maxRate) {
      inputs.Add((resource, maxRate));
    }

    public void AddOutput(Resource resource, double maxRate) {
      outputs.Add((resource, maxRate));
    }

    public void AddParent(Device device) {
      parent = device;
    }
  }

  // ── Internal types ───────────────────────────────────────────────────

  private class Edge {
    public Node Parent;
    public Node Child;
    public HashSet<Resource> AllowedResources;
    public HashSet<Resource> UpOnlyResources = new();
  }

  private class Pool {
    public Node Node;
    public Resource Resource;
    public int DrainPriority;
    public List<Buffer> Tanks = new();
    // Persistent LP handles.
    public Variable SupplyVar;
    public Variable FillVar;
    public double TotalAmount => Tanks.Sum(t => t.Contents > 1e-9 ? t.Contents : 0);
    public double MaxSupplyRate => Tanks.Sum(t => t.Contents > 1e-9 ? t.MaxRateOut : 0);
    public double MaxFillRate => Tanks.Sum(t => t.Contents < t.Capacity - 1e-9 ? t.MaxRateIn : 0);
  }

  private class FlowVar {
    public Node Parent;
    public Node Child;
    public Resource Resource;
    public bool UpOnly;
    public Variable Var;
  }

  private class ConservationEntry {
    public Node Node;
    public Resource Resource;
    public Constraint Eq;
  }

  // ── State ────────────────────────────────────────────────────────────

  private List<Node> nodes = new();
  private List<Edge> edges = new();
  private Dictionary<Resource, double> drainCosts = new();
  private long nextNodeId;

  // Persistent LP — built once when topology is finalized, mutated per Solve.
  private bool topologyFinalized;
  private List<Pool> pools = new();
  private List<FlowVar> flowVars = new();
  private List<ConservationEntry> conservationEntries = new();
  private Dictionary<(Node, Resource), ConservationEntry> conservationByNodeRes = new();
  private Solver lpSolver;
  private Constraint[] devicePin;
  private LinearExpr drainCostExpr;
  private int maxDrainPriority;

  // Fair-distribution apparatus (Phase C). One zVar per resource that
  // has multiple pools. Each tick the fair constraint for pool p in
  // resource r enforces:
  //
  //   pool.SupplyVar - z_r * max(amount / maxAmount_r, FairnessFloor) ≤ 0
  //
  // where maxAmount_r is the largest TotalAmount across r's pools.
  // Per-resource normalization keeps the coefficient bounded above by
  // 1; FairnessFloor (0.1) bounds it below so the LP coefficient lives
  // in [0.1, 1] regardless of how skewed pool amounts are. Without the
  // floor a tiny pool against a full battery would yield coefficients
  // around 1e-8 — well below GLOP's tolerance (see docs/lp_hygiene.md).
  // The cost: pools below 10% of the resource's max participate in
  // fairness with a slightly looser bound than their literal share,
  // but the imbalance is small and bounded.
  // costPinConstraint freezes the drain cost at its minimum so the
  // fairness pass can only shuffle among equally-cheap solutions.
  private const double FairnessFloor = 0.1;

  private Dictionary<Resource, Variable> zVars;
  private Dictionary<Resource, List<Pool>> poolsByResource;
  private Constraint costPinConstraint;
  private Dictionary<Pool, Constraint> fairConstraints;

  // Cost weights for the post-pinning drain-min phase. Uniform `Epsilon`
  // is the tie-breaking minimum; `DrainPriorityStep` makes lower-priority
  // pools more expensive (so high-priority pools drain first). Caller-set
  // `drainCosts` are added on top of both.
  private const double DefaultDrainEpsilon = 1e-6;
  private const double DrainPriorityStep = 1.0;

  // ── Public methods ───────────────────────────────────────────────────

  public Node AddNode() {
    var node = new Node { id = nextNodeId++ };
    nodes.Add(node);
    return node;
  }

  public void AddEdge(Node parent, Node child,
      HashSet<Resource> allowedResources = null,
      HashSet<Resource> upOnlyResources = null) {
    var edge = new Edge {
      Parent = parent,
      Child = child,
      AllowedResources = allowedResources,
      UpOnlyResources = upOnlyResources ?? new HashSet<Resource>(),
    };
    edges.Add(edge);
    parent.children.Add(child);
  }

  public void SetDrainCost(Resource resource, double cost) {
    drainCosts[resource] = cost;
  }

  public IEnumerable<Node> AllNodes() => nodes;

  public IEnumerable<Node> ActiveNodes() => nodes.Where(n => !n.Jettisoned);

  // ── Reach ────────────────────────────────────────────────────────────
  //
  // Static topology query: which nodes / buffers can supply `resource` to
  // `from`? Mirrors the flow-direction semantics the LP enforces:
  //
  //   - Edges with a non-null AllowedResources that doesn't contain
  //     `resource` are skipped entirely.
  //   - Jettisoned nodes are excluded from the visited set.
  //   - On a non-UpOnly edge, supply flows in either direction — both
  //     ends of the edge are reachable from each other.
  //   - On an UpOnly edge for `resource`, the LP clamps flow to ≤ 0
  //     (i.e. only child→parent). So Parent can still reach Child
  //     (Child supplies Parent), but Child cannot reach Parent
  //     (Parent can't supply Child).
  //
  // Used by NovaEngineTopic to compute per-engine fuel pools without
  // running the LP — same answer the solver would give if you isolated
  // one engine and looked at which buffers drained.
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
        if (e.Parent == n) {
          other = e.Child;
        } else if (e.Child == n) {
          if (e.UpOnlyResources.Contains(resource)) continue;
          other = e.Parent;
        } else continue;
        if (other.Jettisoned) continue;
        if (visited.Add(other)) stack.Push(other);
      }
    }
    return visited;
  }

  public List<Buffer> ReachableBuffers(Node from, Resource resource) {
    var buffers = new List<Buffer>();
    foreach (var node in ReachableNodes(from, resource))
      foreach (var buf in node.buffers)
        if (ReferenceEquals(buf.Resource, resource)) buffers.Add(buf);
    return buffers;
  }

  // ── Solve ────────────────────────────────────────────────────────────

  public void Solve() {
    if (!topologyFinalized) {
      FinalizeTopology();
      BuildLP();
      topologyFinalized = true;
    }

    ResetPerTickBounds();

    // Device priority loop. At each priority, maximize sum of activities
    // and pin the optimum so subsequent (lower) priorities can't regress it.
    var devicePriorities = new SortedSet<Priority>(
      nodes.SelectMany(n => n.devices).Select(d => d.priority));

    foreach (var devPri in devicePriorities) {
      lpSolver.Maximize(SumDeviceActivity(devPri));
      if (lpSolver.Solve() != Solver.ResultStatus.OPTIMAL) {
        ExtractResults();
        return;
      }
      // Snapshot the LP solution into Activity / Rate before mutating the
      // pin bounds. SetBounds on a Constraint invalidates the GLOP basis,
      // which makes Variable.SolutionValue() return 0 until the LP is
      // re-solved — even if no actual change to the optimum is required.
      ExtractResults();
      var aStar = lpSolver.Objective().Value();
      if (aStar > 1e-9)
        devicePin[(int)devPri].SetBounds(aStar - 1e-9, double.PositiveInfinity);
    }

    // Phase B: drain minimization. With all device activities pinned,
    // minimize total buffer drain. Pool weights: high-DrainPriority pools
    // are cheap (drain first); same-DP pools at same cost (LP picks based
    // on basis). FillVar is uncosted, so degenerate (supply=X, fill=X)
    // collapses to (0, 0) — the asymmetry is what eliminates same-pool
    // and cross-pool sloshing.
    //
    // If drain-min returns ABNORMAL — typical when the drain gradient is
    // below GLOP's tolerance, e.g. fuel cells with µL/s reactant flow
    // whose pool weights are ~1e-6 × ~1e-4 — we keep the priority-loop
    // values already captured above. The drain-min only refines tie-breaks
    // and produces no useful information when the gradient is degenerate.
    lpSolver.Minimize(drainCostExpr ?? new LinearExpr());
    if (lpSolver.Solve() != Solver.ResultStatus.OPTIMAL) return;
    var costStar = lpSolver.Objective().Value();
    ExtractResults();

    // Phase C: fair distribution. Among solutions with the minimum drain
    // cost, pick the one that distributes drain proportionally to the
    // remaining contents in each pool — so symmetric same-priority pools
    // (asparagus side tanks, etc.) drain in lockstep instead of the LP
    // arbitrarily picking one to drain first based on the simplex basis.
    //
    // Per resource: minimize z_r subject to drain_p ≤ z_r * (amount_p /
    // maxAmount_r) for each pool p in resource r. At the optimum the
    // binding pools all have equal drain/amount ratios — equal time-to-
    // empty. Normalizing by maxAmount_r keeps the z coefficient inside
    // GLOP's working envelope even for MJ-scale battery pools.
    //
    // costPinConstraint locks the drain cost at costStar (1e-9 slack so
    // GLOP can move off the existing vertex). The objective is the sum
    // of all active z's — each minimizes independently within its
    // resource since cross-resource drain isn't coupled by demand.
    if (zVars.Count == 0) return;

    costPinConstraint.SetBounds(0, costStar + 1e-9);

    var fairObj = new LinearExpr();
    bool anyActive = false;
    foreach (var kvp in poolsByResource) {
      if (!zVars.TryGetValue(kvp.Key, out var z)) continue;
      double maxAmount = 0;
      foreach (var pool in kvp.Value) {
        var a = pool.TotalAmount;
        if (a > maxAmount) maxAmount = a;
      }
      if (maxAmount < 1e-9) continue;

      foreach (var pool in kvp.Value) {
        var amount = pool.TotalAmount;
        if (amount > 1e-9) {
          var normalized = Math.Max(amount / maxAmount, FairnessFloor);
          fairConstraints[pool].SetCoefficient(z, -normalized);
          fairConstraints[pool].SetBounds(double.NegativeInfinity, 0);
        }
      }
      fairObj += z;
      anyActive = true;
    }

    if (!anyActive) return;

    lpSolver.Minimize(fairObj);
    if (lpSolver.Solve() == Solver.ResultStatus.OPTIMAL) {
      ExtractResults();
    }
  }

  // ── Topology finalization ────────────────────────────────────────────

  private void FinalizeTopology() {
    var nodeResources = BuildNodeResources();
    BuildPools();
    BuildFlowVars(nodeResources);
    BuildConservationEntries(nodeResources);
  }

  private Dictionary<Node, HashSet<Resource>> BuildNodeResources() {
    var result = new Dictionary<Node, HashSet<Resource>>();
    foreach (var node in nodes) {
      var resources = new HashSet<Resource>();
      foreach (var d in node.devices) {
        foreach (var (res, _) in d.inputs) resources.Add(res);
        foreach (var (res, _) in d.outputs) resources.Add(res);
      }
      foreach (var b in node.buffers) resources.Add(b.Resource);
      result[node] = resources;
    }

    // Propagate: parent nodes get resources reachable through edges.
    bool changed = true;
    while (changed) {
      changed = false;
      foreach (var edge in edges) {
        if (!result.TryGetValue(edge.Child, out var childRes)) continue;
        if (!result.TryGetValue(edge.Parent, out var parentRes)) {
          parentRes = new HashSet<Resource>();
          result[edge.Parent] = parentRes;
        }
        foreach (var res in childRes) {
          if (edge.AllowedResources != null && !edge.AllowedResources.Contains(res)) continue;
          if (parentRes.Add(res)) changed = true;
        }
      }
    }

    return result;
  }

  private void BuildPools() {
    pools.Clear();
    foreach (var node in nodes) {
      var byResource = new Dictionary<Resource, Pool>();
      foreach (var buffer in node.buffers) {
        if (!byResource.TryGetValue(buffer.Resource, out var pool)) {
          pool = new Pool {
            Node = node,
            Resource = buffer.Resource,
            DrainPriority = node.DrainPriority,
          };
          byResource[buffer.Resource] = pool;
          pools.Add(pool);
        }
        pool.Tanks.Add(buffer);
      }
    }
    maxDrainPriority = pools.Count > 0 ? pools.Max(p => p.DrainPriority) : 0;
  }

  private void BuildFlowVars(Dictionary<Node, HashSet<Resource>> nodeResources) {
    flowVars.Clear();
    foreach (var edge in edges) {
      if (!nodeResources.TryGetValue(edge.Child, out var childRes)) continue;
      foreach (var res in childRes) {
        if (edge.AllowedResources != null && !edge.AllowedResources.Contains(res)) continue;
        flowVars.Add(new FlowVar {
          Parent = edge.Parent,
          Child = edge.Child,
          Resource = res,
          UpOnly = edge.UpOnlyResources.Contains(res),
        });
      }
    }
  }

  private void BuildConservationEntries(Dictionary<Node, HashSet<Resource>> nodeResources) {
    conservationEntries.Clear();
    conservationByNodeRes.Clear();
    foreach (var kvp in nodeResources) {
      foreach (var res in kvp.Value) {
        var entry = new ConservationEntry { Node = kvp.Key, Resource = res };
        conservationEntries.Add(entry);
        conservationByNodeRes[(kvp.Key, res)] = entry;
      }
    }
  }

  private ConservationEntry ConservationFor(Node node, Resource res) {
    return conservationByNodeRes.TryGetValue((node, res), out var entry) ? entry : null;
  }

  // ── One-shot LP construction ─────────────────────────────────────────

  private void BuildLP() {
    lpSolver = Solver.CreateSolver("GLOP");

    // Device variables.
    int di = 0;
    foreach (var node in nodes)
      foreach (var device in node.devices)
        device.Var = lpSolver.MakeNumVar(0, double.PositiveInfinity, $"d_{di++}");

    // Pool supply + fill variables.
    foreach (var pool in pools) {
      pool.SupplyVar = lpSolver.MakeNumVar(0, double.PositiveInfinity,
        $"s_{pool.Node.id}_{pool.Resource.Abbreviation}");
      pool.FillVar = lpSolver.MakeNumVar(0, double.PositiveInfinity,
        $"f_{pool.Node.id}_{pool.Resource.Abbreviation}");
    }

    // Flow variables.
    int fi = 0;
    foreach (var fv in flowVars) {
      double lb = double.NegativeInfinity;
      double ub = fv.UpOnly ? 0 : double.PositiveInfinity;
      fv.Var = lpSolver.MakeNumVar(lb, ub,
        $"flow_{fv.Parent.id}_{fv.Child.id}_{fv.Resource.Abbreviation}_{fi++}");
    }

    // Conservation equality constraints (RHS reset per tick to 0,0 since
    // we no longer accumulate fixedSupply across passes).
    foreach (var entry in conservationEntries)
      entry.Eq = lpSolver.MakeConstraint(0, 0,
        $"Cons_{entry.Node.id}_{entry.Resource.Abbreviation}");

    // Wire device input (consumption, -maxRate) and output (production,
    // +maxRate) coefficients. Topology-constant.
    foreach (var node in nodes)
      foreach (var device in node.devices) {
        foreach (var (res, maxRate) in device.inputs) {
          var entry = ConservationFor(node, res);
          if (entry != null)
            entry.Eq.SetCoefficient(device.Var, -maxRate);
        }
        foreach (var (res, maxRate) in device.outputs) {
          var entry = ConservationFor(node, res);
          if (entry != null)
            entry.Eq.SetCoefficient(device.Var, maxRate);
        }
      }

    // Wire pool supply (+1) and fill (-1) into conservation.
    foreach (var pool in pools) {
      var entry = ConservationFor(pool.Node, pool.Resource);
      if (entry != null) {
        entry.Eq.SetCoefficient(pool.SupplyVar, 1);
        entry.Eq.SetCoefficient(pool.FillVar, -1);
      }
    }

    // Wire flow variables (positive flow = parent→child: parent loses, child gains).
    foreach (var fv in flowVars) {
      var parentEntry = ConservationFor(fv.Parent, fv.Resource);
      var childEntry = ConservationFor(fv.Child, fv.Resource);
      if (parentEntry != null)
        parentEntry.Eq.SetCoefficient(fv.Var, -1);
      if (childEntry != null)
        childEntry.Eq.SetCoefficient(fv.Var, 1);
    }

    // Parent constraints: device.activity ≤ parent.activity. One-way only —
    // alternator-on-engine: alt is bounded by engine, but engine is free
    // to be at full throttle even when alt's output has nowhere to go.
    foreach (var node in nodes)
      foreach (var device in node.devices) {
        if (device.parent == null) continue;
        var c = lpSolver.MakeConstraint(double.NegativeInfinity, 0,
          $"Parent_{device.Var.Name()}");
        c.SetCoefficient(device.Var, 1);
        c.SetCoefficient(device.parent.Var, -1);
      }

    // Pre-allocate sum-pin constraints (one per device priority level).
    // Coefficients are 1 on every device of that priority — topology-constant.
    // Per-tick we toggle bounds (-inf,+inf for inactive, [lb,+inf] for active).
    devicePin = new Constraint[PriorityCount];
    for (int i = 0; i < PriorityCount; i++) {
      var pri = (Priority)i;
      devicePin[i] = lpSolver.MakeConstraint(double.NegativeInfinity, double.PositiveInfinity,
        $"PinSat_{pri}");
      foreach (var node in nodes)
        foreach (var device in node.devices)
          if (device.priority == pri)
            devicePin[i].SetCoefficient(device.Var, 1);
    }

    // Pre-build the drain cost objective expression. Per-pool weight =
    // Epsilon (uniform tie-breaker) + (MaxDP − DP) × Step (staging order)
    // + drainCosts[res] (caller override). FillVar is intentionally NOT
    // costed — that asymmetry is what prevents simultaneous drain+fill at
    // the same pool and cross-pool sloshing.
    //
    // costPinConstraint mirrors drainCostExpr's coefficients so Phase C
    // can pin the drain cost at its minimum (set per-tick after Phase B).
    if (pools.Count > 0) {
      drainCostExpr = new LinearExpr();
      costPinConstraint = lpSolver.MakeConstraint(0, double.PositiveInfinity, "CostPin");
      foreach (var pool in pools) {
        var dc = drainCosts.TryGetValue(pool.Resource, out var c) ? c : 0;
        var weight = DefaultDrainEpsilon
                     + (maxDrainPriority - pool.DrainPriority) * DrainPriorityStep
                     + dc;
        drainCostExpr += pool.SupplyVar * weight;
        costPinConstraint.SetCoefficient(pool.SupplyVar, weight);
      }
    }

    // Pre-allocate fair-distribution apparatus, one zVar per multi-pool
    // resource. Single-pool resources need no fairness (their drain is
    // already determined by demand + conservation). The SupplyVar
    // coefficient on each fair constraint is fixed at 1; the z
    // coefficient is set per-tick during Phase C, normalized by the
    // resource's max amount so the LP coefficient stays in [-1, 0].
    poolsByResource = new Dictionary<Resource, List<Pool>>();
    foreach (var pool in pools) {
      if (!poolsByResource.TryGetValue(pool.Resource, out var list))
        poolsByResource[pool.Resource] = list = new List<Pool>();
      list.Add(pool);
    }

    zVars = new Dictionary<Resource, Variable>();
    fairConstraints = new Dictionary<Pool, Constraint>();
    foreach (var kvp in poolsByResource) {
      if (kvp.Value.Count <= 1) continue;
      zVars[kvp.Key] = lpSolver.MakeNumVar(0, double.PositiveInfinity,
        $"z_{kvp.Key.Abbreviation}");
      foreach (var pool in kvp.Value) {
        var c = lpSolver.MakeConstraint(double.NegativeInfinity, double.PositiveInfinity,
          $"Fair_{pool.Node.id}_{pool.Resource.Abbreviation}");
        c.SetCoefficient(pool.SupplyVar, 1);
        fairConstraints[pool] = c;
      }
    }
  }

  // ── Per-tick reset ──────────────────────────────────────────────────

  private void ResetPerTickBounds() {
    // Device UBs from current state. Demand is the per-tick LP target;
    // MaxActivity is a separate cap (binary deploy/shadow toggle for
    // solar). Jettisoned nodes pin everything at 0.
    foreach (var node in nodes) {
      bool jett = node.Jettisoned;
      foreach (var device in node.devices) {
        var ub = jett ? 0 : Math.Min(device.Demand, device.MaxActivity);
        if (ub < 0) ub = 0;
        device.Var.SetBounds(0, ub);
      }
    }

    // Pool supply + fill UBs from buffer state. Both always enabled.
    foreach (var pool in pools) {
      pool.SupplyVar.SetBounds(0, pool.MaxSupplyRate);
      pool.FillVar.SetBounds(0, pool.MaxFillRate);
    }

    // Conservation RHS resets to (0, 0) — single-pass solve, no committed
    // accumulator carryover.
    foreach (var entry in conservationEntries)
      entry.Eq.SetBounds(0, 0);

    // All sum-pin constraints inactive — populated as priority loop runs.
    foreach (var pin in devicePin)
      pin.SetBounds(double.NegativeInfinity, double.PositiveInfinity);

    // Fair-distribution apparatus inactive until Phase C.
    if (costPinConstraint != null)
      costPinConstraint.SetBounds(0, double.PositiveInfinity);
    if (fairConstraints != null)
      foreach (var c in fairConstraints.Values)
        c.SetBounds(double.NegativeInfinity, double.PositiveInfinity);
  }

  // ── Objective helpers ────────────────────────────────────────────────

  private LinearExpr SumDeviceActivity(Priority priority) {
    var expr = new LinearExpr();
    foreach (var node in nodes)
      foreach (var device in node.devices)
        if (device.priority == priority)
          expr += device.Var;
    return expr;
  }

  // ── Result extraction ────────────────────────────────────────────────

  private void ExtractResults() {
    foreach (var node in nodes) {
      foreach (var device in node.devices)
        device.Activity = node.Jettisoned ? 0 : device.Var.SolutionValue();
    }

    foreach (var pool in pools) {
      if (pool.Node.Jettisoned) {
        foreach (var tank in pool.Tanks) tank.Rate = 0;
        continue;
      }
      // Net rate from buffer's perspective: SupplyVar drains, FillVar fills.
      // Positive netRate = drain (buffer loses); negative = fill.
      var supply = pool.SupplyVar.SolutionValue();
      var fill = pool.FillVar.SolutionValue();
      var netRate = supply - fill;
      var totalAmount = pool.TotalAmount;
      foreach (var tank in pool.Tanks) {
        if (netRate > 0) {
          var share = totalAmount > 1e-9 ? tank.Contents / totalAmount : 0;
          tank.Rate = -netRate * share;
        } else if (netRate < 0) {
          var totalSpace = pool.Tanks.Sum(t => t.Capacity - t.Contents);
          var share = totalSpace > 1e-9 ? (tank.Capacity - tank.Contents) / totalSpace : 0;
          tank.Rate = -netRate * share;
        } else {
          tank.Rate = 0;
        }
      }
    }
  }
}
