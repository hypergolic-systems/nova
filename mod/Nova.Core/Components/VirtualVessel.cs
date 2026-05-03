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

  // Hysteresis bands for fuel cell auto-on/auto-off. Centralised here so
  // the same numbers drive both the on/off transition and the
  // valid-until forecast — there's exactly one place to update if the
  // bands ever need tuning.
  private const double FuelCellOnSocThreshold  = 0.20;
  private const double FuelCellOffSocThreshold = 0.80;

  // Manifold refill hysteresis. The refill demands pull reactant from
  // main tanks at envelope-friendly rates (~0.1466 mix-L/s) and top up
  // the component-internal manifold; production drains the manifold
  // off-LP at the µL/s reactant rate. Refill kicks on when the manifold
  // dips below 10% and turns off at 100%.
  private const double FuelCellRefillOnThreshold  = 0.10;
  private const double FuelCellRefillOffThreshold = 1.00;

  // Pre-solve: aggregate vessel-wide battery SoC and apply hysteresis
  // to each FuelCell's IsActive (production) and RefillActive (manifold
  // top-up) flags. Sets Demand on the production device + refill demand
  // rates (via FuelCell.OnPreSolve) for the upcoming solve.
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
      if (fc.production == null) continue;

      // Production hysteresis (SoC band).
      if (noBatteries) {
        fc.IsActive = true;
      } else if (fc.IsActive && soc > FuelCellOffSocThreshold) {
        fc.IsActive = false;
      } else if (!fc.IsActive && soc < FuelCellOnSocThreshold) {
        fc.IsActive = true;
      }

      // Refill hysteresis. Single mix-manifold; one fraction reading.
      double frac = fc.Manifold.FillFraction;
      if (fc.RefillActive && frac >= FuelCellRefillOffThreshold) {
        fc.RefillActive = false;
      } else if (!fc.RefillActive && frac < FuelCellRefillOnThreshold) {
        fc.RefillActive = true;
      }

      fc.production.Demand = (fc.IsActive && !fc.Manifold.IsEmpty) ? 1.0 : 0.0;
      // Refill demand rates set by FuelCell.OnPreSolve based on RefillActive.
    }
  }

  // Post-solve: compute valid-until forecasts for each fuel cell using
  // the *converged* Activities. Production-side ValidUntil lives on the
  // Process device; refill-side lives on the component (no per-demand
  // ValidUntil — it's a coupled-pair forecast).
  //
  // Production flips on:
  //   • SoC reaches a hysteresis threshold
  //   • manifold runs dry (production was active, can't continue)
  //
  // Refill flips on:
  //   • manifold drops below 10% (turn ON)
  //   • manifold reaches 100% (turn OFF)
  private void DistributeFuelCellState() {
    var fuelCells = AllComponents().OfType<FuelCell>().ToList();
    if (fuelCells.Count == 0) return;

    double contents = 0, capacity = 0, batteryRate = 0;
    foreach (var b in AllComponents().OfType<Battery>()) {
      contents += b.Buffer.Contents;
      capacity += b.Buffer.Capacity;
      batteryRate += b.Buffer.Rate;  // signed: + = charging, − = draining
    }
    bool noBatteries = capacity < 1e-9;

    foreach (var fc in fuelCells) {
      if (fc.production == null) continue;

      // -------- Production ValidUntil --------

      // SoC threshold flip.
      double dtSocFlip = double.PositiveInfinity;
      if (!noBatteries && Math.Abs(batteryRate) > 1e-9) {
        if (fc.IsActive && batteryRate > 0) {
          double remaining = FuelCellOffSocThreshold * capacity - contents;
          if (remaining > 0) dtSocFlip = remaining / batteryRate;
        } else if (!fc.IsActive && batteryRate < 0) {
          double remaining = contents - FuelCellOnSocThreshold * capacity;
          if (remaining > 0) dtSocFlip = remaining / (-batteryRate);
        }
      }

      // Manifold-empty time (production gated off when fuel runs out).
      // Net drain rate in mix-L/s; refill is so much faster than
      // production reactant draw (~200×) that this only matters when
      // the main tank is empty and refill is forced to 0.
      double dtMfdEmpty = double.PositiveInfinity;
      if (fc.production.Activity > 1e-9) {
        double netDrain = fc.production.Activity * fc.ProductionDrainRate
                        - fc.RefillActivity      * fc.RefillRate;
        if (netDrain > 1e-12 && fc.Manifold.Contents > 0)
          dtMfdEmpty = fc.Manifold.Contents / netDrain;
      }

      double dtProdFlip = Math.Min(dtSocFlip, dtMfdEmpty);
      fc.ValidUntilSeconds = dtProdFlip;
      fc.production.ValidUntil = double.IsPositiveInfinity(dtProdFlip)
        ? double.PositiveInfinity
        : simulationTime + dtProdFlip;

      // -------- Refill ValidUntil → on FuelCell.ValidUntil --------

      double netFill = fc.RefillActivity      * fc.RefillRate
                     - fc.production.Activity * fc.ProductionDrainRate;
      double dtRefillFlip = fc.RefillActive
        ? fc.Manifold.TimeToFraction(FuelCellRefillOffThreshold, netFill)
        : fc.Manifold.TimeToFraction(FuelCellRefillOnThreshold, netFill);

      // Single component-level ValidUntil for the runner; min of the
      // two flip times so whichever comes first triggers the resolve.
      double dtCmp = Math.Min(dtProdFlip, dtRefillFlip);
      fc.ValidUntil = double.IsPositiveInfinity(dtCmp)
        ? double.PositiveInfinity
        : simulationTime + dtCmp;
    }
  }

  // Drain/fill manifold contents using the previous solve's Activities.
  // Same staleness contract as Buffer.Rate-based integration: rates
  // come from the prior Solve, integrate over `deltaT`, next Solve
  // reads the new state. Accumulator.Integrate clamps at [0, capacity]
  // so an over-shoot between solves doesn't push the manifold negative —
  // the production ValidUntil set in DistributeFuelCellState ensures we
  // re-solve before the over-shoot grows large.
  private void IntegrateFuelCellManifolds(double deltaT) {
    if (deltaT <= 0) return;
    foreach (var fc in AllComponents().OfType<FuelCell>()) {
      if (fc.production == null) continue;
      double netRate = fc.RefillActivity      * fc.RefillRate
                     - fc.production.Activity * fc.ProductionDrainRate;
      fc.Manifold.Integrate(netRate, deltaT);
    }
  }

  // Drain/fill reaction-wheel accumulators off-system. Drain comes from
  // live intensity (sum of |throttles| set this frame by SolveAttitude);
  // refill comes from the LP-solved refill device's previous Activity.
  // The hysteresis flip itself happens in ReactionWheel.OnPreSolve;
  // this method just integrates the buffer and refreshes the per-wheel
  // ValidUntil forecast so the Tick scheduler steps to the right next
  // event.
  private void IntegrateReactionWheelBuffers(double deltaT) {
    if (deltaT <= 0) return;

    foreach (var w in AllComponents().OfType<ReactionWheel>()) {
      if (w.refill == null || w.Buffer == null) continue;

      double intensity = Math.Abs(w.ThrottlePitch)
                       + Math.Abs(w.ThrottleRoll)
                       + Math.Abs(w.ThrottleYaw);
      double desiredDrain = intensity * w.ElectricRate;
      double fillRate     = w.refill.Activity * w.RefillRateWatts;
      // Available power this tick = whatever's in the buffer plus this
      // tick's worth of LP-supplied refill. Drain can't exceed it.
      double available    = w.Buffer.Contents / deltaT + fillRate;
      double effective    = Math.Min(desiredDrain, available);
      double net          = fillRate - effective;

      w.Buffer.Integrate(net, deltaT);
      w.Satisfaction = desiredDrain > 1e-9 ? effective / desiredDrain : 1.0;
      w.CurrentDrain = effective;
      w.CurrentRefill = fillRate;

      UpdateReactionWheelForecast(w);
    }
  }

  // Refresh refill.ValidUntil for each wheel based on current
  // intensity + buffer state + last-solve refill.Activity. Called at
  // three hooks where any of those inputs may have just changed:
  //   1. Top of Tick — SolveAttitude may have changed intensity since
  //      the last solve.
  //   2. After IntegrateReactionWheelBuffers — buffer state advances.
  //   3. End of DoSolve — refill.Activity changes when the LP solves
  //      with a flipped Demand.
  private void UpdateReactionWheelForecasts() {
    foreach (var w in AllComponents().OfType<ReactionWheel>())
      UpdateReactionWheelForecast(w);
  }

  private void UpdateReactionWheelForecast(ReactionWheel w) {
    if (w.refill == null || w.Buffer == null) return;

    double intensity = Math.Abs(w.ThrottlePitch)
                     + Math.Abs(w.ThrottleRoll)
                     + Math.Abs(w.ThrottleYaw);
    double drain = intensity * w.ElectricRate;
    double fill  = w.refill.Activity * w.RefillRateWatts;
    double net   = fill - drain;

    // Next flip is at the OPPOSITE threshold from the current state.
    double dt = w.RefillActive
      ? w.Buffer.TimeToFraction(ReactionWheel.RefillOffFraction, net)
      : w.Buffer.TimeToFraction(ReactionWheel.RefillOnFraction,  net);

    w.refill.ValidUntil = double.IsPositiveInfinity(dt)
      ? double.PositiveInfinity
      : simulationTime + dt;
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
      UpdateFuelCellDevices();
      foreach (var component in AllComponents())
        component.OnPreSolve();

      systems.Solve();

      DistributeSolarPanelCurrentRates();
      DistributeFuelCellState();
      // Wheel forecasts depend on the freshly-solved refill.Activity —
      // refresh after Solve so nextExpiry reflects the new state.
      UpdateReactionWheelForecasts();
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

    // SolveAttitude (NovaVesselModule.FixedUpdate, runs before this
    // Tick) may have changed wheel intensity since the last solve —
    // refresh the per-wheel forecasts so the cached `nextExpiry`
    // reflects current drain/fill rates before the loop consumes it.
    UpdateReactionWheelForecasts();

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
        IntegrateFuelCellManifolds(dt);
        IntegrateReactionWheelBuffers(dt);  // refreshes per-wheel ValidUntil
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
  /// Run OnPreSolve on all components and solve the resource systems.
  /// Used by DeltaVSimulation on cloned vessels.
  /// </summary>
  public void Solve() {
    foreach (var cmp in AllComponents())
      cmp.OnPreSolve();
    systems.Solve();
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
