using System;
using Nova.Core.Persistence.Protos;
using System.Collections.Generic;
using System.Linq;
using Nova.Core.Components;
using Nova.Core.Components.Electrical;
using Nova.Core.Components.Propulsion;
using Nova.Core.Components.Structural;
using Nova.Core.Resources;
using Nova.Core.Flight;
using Nova.Core.Utils;
namespace Nova.Core.Components;

public class VirtualVessel {
  private Dictionary<uint, Part> parts = new();
  private Dictionary<uint, uint?> partTree = new();
  private Dictionary<uint, double> partDryMasses = new();
  private ResourceSolver solver;
  private bool needsSolve = true;
  private double simulationTime;
  private double nextExpiry = double.PositiveInfinity;

  public Func<double, Vec3d> GetVesselPosition;
  public Func<double, Vec3d> GetSunDirection;
  public double OrbitPeriod;
  public double BodyRadius;
  public bool OrbitingSun;

  public Action<string> Log;

  public ResourceSolver Solver => solver;

  public IEnumerable<VirtualComponent> GetComponents(uint persistentId) {
    if (!parts.TryGetValue(persistentId, out var part)) {
      return Enumerable.Empty<VirtualComponent>();
    }
    return part.components;
  }

  public string GetPartName(uint persistentId) {
    return parts.TryGetValue(persistentId, out var part) ? part.partName : null;
  }

  public uint? GetPartParent(uint persistentId) {
    return partTree.TryGetValue(persistentId, out var parent) ? parent : null;
  }

  /// <summary>
  /// Register a part's flight components. Called by HgsPartModule.OnStartFlight
  /// when no VirtualVessel pre-exists (case 1: vessel loaded with Parts).
  /// </summary>
  public void AddPart(uint id, string partName, double dryMass, List<VirtualComponent> components) {
    if (parts.TryGetValue(id, out var existing)) {
      existing.components.AddRange(components);
    } else {
      parts[id] = new Part { partName = partName, dryMass = dryMass, components = components };
    }
    partDryMasses[id] = dryMass;
  }

  public void SetPartDryMass(uint id, double dryMass) {
    partDryMasses[id] = dryMass;
  }

  public void InitializeSolver(double time) {
    solver = new ResourceSolver();

    if (partTree.Count == 0)
      throw new System.InvalidOperationException("Cannot initialize solver: part tree is empty. Was UpdatePartTree called?");

    var root = partTree.FirstOrDefault(p => p.Value == null);
    if (!partTree.ContainsKey(root.Key))
      throw new System.InvalidOperationException("Cannot initialize solver: no root part found in part tree.");

    var rootNode = solver.AddNode();
    WalkPartTree(rootNode, root.Key);

    needsSolve = true;
    simulationTime = time;
  }

  /// <summary>
  /// Walk the full KSP part tree. Every part ID is visited (including
  /// non-HGS parts). HGS components are attached to the current solver
  /// node. Decoupler components create a child node.
  /// </summary>
  private void WalkPartTree(ResourceSolver.Node solverNode, uint partId) {
    parts.TryGetValue(partId, out var partEntry);
    var partDryMass = partDryMasses.TryGetValue(partId, out var mass) ? mass : 0;

    if (partEntry != null) {
      var decoupler = partEntry.components.OfType<Decoupler>().FirstOrDefault();
      if (decoupler != null) {
        var childNode = solver.AddNode();
        childNode.id = (long)partId;
        childNode.DrainPriority = decoupler.Priority;
        solver.AddEdge(solverNode, childNode, decoupler.AllowedResources, decoupler.UpOnlyResources);
        solverNode = childNode;
      }

      var dockingPort = partEntry.components.OfType<DockingPort>().FirstOrDefault();
      if (dockingPort != null) {
        var childNode = solver.AddNode();
        childNode.id = (long)partId;
        childNode.DrainPriority = dockingPort.Priority;
        var allowed = dockingPort.AllowedResources.Count == 0 ? null : dockingPort.AllowedResources;
        solver.AddEdge(solverNode, childNode, allowed, dockingPort.UpOnlyResources);
        solverNode = childNode;
      }

      foreach (var cmp in partEntry.components)
        cmp.OnBuildSolver(solver, solverNode);
    }

    solverNode.DryMass += partDryMass;

    foreach (var child in partTree.Where(p => p.Value == partId))
      WalkPartTree(solverNode, child.Key);
  }

  public IEnumerable<uint> AllPartIds() {
    return parts.Keys;
  }

