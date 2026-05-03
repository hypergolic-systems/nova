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
using Nova.Core.Systems;
namespace Nova.Core.Components;

public class VirtualVessel {
  private Dictionary<uint, Part> parts = new();
  private Dictionary<uint, uint?> partTree = new();
  private Dictionary<uint, double> partDryMasses = new();
  private VesselSystems systems;
  private bool needsSolve = true;
  private double simulationTime;
  private double nextExpiry = double.PositiveInfinity;

  // Monotonic count of solves (staging + process round). Useful for
  // tests asserting that a particular event (e.g. wheel-buffer crossing)
  // DID or DIDN'T trigger a re-solve.
  public int SolveCount { get; private set; }

  // Vessel-level solar aggregation. One Process Device sums every
  // panel's ChargeRate as its output coefficient; per-tick Demand caps
  // the LP variable so output tops out at the SolarOptimizer result
  // (sunlit-and-deployed-aware). See ComputeSolarRates +
  // UpdateSolarDeviceDemand.
  private ProcessFlowSystem.Device solarDevice;
  private double totalChargeRate;
  private double cachedOptimalRate;
  private bool vesselSunlit = true;
  private double nextShadowTransitionUT = double.PositiveInfinity;

  // Live adapter over the host KSP vessel. Mod-side wraps `vessel`;
  // tests inject a stub. Single source of truth for body, situation,
  // orbit, etc — no caching, no sync hazard.
  public IVesselContext Context;

  public Action<string> Log;

