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

  // Vessel-level solar aggregation. One Device on the root node sums
  // every panel's ChargeRate as its topology output coefficient; per-tick
  // Demand caps the LP variable so output tops out at the SolarOptimizer
  // result (sunlit-and-deployed-aware). See ComputeSolarRates +
  // UpdateSolarDeviceDemand.
  private ResourceSolver.Device solarDevice;
  private double totalChargeRate;
  private double cachedOptimalRate;
  private bool vesselSunlit = true;
  private double nextShadowTransitionUT = double.PositiveInfinity;

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

    BuildSolarDevice(rootNode);

    needsSolve = true;
    simulationTime = time;
  }

  // Sum every panel's rated ChargeRate and create one aggregate producer
  // Device on the root node. Per-tick Demand (set by UpdateSolarDeviceDemand)
  // gates the LP variable to the SolarOptimizer-computed optimal rate.
  private void BuildSolarDevice(ResourceSolver.Node rootNode) {
    totalChargeRate = 0;
    foreach (var panel in AllComponents().OfType<SolarPanel>())
      totalChargeRate += panel.ChargeRate;

    if (totalChargeRate <= 0) {
      solarDevice = null;
      return;
    }

    solarDevice = rootNode.AddDevice(ResourceSolver.Priority.Low);
    solarDevice.AddOutput(Resource.ElectricCharge, totalChargeRate);
    solarDevice.Demand = 0;  // populated per-tick via UpdateSolarDeviceDemand
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
  /// then proportions the result to each panel by rated capacity. Caches
  /// the aggregate optimal rate for the vessel-level solar Device's per-tick
  /// Demand calc.
  /// </summary>
  public void ComputeSolarRates() {
    cachedOptimalRate = 0;
    var panels = AllComponents().OfType<SolarPanel>().ToList();
    if (panels.Count == 0) return;

    var deployed = new List<SolarOptimizer.Panel>();
    double deployedChargeRate = 0;

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
      deployedChargeRate += panel.ChargeRate;
    }

    if (deployedChargeRate <= 0) return;

    double optimalRate = SolarOptimizer.ComputeOptimalRate(deployed);
    cachedOptimalRate = optimalRate;

    foreach (var panel in panels) {
      if (panel.IsDeployed)
        panel.EffectiveRate = (panel.ChargeRate / deployedChargeRate) * optimalRate;
    }
  }

  // Per-tick LP gate for the aggregate solar Device. Demand ∈ [0, 1] is
  // (cachedOptimalRate / totalChargeRate) when the vessel is sunlit, 0
  // otherwise — so the LP variable's output tops out at the optimal-
  // orientation collection rate, not the rated max.
  private void UpdateSolarDeviceDemand() {
    if (solarDevice == null) return;
    if (totalChargeRate <= 0) {
      solarDevice.Demand = 0;
      return;
    }
    solarDevice.Demand = vesselSunlit ? cachedOptimalRate / totalChargeRate : 0;
    solarDevice.ValidUntil = nextShadowTransitionUT;
  }

  // Hysteresis bands for fuel cell auto-on/auto-off. Centralised here so
  // the same numbers drive both the on/off transition and the
  // valid-until forecast — there's exactly one place to update if the
  // bands ever need tuning.
  private const double FuelCellOnSocThreshold  = 0.20;
  private const double FuelCellOffSocThreshold = 0.80;

  // Pre-solve: aggregate vessel-wide battery SoC and apply hysteresis
  // to each FuelCell's IsActive flag. Sets Device.Demand for the
  // upcoming solve. The forecast (ValidUntilSeconds + Device.ValidUntil)
  // is intentionally NOT computed here — Buffer.Rate at this point is
  // from the previous solve, and the demand change we just made will
  // shift the rates the moment Solve runs. The post-solve sibling
  // DistributeFuelCellState reads the freshly-converged rates instead.
  //
  // The "no batteries" branch keeps the cell on continuously — without
  // a SoC signal there's no reason to throttle.
  private void UpdateFuelCellDevices() {
    var fuelCells = AllComponents().OfType<FuelCell>().ToList();
    if (fuelCells.Count == 0) return;

    double contents = 0, capacity = 0;
    foreach (var b in AllComponents().OfType<Battery>()) {
      contents += b.Buffer.Contents;
      capacity += b.Buffer.Capacity;
    }
    bool noBatteries = capacity < 1e-9;
    double soc = noBatteries ? 0 : contents / capacity;

    foreach (var fc in fuelCells) {
      if (fc.device == null) continue;

      if (noBatteries) {
        fc.IsActive = true;
      } else if (fc.IsActive && soc > FuelCellOffSocThreshold) {
        fc.IsActive = false;
      } else if (!fc.IsActive && soc < FuelCellOnSocThreshold) {
        fc.IsActive = true;
      }
      fc.device.Demand = fc.IsActive ? 1.0 : 0.0;
    }
  }

  // Post-solve: compute the valid-until forecast for each fuel cell
  // using the *converged* battery rates. Setting Device.ValidUntil
  // tells ComputeNextExpiry when the LP needs re-evaluation —
  // typically the next ON↔OFF threshold crossing.
  //
  // Forecast cases:
  //   ON  + charging → time until SoC reaches the OFF threshold (80%)
  //   OFF + draining → time until SoC reaches the ON threshold  (20%)
  //   anything else  → +∞ (the rate doesn't move SoC toward a flip)
  private void DistributeFuelCellState() {
    var fuelCells = AllComponents().OfType<FuelCell>().ToList();
    if (fuelCells.Count == 0) return;

    double contents = 0, capacity = 0, rate = 0;
    foreach (var b in AllComponents().OfType<Battery>()) {
      contents += b.Buffer.Contents;
      capacity += b.Buffer.Capacity;
      rate     += b.Buffer.Rate;  // signed: + = charging, − = draining
    }
    bool noBatteries = capacity < 1e-9;

    foreach (var fc in fuelCells) {
      if (fc.device == null) continue;

      double dtToFlip = double.PositiveInfinity;
      if (!noBatteries && Math.Abs(rate) > 1e-9) {
        if (fc.IsActive && rate > 0) {
          double remaining = FuelCellOffSocThreshold * capacity - contents;
          if (remaining > 0) dtToFlip = remaining / rate;
        } else if (!fc.IsActive && rate < 0) {
          double remaining = contents - FuelCellOnSocThreshold * capacity;
          if (remaining > 0) dtToFlip = remaining / (-rate);
        }
      }
      fc.ValidUntilSeconds = dtToFlip;
      fc.device.ValidUntil = double.IsPositiveInfinity(dtToFlip)
        ? double.PositiveInfinity
        : simulationTime + dtToFlip;
    }
  }

  private const int MaxTickIterations = 100;

  private void DoSolve() {
    try {
      UpdateShadowState();
      UpdateSolarDeviceDemand();
      UpdateFuelCellDevices();
      foreach (var component in AllComponents())
        component.OnPreSolve();
      solver.Solve();
      DistributeSolarPanelCurrentRates();
      DistributeFuelCellState();
      needsSolve = false;
      nextExpiry = ComputeNextExpiry(simulationTime);
    } catch (Exception e) {
      Log?.Invoke($"Solver error: {e.Message}");
      needsSolve = false;
      nextExpiry = double.PositiveInfinity;
    }
  }

  // Spread the LP-solved aggregate solar output across panels in
  // proportion to each panel's optimal share. Called post-Solve so
  // the per-panel telemetry reflects what actually flowed, not what
  // the panel could've produced in isolation.
  private void DistributeSolarPanelCurrentRates() {
    if (solarDevice == null || cachedOptimalRate <= 0) {
      foreach (var panel in AllComponents().OfType<SolarPanel>())
        panel.CurrentRate = 0;
      return;
    }
    double actualOutput = solarDevice.Activity * totalChargeRate;
    double scale = actualOutput / cachedOptimalRate;
    foreach (var panel in AllComponents().OfType<SolarPanel>())
      panel.CurrentRate = panel.EffectiveRate * scale;
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
    vesselSunlit = shadow.InSunlight;
    nextShadowTransitionUT = shadow.NextTransitionUT;
    // Stamp per-panel for telemetry — Power view's per-panel rows still
    // surface deploy/sunlit state. The LP doesn't read these anymore.
    foreach (var panel in AllComponents().OfType<SolarPanel>()) {
      panel.IsSunlit = shadow.InSunlight;
      panel.ShadowTransitionUT = shadow.NextTransitionUT;
    }
  }

  private double ComputeNextExpiry(double now) {
    var earliest = double.PositiveInfinity;
    foreach (var node in solver.AllNodes()) {
      earliest = Math.Min(earliest, now + node.TimeToNextExpiry());
      foreach (var device in node.Devices)
        if (device.ValidUntil < earliest)
          earliest = device.ValidUntil;
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