  public IEnumerable<VirtualComponent> AllComponents() {
    return parts.Values.SelectMany(p => p.components);
  }

  public void Invalidate() {
    needsSolve = true;
  }

  /// <summary>
  /// Compute optimal solar rates for all panels on the vessel.
  /// Finds the sun direction that maximizes total power given panel geometry,
  /// then proportions the result to each panel by rated capacity.
  /// </summary>
  public void ComputeSolarRates() {
    var panels = AllComponents().OfType<SolarPanel>().ToList();
    if (panels.Count == 0) return;

    var deployed = new List<SolarOptimizer.Panel>();
    double totalChargeRate = 0;

    foreach (var panel in panels) {
      if (!panel.IsDeployed) {
        panel.EffectiveRate = 0;
        continue;
      }
      deployed.Add(new SolarOptimizer.Panel {
        Direction = panel.PanelDirection,
        ChargeRate = panel.ChargeRate,
        IsTracking = panel.IsTracking,
      });
      totalChargeRate += panel.ChargeRate;
    }

    if (totalChargeRate <= 0) return;

    double optimalRate = SolarOptimizer.ComputeOptimalRate(deployed);

    foreach (var panel in panels) {
      if (panel.IsDeployed)
        panel.EffectiveRate = (panel.ChargeRate / totalChargeRate) * optimalRate;
    }
  }

  private const int MaxTickIterations = 100;

  private void DoSolve() {
    try {
      UpdateShadowState();
      foreach (var component in AllComponents())
        component.OnPreSolve();
      solver.Solve();
      needsSolve = false;
      nextExpiry = ComputeNextExpiry(simulationTime);
    } catch (Exception e) {
      Log?.Invoke($"Solver error: {e.Message}");
      needsSolve = false;
      nextExpiry = double.PositiveInfinity;
    }
  }

  public void Tick(double targetTime) {
    if (solver == null) return;

    var iterations = 0;

    while (simulationTime < targetTime) {
      if (++iterations > MaxTickIterations) {
        Log?.Invoke($"Tick() exceeded {MaxTickIterations} iterations, forcing advance. simTime={simulationTime} target={targetTime} nextExpiry={nextExpiry}");
        simulationTime = targetTime;
        break;
      }

      var nextStop = Math.Min(targetTime, nextExpiry);

      if (nextStop > simulationTime) {
        IntegrateBuffers(nextStop - simulationTime);
        simulationTime = nextStop;
      } else if (!needsSolve && simulationTime >= nextExpiry) {
        needsSolve = true;
      }

      if (needsSolve || simulationTime >= nextExpiry) {
        DoSolve();
      }
    }

    if (needsSolve) {
      DoSolve();
    }
  }

  private void IntegrateBuffers(double deltaT) {
    if (deltaT <= 0) return;
    foreach (var node in solver.AllNodes())
      node.IntegrateBuffers(deltaT);
  }

  private void UpdateShadowState() {
    var shadow = ShadowCalculator.Compute(GetVesselPosition, GetSunDirection,
        OrbitPeriod, BodyRadius, OrbitingSun, simulationTime);
    foreach (var panel in AllComponents().OfType<SolarPanel>()) {
      panel.IsSunlit = shadow.InSunlight;
      panel.ShadowTransitionUT = shadow.NextTransitionUT;
    }
  }

  private double ComputeNextExpiry(double now) {
    var earliest = double.PositiveInfinity;
    foreach (var node in solver.AllNodes()) {
      earliest = Math.Min(earliest, now + node.TimeToNextExpiry());
      foreach (var converter in node.Converters)
        if (converter.ValidUntil < earliest)
          earliest = converter.ValidUntil;
    }
    return earliest;
  }

  /// <summary>
  /// Capture current buffer values as a flat array. Order is deterministic
  /// (parts sorted by ID, components in order, tanks in order).
  /// </summary>
  public double[] CaptureBufferSnapshot() {
    var values = new List<double>();
    foreach (var partId in parts.Keys.OrderBy(k => k)) {
      foreach (var cmp in parts[partId].components) {
        if (cmp is TankVolume tank)
          foreach (var t in tank.Tanks)
            values.Add(t.Contents);
      }
    }
    return values.ToArray();
  }

