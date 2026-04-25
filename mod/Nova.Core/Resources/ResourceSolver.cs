using System;
using System.Collections.Generic;
using System.Linq;
using Google.OrTools.LinearSolver;

namespace Nova.Core.Resources;

public class ResourceSolver {

  // ── Public API types ─────────────────────────────────────────────────

  public enum Priority { Critical, High, Low }

  public class Node {
    internal long id;
    public long Id => id;
    internal List<Device> devices = new();
    internal List<Converter> converters = new();
    internal List<Buffer> buffers = new();

    public double DryMass;
    public bool Jettisoned;
    public int DrainPriority;

    public IReadOnlyList<Device> Devices => devices;
    public IReadOnlyList<Converter> Converters => converters;
    public IReadOnlyList<Buffer> Buffers => buffers;

    public Device AddDevice(Priority priority) {
      var device = new Device { node = this, priority = priority };
      devices.Add(device);
      return device;
    }

    public Converter AddConverter() {
      var converter = new Converter { node = this };
      converters.Add(converter);
      return converter;
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

  public class Device {
    internal Node node;
    internal Priority priority;
    internal List<(Resource Resource, double MaxRate)> inputs = new();

    public double Demand;
    public double Activity;
    public double Satisfaction => Demand > 1e-9 ? Activity / Demand : 0;

    public void AddInput(Resource resource, double maxRate) {
      inputs.Add((resource, maxRate));
    }

}

  public class Converter {
    internal Node node;
    internal List<(Resource Resource, double MaxRate)> inputs = new();
    internal List<(Resource Resource, double MaxRate)> outputs = new();
    internal Device parent;

    public double Cost;
    public double ValidUntil = double.PositiveInfinity;
    public double Activity;

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
    public double TotalAmount => Tanks.Sum(t => t.Contents > 1e-9 ? t.Contents : 0);
    public double MaxSupplyRate => Tanks.Sum(t => t.Contents > 1e-9 ? t.MaxRateOut : 0);
    public double MaxFillRate => Tanks.Sum(t => t.Contents < t.Capacity - 1e-9 ? t.MaxRateIn : 0);
  }

  private class FlowVar {
    public Node Parent;
    public Node Child;
    public Resource Resource;
    public bool UpOnly;
  }

  private class ConservationEntry {
    public Node Node;
    public Resource Resource;
  }

  // ── State ────────────────────────────────────────────────────────────

  private List<Node> nodes = new();
  private List<Edge> edges = new();
  private Dictionary<Resource, double> drainCosts = new();
  private long nextNodeId;

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

  // ── Solve ────────────────────────────────────────────────────────────

  public void Solve() {
    // Build internal model from current state.
    var nodeResources = BuildNodeResources();
    var pools = BuildPools();
    var flowVars = BuildFlowVars(nodeResources);
    var conservationEntries = BuildConservationEntries(nodeResources);

    // Collect priority levels.
    var devicePriorities = new SortedSet<Priority>(
      nodes.SelectMany(n => n.devices).Select(d => d.priority));
    var drainPriorities = new SortedSet<int>(
      pools.Select(p => p.DrainPriority),
      Comparer<int>.Create((a, b) => b.CompareTo(a))); // descending

    // Accumulators across all passes.
    var fixedSupply = new Dictionary<(Node, Resource), double>();
    var poolDrainRates = new Dictionary<Pool, double>();
    var deviceActivities = new Dictionary<Device, double>();
    var converterActivities = new Dictionary<Converter, double>();

    // Priority passes: one LP per drain priority, device priority handled
    // by sequential objectives within each pass.
    foreach (var drainPri in drainPriorities) {
      var activePools = pools.Where(p =>
        p.DrainPriority == drainPri && p.MaxSupplyRate > 1e-9).ToList();
      if (activePools.Count == 0) continue;

      RunPriorityPass(activePools, fixedSupply, poolDrainRates,
        deviceActivities, converterActivities, flowVars, conservationEntries,
        devicePriorities);
    }

    // If no passes ran (no buffers or all empty), do a converters-only solve.
    if (deviceActivities.Count == 0) {
      var pris = devicePriorities.Count > 0 ? devicePriorities
        : new SortedSet<Priority> { Priority.Low };
      RunPriorityPass(new List<Pool>(), fixedSupply, poolDrainRates,
        deviceActivities, converterActivities, flowVars, conservationEntries, pris);
    }

    // Buffer fill pass.
    var fillPools = pools.Where(p => p.MaxFillRate > 1e-9).ToList();
    if (fillPools.Count > 0)
      RunFillPass(fillPools, fixedSupply, poolDrainRates,
        deviceActivities, converterActivities, flowVars, conservationEntries);

    // Extract results.
    ExtractResults(deviceActivities, converterActivities, poolDrainRates, pools);

  }

  // ── Internal model building ──────────────────────────────────────────

  private Dictionary<Node, HashSet<Resource>> BuildNodeResources() {
    var result = new Dictionary<Node, HashSet<Resource>>();
    foreach (var node in nodes) {
      var resources = new HashSet<Resource>();
      foreach (var d in node.devices)
        foreach (var (res, _) in d.inputs) resources.Add(res);
      foreach (var c in node.converters) {
        foreach (var (res, _) in c.inputs) resources.Add(res);
        foreach (var (res, _) in c.outputs) resources.Add(res);
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

  private List<Pool> BuildPools() {
    var pools = new List<Pool>();
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
    return pools;
  }

  private List<FlowVar> BuildFlowVars(Dictionary<Node, HashSet<Resource>> nodeResources) {
    var flowVars = new List<FlowVar>();
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
    return flowVars;
  }

  private List<ConservationEntry> BuildConservationEntries(
      Dictionary<Node, HashSet<Resource>> nodeResources) {
    var entries = new List<ConservationEntry>();
    foreach (var kvp in nodeResources)
      foreach (var res in kvp.Value)
        entries.Add(new ConservationEntry { Node = kvp.Key, Resource = res });
    return entries;
  }

  // ── LP construction ──────────────────────────────────────────────────

  private class LPInstance {
    public Solver Solver;
    public Dictionary<Device, Variable> DeviceVars;
    public Dictionary<Converter, Variable> ConverterVars;
    public Dictionary<Pool, Variable> PoolVars;
    public Dictionary<Pool, Variable> FillVars;
    public Dictionary<FlowVar, Variable> FlowVarMap;
    public Dictionary<ConservationEntry, Constraint> ConservationMap;
  }

  private LPInstance BuildLP(
      Dictionary<(Node, Resource), double> fixedSupply,
      List<Pool> supplyPools,
      List<Pool> fillPools,
      Dictionary<Device, double> pinnedDevices,
      Dictionary<Converter, double> pinnedConverters,
      List<FlowVar> flowVars,
      List<ConservationEntry> conservationEntries) {

    var solver = Solver.CreateSolver("GLOP");

    // Device activity variables.
    var deviceVars = new Dictionary<Device, Variable>();
    int di = 0;
    foreach (var node in nodes) {
      foreach (var device in node.devices) {
        double ub = node.Jettisoned ? 0 : device.Demand;
        if (pinnedDevices != null && pinnedDevices.TryGetValue(device, out var pinned))
          ub = Math.Min(ub, pinned + 1e-9);
        var lb = pinnedDevices != null && pinnedDevices.TryGetValue(device, out var pLb)
          ? Math.Max(0, pLb - 1e-9) : 0;
        deviceVars[device] = solver.MakeNumVar(lb, ub, $"d_{di++}");
      }
    }

    // Converter activity variables.
    var converterVars = new Dictionary<Converter, Variable>();
    int ci = 0;
    foreach (var node in nodes) {
      foreach (var converter in node.converters) {
        double ub = node.Jettisoned ? 0 : 1;
        if (pinnedConverters != null && pinnedConverters.TryGetValue(converter, out var pinned))
          ub = Math.Min(ub, pinned + 1e-9);
        var lb = pinnedConverters != null && pinnedConverters.TryGetValue(converter, out var cLb)
          ? Math.Max(0, cLb - 1e-9) : 0;
        converterVars[converter] = solver.MakeNumVar(lb, ub, $"c_{ci++}");
      }
    }

    // Pool supply variables.
    var poolVars = new Dictionary<Pool, Variable>();
    foreach (var pool in supplyPools) {
      poolVars[pool] = solver.MakeNumVar(0, pool.MaxSupplyRate,
        $"s_{pool.Node.id}_{pool.Resource.Abbreviation}");
    }

    // Pool fill variables.
    var fillVarMap = new Dictionary<Pool, Variable>();
    foreach (var pool in fillPools) {
      fillVarMap[pool] = solver.MakeNumVar(0, pool.MaxFillRate,
        $"fill_{pool.Node.id}_{pool.Resource.Abbreviation}");
    }

    // Flow variables.
    var flowVarMapResult = new Dictionary<FlowVar, Variable>();
    int fi = 0;
    foreach (var fv in flowVars) {
      var lb = double.NegativeInfinity;
      var fub = fv.UpOnly ? 0 : double.PositiveInfinity;
      flowVarMapResult[fv] = solver.MakeNumVar(lb, fub,
        $"f_{fv.Parent.id}_{fv.Child.id}_{fv.Resource.Abbreviation}_{fi++}");
    }

    // Conservation constraints.
    var conservationMap = new Dictionary<ConservationEntry, Constraint>();
    foreach (var entry in conservationEntries) {
      var committed = fixedSupply.TryGetValue((entry.Node, entry.Resource), out var fs) ? fs : 0;
      conservationMap[entry] = solver.MakeConstraint(-committed, -committed,
        $"Cons_{entry.Node.id}_{entry.Resource.Abbreviation}");
    }

    // Wire device inputs into conservation (consumption = negative).
    foreach (var node in nodes) {
      foreach (var device in node.devices) {
        foreach (var (res, maxRate) in device.inputs) {
          var entry = FindConservation(conservationEntries, node, res);
          if (entry != null)
            conservationMap[entry].SetCoefficient(deviceVars[device], -maxRate);
        }
      }
    }

    // Wire converter inputs/outputs into conservation.
    foreach (var node in nodes) {
      foreach (var converter in node.converters) {
        foreach (var (res, maxRate) in converter.inputs) {
          var entry = FindConservation(conservationEntries, node, res);
          if (entry != null)
            conservationMap[entry].SetCoefficient(converterVars[converter], -maxRate);
        }
        foreach (var (res, maxRate) in converter.outputs) {
          var entry = FindConservation(conservationEntries, node, res);
          if (entry != null)
            conservationMap[entry].SetCoefficient(converterVars[converter], maxRate);
        }
      }
    }

    // Wire pool supply into conservation (supply = positive).
    foreach (var pool in supplyPools) {
      var entry = FindConservation(conservationEntries, pool.Node, pool.Resource);
      if (entry != null)
        conservationMap[entry].SetCoefficient(poolVars[pool], 1);
    }

    // Wire pool fill into conservation (fill = negative).
    foreach (var pool in fillPools) {
      var entry = FindConservation(conservationEntries, pool.Node, pool.Resource);
      if (entry != null)
        conservationMap[entry].SetCoefficient(fillVarMap[pool], -1);
    }

    // Wire flow variables into conservation.
    // Positive flow = parent → child. Parent loses (-1), child gains (+1).
    foreach (var fv in flowVars) {
      var parentEntry = FindConservation(conservationEntries, fv.Parent, fv.Resource);
      var childEntry = FindConservation(conservationEntries, fv.Child, fv.Resource);
      if (parentEntry != null)
        conservationMap[parentEntry].SetCoefficient(flowVarMapResult[fv], -1);
      if (childEntry != null)
        conservationMap[childEntry].SetCoefficient(flowVarMapResult[fv], 1);
    }

    // Parent constraints: converter.activity <= parent.activity.
    foreach (var node in nodes) {
      foreach (var converter in node.converters) {
        if (converter.parent == null) continue;
        var c = solver.MakeConstraint(double.NegativeInfinity, 0,
          $"Parent_{converterVars[converter].Name()}");
        c.SetCoefficient(converterVars[converter], 1);
        c.SetCoefficient(deviceVars[converter.parent], -1);
      }
    }

    return new LPInstance {
      Solver = solver,
      DeviceVars = deviceVars,
      ConverterVars = converterVars,
      PoolVars = poolVars,
      FillVars = fillVarMap,
      FlowVarMap = flowVarMapResult,
      ConservationMap = conservationMap,
    };
  }

  private static LinearExpr SumDeviceActivity(LPInstance lp, Priority? priority = null) {
    var expr = new LinearExpr();
    foreach (var kvp in lp.DeviceVars) {
      if (priority == null || kvp.Key.priority == priority)
        expr += kvp.Value;
    }
    return expr;
  }

  private static ConservationEntry FindConservation(
      List<ConservationEntry> entries, Node node, Resource resource) {
    return entries.FirstOrDefault(e => e.Node == node && e.Resource == resource);
  }

  // ── Solve phases ─────────────────────────────────────────────────────

  private void RunPriorityPass(
      List<Pool> activePools,
      Dictionary<(Node, Resource), double> fixedSupply,
      Dictionary<Pool, double> poolDrainRates,
      Dictionary<Device, double> deviceActivities,
      Dictionary<Converter, double> converterActivities,
      List<FlowVar> flowVars,
      List<ConservationEntry> conservationEntries,
      IEnumerable<Priority> devicePriorities) {

    var lp = BuildLP(fixedSupply, activePools, new List<Pool>(),
      null, null, flowVars, conservationEntries);

    // Pin device activities from previous drain priority passes as LOWER bounds only.
    // Devices can increase their activity as more supply becomes available from
    // lower-priority drain pools — they just can't regress.
    foreach (var kvp in deviceActivities) {
      if (lp.DeviceVars.TryGetValue(kvp.Key, out var v)) {
        v.SetBounds(Math.Max(0, kvp.Value - 1e-9),
          kvp.Key.node.Jettisoned ? 0 : kvp.Key.Demand);
      }
    }

    // Satisfy device priorities sequentially within this LP.
    foreach (var devPri in devicePriorities) {
      lp.Solver.Maximize(SumDeviceActivity(lp, devPri));
      if (lp.Solver.Solve() != Solver.ResultStatus.OPTIMAL) return;

      var aStar = lp.Solver.Objective().Value();
      if (aStar > 1e-9) {
        // Pin this priority's satisfaction before solving the next.
        var pin = lp.Solver.MakeConstraint(aStar - 1e-9, double.PositiveInfinity, $"PinSat_{devPri}");
        foreach (var kvp in lp.DeviceVars)
          if (kvp.Key.priority == devPri)
            pin.SetCoefficient(kvp.Value, 1);
      }
    }

    // All device priorities satisfied. Now minimize cost.
    var costExpr = new LinearExpr();
    bool hasCost = false;
    foreach (var kvp in lp.ConverterVars) {
      if (kvp.Key.Cost > 0) {
        costExpr += kvp.Value * kvp.Key.Cost;
        hasCost = true;
      }
    }
    foreach (var kvp in lp.PoolVars) {
      var drainCost = drainCosts.TryGetValue(kvp.Key.Resource, out var dc) ? dc : 0;
      if (drainCost > 0) {
        costExpr += kvp.Value * drainCost;
        hasCost = true;
      }
    }
    if (hasCost) {
      lp.Solver.Minimize(costExpr);
      lp.Solver.Solve();
    }

    // Fair distribution among pools.
    if (activePools.Count > 1) {
      if (hasCost) {
        var costVal = lp.Solver.Objective().Value();
        var pinCost = lp.Solver.MakeConstraint(double.NegativeInfinity, costVal + 1e-9, "PinCost");
        foreach (var kvp in lp.ConverterVars)
          if (kvp.Key.Cost > 0) pinCost.SetCoefficient(kvp.Value, kvp.Key.Cost);
        foreach (var kvp in lp.PoolVars) {
          var dc = drainCosts.TryGetValue(kvp.Key.Resource, out var c) ? c : 0;
          if (dc > 0) pinCost.SetCoefficient(kvp.Value, dc);
        }
      }

      var z = lp.Solver.MakeNumVar(0, double.PositiveInfinity, "z");
      foreach (var pool in activePools) {
        var amount = pool.TotalAmount;
        if (amount < 1e-9) continue;
        var fair = lp.Solver.MakeConstraint(double.NegativeInfinity, 0,
          $"Fair_{pool.Node.id}_{pool.Resource.Abbreviation}");
        fair.SetCoefficient(lp.PoolVars[pool], 1);
        fair.SetCoefficient(z, -amount);
      }
      lp.Solver.Minimize(z);
      lp.Solver.Solve();
    }

    // Re-solve to ensure solution is current after constraint additions.
    // Use cost minimization if available, otherwise neutral objective.
    if (hasCost) {
      lp.Solver.Minimize(costExpr);
    } else {
      lp.Solver.Minimize(new LinearExpr());
    }
    lp.Solver.Solve();

    // Final snapshot.
    foreach (var kvp in lp.DeviceVars)
      deviceActivities[kvp.Key] = kvp.Value.SolutionValue();
    foreach (var kvp in lp.ConverterVars)
      converterActivities[kvp.Key] = kvp.Value.SolutionValue();

    // Commit pool supply.
    foreach (var pool in activePools) {
      var rate = lp.PoolVars[pool].SolutionValue();
      poolDrainRates[pool] = rate;
      var key = (pool.Node, pool.Resource);
      if (!fixedSupply.ContainsKey(key)) fixedSupply[key] = 0;
      fixedSupply[key] += rate;
    }
  }

  private void RunFillPass(
      List<Pool> fillPools,
      Dictionary<(Node, Resource), double> fixedSupply,
      Dictionary<Pool, double> poolDrainRates,
      Dictionary<Device, double> deviceActivities,
      Dictionary<Converter, double> converterActivities,
      List<FlowVar> flowVars,
      List<ConservationEntry> conservationEntries) {

    var lp = BuildLP(fixedSupply, new List<Pool>(), fillPools,
      deviceActivities, null, flowVars, conservationEntries);

    // Step 5: maximize fill.
    var fillGoal = new LinearExpr();
    foreach (var kvp in lp.FillVars)
      fillGoal += kvp.Value;
    lp.Solver.Maximize(fillGoal);
    var status = lp.Solver.Solve();
    if (status != Solver.ResultStatus.OPTIMAL) return;

    // Snapshot after step 5.
    foreach (var kvp in lp.ConverterVars)
      converterActivities[kvp.Key] = kvp.Value.SolutionValue();
    var fillSnapshot = new Dictionary<Pool, double>();
    foreach (var pool in fillPools)
      if (lp.FillVars.TryGetValue(pool, out var fv))
        fillSnapshot[pool] = fv.SolutionValue();

    // Step 6: pin fill, minimize cost.
    var fillStar = lp.Solver.Objective().Value();
    if (fillStar > 1e-9) {
      var pinFill = lp.Solver.MakeConstraint(fillStar - 1e-9, double.PositiveInfinity, "PinFill");
      foreach (var kvp in lp.FillVars)
        pinFill.SetCoefficient(kvp.Value, 1);

      var costExpr = new LinearExpr();
      bool hasCost = false;
      foreach (var kvp in lp.ConverterVars) {
        if (kvp.Key.Cost > 0) {
          costExpr += kvp.Value * kvp.Key.Cost;
          hasCost = true;
        }
      }
      if (hasCost) {
        lp.Solver.Minimize(costExpr);
        if (lp.Solver.Solve() == Solver.ResultStatus.OPTIMAL) {
          foreach (var kvp in lp.ConverterVars)
            converterActivities[kvp.Key] = kvp.Value.SolutionValue();
          foreach (var pool in fillPools)
            if (lp.FillVars.TryGetValue(pool, out var fv2))
              fillSnapshot[pool] = fv2.SolutionValue();
        }
      }
    }

    // Record fill rates.
    foreach (var pool in fillPools) {
      var fillRate = fillSnapshot.TryGetValue(pool, out var snap) ? snap : 0;
      if (!poolDrainRates.ContainsKey(pool))
        poolDrainRates[pool] = 0;
      poolDrainRates[pool] -= fillRate; // negative = filling
    }
  }

  // ── Result extraction ────────────────────────────────────────────────

  private void ExtractResults(
      Dictionary<Device, double> deviceActivities,
      Dictionary<Converter, double> converterActivities,
      Dictionary<Pool, double> poolDrainRates,
      List<Pool> pools) {

    // Write device activities.
    foreach (var node in nodes) {
      foreach (var device in node.devices) {
        device.Activity = node.Jettisoned ? 0
          : deviceActivities.TryGetValue(device, out var a) ? a : 0;
      }
    }

    // Write converter activities.
    foreach (var node in nodes) {
      foreach (var converter in node.converters)
        converter.Activity = node.Jettisoned ? 0
          : converterActivities.TryGetValue(converter, out var a) ? a : 0;
    }

    // Distribute pool drain/fill rates to individual buffers.
    foreach (var pool in pools) {
      if (pool.Node.Jettisoned) {
        foreach (var tank in pool.Tanks) tank.Rate = 0;
        continue;
      }
      var netRate = poolDrainRates.TryGetValue(pool, out var r) ? r : 0;
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