  public VesselSystems Systems => systems;

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
    foreach (var cmp in components) cmp.Vessel = this;
  }

  public void SetPartDryMass(uint id, double dryMass) {
    partDryMasses[id] = dryMass;
  }

  public void InitializeSolver(double time) {
    systems = new VesselSystems();
    // Anchor the clock at the initial UT so Contents lerps reference
    // a sensible baseline even before the first Solve.
    systems.Clock.UT = time;

    if (partTree.Count == 0)
      throw new System.InvalidOperationException("Cannot initialize solver: part tree is empty. Was UpdatePartTree called?");

    var root = partTree.FirstOrDefault(p => p.Value == null);
    if (!partTree.ContainsKey(root.Key))
      throw new System.InvalidOperationException("Cannot initialize solver: no root part found in part tree.");

    var rootNode = systems.Staging.AddNode();
    WalkPartTree(rootNode, root.Key);

    BuildSolarDevice(rootNode);

    needsSolve = true;
    simulationTime = time;
  }

  // Sum every panel's rated ChargeRate and create one aggregate producer
  // Device on the Process system. Per-tick Demand (set by
  // UpdateSolarDeviceDemand) gates the LP variable to the SolarOptimizer-
  // computed optimal rate.
  private void BuildSolarDevice(StagingFlowSystem.Node rootNode) {
    totalChargeRate = 0;
    foreach (var panel in AllComponents().OfType<SolarPanel>())
      totalChargeRate += panel.ChargeRate;

    if (totalChargeRate <= 0) {
      solarDevice = null;
      return;
    }

    solarDevice = systems.Process.AddDevice(ProcessFlowSystem.Priority.Low);
    solarDevice.AddOutput(Resource.ElectricCharge, totalChargeRate);
    solarDevice.Demand = 0;  // populated per-tick via UpdateSolarDeviceDemand
  }

  /// <summary>
  /// Walk the full KSP part tree. Every part ID is visited (including
  /// non-HGS parts). HGS components register on the systems via the
  /// current staging node. Decoupler / DockingPort components create a
  /// child staging node.
  /// </summary>
  private void WalkPartTree(StagingFlowSystem.Node stagingNode, uint partId) {
    parts.TryGetValue(partId, out var partEntry);
    var partDryMass = partDryMasses.TryGetValue(partId, out var mass) ? mass : 0;

    if (partEntry != null) {
      foreach (var cmp in partEntry.components) cmp.Vessel = this;
      var decoupler = partEntry.components.OfType<Decoupler>().FirstOrDefault();
      if (decoupler != null) {
        var childNode = systems.Staging.AddNode();
        childNode.Id = (long)partId;
        childNode.DrainPriority = decoupler.Priority;
        systems.Staging.AddEdge(stagingNode, childNode, decoupler.AllowedResources, decoupler.UpOnlyResources);
        stagingNode = childNode;
      }

      var dockingPort = partEntry.components.OfType<DockingPort>().FirstOrDefault();
      if (dockingPort != null) {
        var childNode = systems.Staging.AddNode();
        childNode.Id = (long)partId;
        childNode.DrainPriority = dockingPort.Priority;
        var allowed = dockingPort.AllowedResources.Count == 0 ? null : dockingPort.AllowedResources;
        systems.Staging.AddEdge(stagingNode, childNode, allowed, dockingPort.UpOnlyResources);
        stagingNode = childNode;
      }

      foreach (var cmp in partEntry.components)
        cmp.OnBuildSystems(systems, stagingNode);
    }

    stagingNode.DryMass += partDryMass;

    foreach (var child in partTree.Where(p => p.Value == partId))
      WalkPartTree(stagingNode, child.Key);
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

  private const int MaxTickIterations = 100;

  // Floor for the per-iteration step in Tick. A device's `ValidUntil` can
  // legitimately land within FP precision of the current simulationTime
  // (e.g. a wheel-buffer forecast where slack/rate < ulp(simTime) makes
  // `simulationTime + dt == simulationTime` exactly). Without a floor,
  // `nextStop - simulationTime` is then 0, no integration runs, and the
  // loop spins on the same time point until MaxTickIterations forces it
  // through. 1 µs is well above representable-time precision even at
  // multi-year simTime, and small enough to be physically unobservable.
  private const double MinTickStep = 1e-6;

  private void DoSolve() {
    SolveCount++;
    try {
      UpdateShadowState();
      UpdateSolarDeviceDemand();
      foreach (var component in AllComponents())
        component.OnPreSolve();

      systems.Solve();

      DistributeSolarPanelCurrentRates();
      foreach (var component in AllComponents())
        component.OnPostSolve();
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
    if (systems == null) return;

    // External mutations between ticks (e.g. SolveAttitude setting wheel
    // throttles) don't invalidate the LP, but may invalidate component
    // forecasts. Give every component a chance to refresh before the
    // first ComputeNextExpiry consumes them.
    foreach (var c in AllComponents()) c.OnTickBegin();

    var iterations = 0;

    while (simulationTime < targetTime) {
      if (++iterations > MaxTickIterations) {
        Log?.Invoke($"Tick() exceeded {MaxTickIterations} iterations, forcing advance. simTime={simulationTime} target={targetTime} nextExpiry={nextExpiry}");
        // Force-advance: bump both simulationTime and the shared
        // clock so Contents lerps reflect the target time.
        systems.AdvanceClock(targetTime - simulationTime);
        simulationTime = targetTime;
        break;
      }

      // Solve FIRST if invalidated. This sets buffer rates at the
      // current clock UT, so the lerp during the upcoming clock
      // advance reflects them. Pre-lerp Tick had this backwards —
      // it integrated first (with rate=0 on a fresh vessel) and
      // solved at the end, which left a window of unaccounted-for
      // drain on the very first tick after init.
      if (needsSolve) {
        DoSolve();
      }

      nextExpiry = ComputeNextExpiry(simulationTime);

      // Floor the step at MinTickStep ahead of simulationTime — a
      // ValidUntil within FP precision of `now` would otherwise land
      // nextStop == simulationTime and stall. Capped by targetTime so
      // we never overshoot the requested target.
      var nextStop = Math.Min(targetTime,
          Math.Max(nextExpiry, simulationTime + MinTickStep));

      var dt = nextStop - simulationTime;
      if (dt > 0) {
        systems.AdvanceClock(dt);
        // Component-internal accumulators (FuelCell.Manifold,
        // ReactionWheel.Buffer) integrate against last solve's
        // Activities here. System-owned Buffers lerp themselves
        // against the shared SimClock — nothing to do for those.
        foreach (var c in AllComponents()) c.OnAdvance(dt);
        simulationTime = nextStop;
      }

      // Fire any component whose ValidUntil has elapsed (slice rollover,
      // scheduled emissions). Component-driven Demand changes propagate
      // via the needsSolve flag — next iter's DoSolve picks them up.
      if (FireComponentUpdates(simulationTime))
        needsSolve = true;

      // Crossing nextExpiry means a forecasted event (buffer empties,
      // fuel-cell hysteresis flip, etc.) just fired — re-solve next
      // iter to reflect the new state.
      if (simulationTime >= nextExpiry)
        needsSolve = true;
    }

    // Final solve if still invalidated — keeps state consistent for
    // subsequent reads even if no further Tick happens.
    if (needsSolve) {
      DoSolve();
    }
  }


  private void UpdateShadowState() {
    if (Context == null) return;
    var shadow = ShadowCalculator.Compute(Context.VesselPositionAt, Context.SunDirectionAt,
        Context.OrbitPeriod, Context.BodyRadius, Context.OrbitingSun, simulationTime);
    vesselSunlit = shadow.InSunlight;
    nextShadowTransitionUT = shadow.NextTransitionUT;
    // Stamp per-panel for telemetry — Power view's per-panel rows still
    // surface deploy/sunlit state. The LP doesn't read these anymore.
    foreach (var panel in AllComponents().OfType<SolarPanel>()) {
      panel.IsSunlit = shadow.InSunlight;
      panel.ShadowTransitionUT = shadow.NextTransitionUT;
    }
  }

  // Next forecasted state-change time, in absolute simulationTime
  // coordinates. Combines the systems' bubbled-up `MaxTickDt` (the
  // VesselSystems orchestrator owns the cross-system min) with
  // component-level ValidUntils (science slice rollovers, fuel-
  // cell refill flip, anything component-owned that isn't a system
  // concern).
  private double ComputeNextExpiry(double now) {
    var earliest = double.PositiveInfinity;

    var systemsDt = systems.MaxTickDt();
    if (!double.IsPositiveInfinity(systemsDt))
      earliest = now + systemsDt;

    foreach (var cmp in AllComponents())
      if (cmp.ValidUntil < earliest)
        earliest = cmp.ValidUntil;
    return earliest;
  }

  // Fire Update(now) on every component whose ValidUntil has elapsed.
  // Component contract: Update MUST advance ValidUntil before returning
  // (or set it back to +Infinity to stop scheduling), or this method
  // would re-fire the same component repeatedly. Returns true iff any
  // component fired — caller marks needsSolve so any Demand changes
  // propagate on the next solve.
  private bool FireComponentUpdates(double now) {
    bool any = false;
    foreach (var cmp in AllComponents()) {
      if (cmp.ValidUntil <= now) {
        cmp.Update(now);
        any = true;
      }
    }
    return any;
  }

  /// <summary>
  /// Run OnPreSolve on all components, solve the resource systems, then
  /// run OnPostSolve so component-internal forecasts (FuelCell ValidUntil,
  /// ReactionWheel refill ValidUntil, etc.) reflect the just-solved
  /// Activities. Used by DeltaVSimulation on cloned vessels and by tests.
  /// </summary>
  public void Solve() {
    foreach (var cmp in AllComponents())
      cmp.OnPreSolve();
    systems.Solve();
    foreach (var cmp in AllComponents())
      cmp.OnPostSolve();
  }

  public void UpdatePartTree(Dictionary<uint, uint?> parentMap) {
    partTree = parentMap;
  }

  /// <summary>
  /// Create an independent deep copy of this vessel. Components are cloned
  /// via their Clone() methods. The clone has its own systems.
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