  /// <summary>
  /// Apply a buffer snapshot and reset simulation state (engine throttle,
  /// jettison flags) so the vessel is ready for a fresh simulation run.
  /// </summary>
  public void ApplyBufferSnapshot(double[] values, double time) {
    int idx = 0;
    foreach (var partId in parts.Keys.OrderBy(k => k)) {
      foreach (var cmp in parts[partId].components) {
        if (cmp is TankVolume tank) {
          foreach (var t in tank.Tanks)
            t.Contents = values[idx++];
        } else if (cmp is Engine engine) {
          engine.Throttle = 0;
        } else if (cmp is Rcs rcs) {
          rcs.Throttle = 0;
        }
      }
    }
    foreach (var node in solver.AllNodes())
      node.Jettisoned = false;
    simulationTime = time;
    needsSolve = true;
  }

  /// <summary>
  /// Run OnPreSolve on all components and solve the resource system.
  /// Used by DeltaVSimulation on cloned vessels.
  /// </summary>
  public void Solve() {
    foreach (var cmp in AllComponents())
      cmp.OnPreSolve();
    solver.Solve();
  }

  public void UpdatePartTree(Dictionary<uint, uint?> parentMap) {
    partTree = parentMap;
  }

  /// <summary>
  /// Create an independent deep copy of this vessel. Components are cloned
  /// via their Clone() methods. The clone has its own solver.
  /// </summary>
  public VirtualVessel Clone(double time) {
    var clone = new VirtualVessel();
    clone.partTree = new Dictionary<uint, uint?>(partTree);
    clone.partDryMasses = new Dictionary<uint, double>(partDryMasses);

    foreach (var kvp in parts) {
      var clonedComponents = new List<VirtualComponent>();
      foreach (var cmp in kvp.Value.components) {
        clonedComponents.Add(cmp.Clone());
      }
      clone.parts[kvp.Key] = new Part {
        partName = kvp.Value.partName,
        dryMass = kvp.Value.dryMass,
        components = clonedComponents,
      };
    }

    clone.InitializeSolver(time);
    return clone;
  }

  public Dictionary<uint, List<VirtualComponent>> ExtractParts(HashSet<uint> partIds) {
    var extracted = new Dictionary<uint, List<VirtualComponent>>();
    foreach (var id in partIds) {
      if (parts.TryGetValue(id, out var part)) {
        extracted[id] = part.components;
        parts.Remove(id);
      }
    }
    return extracted;
  }

  /// <summary>
  /// Absorb parts from another vessel during docking. Caller must
  /// call UpdatePartTree + InitializeSolver (via RebuildTopology) after.
  /// </summary>
  public void MergeParts(Dictionary<uint, List<VirtualComponent>> otherParts,
      Dictionary<uint, string> partNames, Dictionary<uint, double> dryMasses) {
    foreach (var kvp in otherParts) {
      parts[kvp.Key] = new Part {
        partName = partNames.TryGetValue(kvp.Key, out var name) ? name : "",
        dryMass = dryMasses.TryGetValue(kvp.Key, out var mass) ? mass : 0,
        components = kvp.Value,
      };
      partDryMasses[kvp.Key] = dryMasses.TryGetValue(kvp.Key, out var dm) ? dm : 0;
    }
  }

  public static VirtualVessel FromExistingParts(
      Dictionary<uint, List<VirtualComponent>> existingParts,
      Dictionary<uint, uint?> parentMap,
      Dictionary<uint, string> partNames,
      Dictionary<uint, double> dryMasses,
      double time) {
    var vessel = new VirtualVessel();
    vessel.partTree = parentMap;
    vessel.partDryMasses = new Dictionary<uint, double>(dryMasses);
    foreach (var kvp in existingParts) {
      vessel.parts[kvp.Key] = new Part {
        partName = partNames.TryGetValue(kvp.Key, out var name) ? name : "",
        dryMass = dryMasses.TryGetValue(kvp.Key, out var mass) ? mass : 0,
        components = kvp.Value,
      };
    }
    vessel.InitializeSolver(time);
    return vessel;
  }

  public void SavePartState(uint partId, PartState state) {
    if (!parts.TryGetValue(partId, out var part)) return;
    foreach (var cmp in part.components)
      cmp.Save(state);
  }

  public void LoadPartState(uint partId, PartState state) {
    if (!parts.TryGetValue(partId, out var part))
      throw new System.InvalidOperationException($"Part {partId} not found in VirtualVessel");
    foreach (var cmp in part.components)
      cmp.Load(state);
  }

  private class Part {
    public string partName;
    public double dryMass;
    public List<VirtualComponent> components = new();
  }
}
