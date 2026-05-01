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

  // Monotonic count of LP solves. Useful for tests asserting that a
  // particular event (e.g. wheel-buffer crossing) DID or DIDN'T trigger
  // a re-solve, and for production debug logs that want to observe
  // solve cadence without instrumenting DoSolve directly.
  public int SolveCount { get; private set; }

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

  // Current environment. Populated by the mod-side NovaVesselModule
  // ahead of each Tick. For unloaded vessels these are stable (the
  // body and situation don't change while parked). Read by science
  // components for slice / subject computation.
  public string BodyName;
  public uint BodyId;
  public Nova.Core.Science.Situation Situation;
  public double BodyYearSeconds;
  public double Altitude;

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
    foreach (var cmp in components) cmp.Vessel = this;
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
      foreach (var cmp in partEntry.components) cmp.Vessel = this;
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

  // Manifold refill hysteresis. The refill device pulls reactant from
  // main tanks at envelope-friendly rates (~0.1 L/s) and tops up the
  // component-internal manifold; production drains the manifold off-LP
  // at the µL/s reactant rate. Refill kicks on when either manifold
  // dips below 10% of capacity and turns off when both reach 100%.
  private const double FuelCellRefillOnThreshold  = 0.10;
  private const double FuelCellRefillOffThreshold = 1.00;

  // Pre-solve: aggregate vessel-wide battery SoC and apply hysteresis
  // to each FuelCell's IsActive (production) and RefillActive (manifold
  // top-up) flags. Sets Demand on both devices for the upcoming solve.
  // The valid-until forecasts are intentionally NOT computed here —
  // Activity at this point is from the previous solve, and the demand
  // changes we just made will shift things the moment Solve runs. The
  // post-solve sibling DistributeFuelCellState reads the freshly
  // converged Activities instead.
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
      if (fc.production == null || fc.refill == null) continue;

      // Production hysteresis (SoC band).
      if (noBatteries) {
        fc.IsActive = true;
      } else if (fc.IsActive && soc > FuelCellOffSocThreshold) {
        fc.IsActive = false;
      } else if (!fc.IsActive && soc < FuelCellOnSocThreshold) {
        fc.IsActive = true;
      }

      // Refill hysteresis (manifold fill band). Trip on the lower
      // manifold; turn off only when both reach capacity, so a single
      // device with a ratio-locked LH₂/LOx draw fills both in lockstep.
      double minFrac = Math.Min(fc.Lh2Manifold.FillFraction, fc.LoxManifold.FillFraction);
      if (fc.RefillActive && minFrac >= FuelCellRefillOffThreshold) {
        fc.RefillActive = false;
      } else if (!fc.RefillActive && minFrac < FuelCellRefillOnThreshold) {
        fc.RefillActive = true;
      }

      bool hasFuel = !fc.Lh2Manifold.IsEmpty && !fc.LoxManifold.IsEmpty;
      fc.production.Demand = (fc.IsActive && hasFuel) ? 1.0 : 0.0;
      fc.refill.Demand     = fc.RefillActive ? 1.0 : 0.0;
    }
  }

  // Post-solve: compute valid-until forecasts for each fuel cell using
  // the *converged* Activities. Setting Device.ValidUntil tells
  // ComputeNextExpiry when the LP needs re-evaluation.
  //
  // Production flips on:
  //   • SoC reaches a hysteresis threshold
  //   • manifold runs dry (production was active, can't continue)
  //
  // Refill flips on:
  //   • either manifold drops below 10% (turn ON)
  //   • both manifolds reach capacity (turn OFF)
  //
  // ValidUntilSeconds is the production-side forecast — the user-facing
  // "when will the on/off badge change". Refill cycles are an internal
  // implementation detail and don't surface in the dashboard number.
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
      if (fc.production == null || fc.refill == null) continue;

      // -------- Production ValidUntil --------

      // SoC threshold flip (existing logic).
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
      // Net drain rate accounts for parallel refill — refill is so much
      // faster than reactant draw (200×) that this term only matters
      // when the main tank is empty and refill.Activity is forced to 0.
      double dtMfdEmpty = double.PositiveInfinity;
      if (fc.production.Activity > 1e-9) {
        double netDrainLh2 = fc.production.Activity * fc.Lh2Rate
                           - fc.refill.Activity     * fc.RefillRateLh2;
        double netDrainLox = fc.production.Activity * fc.LoxRate
                           - fc.refill.Activity     * fc.RefillRateLh2 * (fc.LoxRate / fc.Lh2Rate);
        if (netDrainLh2 > 1e-12 && fc.Lh2Manifold.Contents > 0)
          dtMfdEmpty = Math.Min(dtMfdEmpty, fc.Lh2Manifold.Contents / netDrainLh2);
        if (netDrainLox > 1e-12 && fc.LoxManifold.Contents > 0)
          dtMfdEmpty = Math.Min(dtMfdEmpty, fc.LoxManifold.Contents / netDrainLox);
      }

      double dtProdFlip = Math.Min(dtSocFlip, dtMfdEmpty);
      fc.ValidUntilSeconds = dtProdFlip;
      fc.production.ValidUntil = double.IsPositiveInfinity(dtProdFlip)
        ? double.PositiveInfinity
        : simulationTime + dtProdFlip;

      // -------- Refill ValidUntil --------

      double netFillLh2 = fc.refill.Activity     * fc.RefillRateLh2
                        - fc.production.Activity * fc.Lh2Rate;
      double netFillLox = fc.refill.Activity     * fc.RefillRateLh2 * (fc.LoxRate / fc.Lh2Rate)
                        - fc.production.Activity * fc.LoxRate;

      double dtRefillFlip = double.PositiveInfinity;
      if (fc.RefillActive) {
        // Flip OFF when *both* manifolds reach 100% — wait for the slower.
        double dtLh2 = fc.Lh2Manifold.TimeToFraction(FuelCellRefillOffThreshold, netFillLh2);
        double dtLox = fc.LoxManifold.TimeToFraction(FuelCellRefillOffThreshold, netFillLox);
        dtRefillFlip = Math.Max(dtLh2, dtLox);
      } else {
        // Flip ON when *either* drops below 10% — earliest wins.
        double dtLh2 = fc.Lh2Manifold.TimeToFraction(FuelCellRefillOnThreshold, netFillLh2);
        double dtLox = fc.LoxManifold.TimeToFraction(FuelCellRefillOnThreshold, netFillLox);
        dtRefillFlip = Math.Min(dtLh2, dtLox);
      }
      fc.refill.ValidUntil = double.IsPositiveInfinity(dtRefillFlip)
        ? double.PositiveInfinity
        : simulationTime + dtRefillFlip;
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
      if (fc.production == null || fc.refill == null) continue;
      double drainLh2 = fc.production.Activity * fc.Lh2Rate;
      double drainLox = fc.production.Activity * fc.LoxRate;
      double fillLh2  = fc.refill.Activity     * fc.RefillRateLh2;
      double fillLox  = fc.refill.Activity     * fc.RefillRateLh2 * (fc.LoxRate / fc.Lh2Rate);

      fc.Lh2Manifold.Integrate(fillLh2 - drainLh2, deltaT);
      fc.LoxManifold.Integrate(fillLox - drainLox, deltaT);
    }
  }

  // Drain/fill reaction-wheel accumulators off-LP. Drain comes from
  // live intensity (sum of |throttles| set this frame by SolveAttitude);
  // refill comes from the LP-solved refill device's previous Activity.
  // The hysteresis flip itself happens in ReactionWheel.OnPreSolve
  // (mirrors UpdateFuelCellDevices); this method just integrates the
  // buffer and refreshes the per-wheel ValidUntil forecast so the Tick
  // scheduler steps to the right next event.
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
  // Together these guarantee `nextExpiry` reflects the true time of
  // the next hysteresis flip, so the Tick loop can step there directly
  // (matters for long-interval catch-up; loaded vessels at 50 Hz would
  // be fine without it, but the refresh is cheap).
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
  // `nextStop - simulationTime` is then 0, no integration runs, the
  // OnPreSolve check sees a frac that's still strictly below threshold,
  // and the loop spins on the same time point until MaxTickIterations
  // forces it through. 1 µs is well above representable-time precision
  // even at multi-year simTime, and small enough to be physically
  // unobservable. The integration over MinTickStep also overshoots any
  // sub-precision gap to the threshold (rate × 1 µs ≫ ulp), so the
  // accumulator clamps to capacity and the next solve flips cleanly.
  private const double MinTickStep = 1e-6;

  private void DoSolve() {
    SolveCount++;
    try {
      UpdateShadowState();
      UpdateSolarDeviceDemand();
      UpdateFuelCellDevices();
      foreach (var component in AllComponents())
        component.OnPreSolve();
      solver.Solve();
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
    if (solver == null) return;

    // SolveAttitude (NovaVesselModule.FixedUpdate, runs before this
    // Tick) may have changed wheel intensity since the last solve —
    // refresh the per-wheel forecasts so the cached `nextExpiry`
    // reflects current drain/fill rates before the loop consumes it.
    UpdateReactionWheelForecasts();
    nextExpiry = ComputeNextExpiry(simulationTime);

    var iterations = 0;

    while (simulationTime < targetTime) {
      if (++iterations > MaxTickIterations) {
        Log?.Invoke($"Tick() exceeded {MaxTickIterations} iterations, forcing advance. simTime={simulationTime} target={targetTime} nextExpiry={nextExpiry}");
        simulationTime = targetTime;
        break;
      }

      // Floor the step at MinTickStep ahead of simulationTime — a
      // ValidUntil within FP precision of `now` would otherwise land
      // nextStop == simulationTime and stall. Capped by targetTime so
      // we never overshoot the requested target.
      var nextStop = Math.Min(targetTime,
          Math.Max(nextExpiry, simulationTime + MinTickStep));

      var dt = nextStop - simulationTime;
      if (dt > 0) {
        IntegrateBuffers(dt);
        IntegrateFuelCellManifolds(dt);
        IntegrateReactionWheelBuffers(dt);  // refreshes per-wheel ValidUntil
        simulationTime = nextStop;
        // Don't recompute nextExpiry here — keep the cached value so
        // the expiry check below can fire for the event we just hit.
        // The post-DoSolve refresh + Tick-start refresh keep the cache
        // current at the actual decision points.
      }

      // Fire any component whose ValidUntil has elapsed (slice rollover,
      // scheduled emissions). Component-driven Demand changes propagate
      // via the needsSolve flag — DoSolve below picks them up.
      if (FireComponentUpdates(simulationTime))
        needsSolve = true;

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
    // Component-level expiries (e.g. science slice rollovers). Default
    // VirtualComponent.ValidUntil is +Infinity, so any non-event-driven
    // component contributes nothing to this min.
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
