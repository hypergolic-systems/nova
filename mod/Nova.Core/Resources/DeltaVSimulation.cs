using System;
using System.Collections.Generic;
using System.Linq;
using Nova.Core.Components;
using Nova.Core.Components.Propulsion;
using Nova.Core.Systems;

namespace Nova.Core.Resources;

public class DeltaVSimulation {

  public class StageDefinition {
    public int InverseStageIndex;
    public List<uint> EnginePartIds = new();
    public List<uint> DecouplerPartIds = new();
  }

  public class StageResult {
    public int InverseStageIndex;
    public double DeltaV;    // m/s
    public double StartMass; // kg
    public double EndMass;   // kg
    public double Thrust;    // kN
    public double Isp;       // s (effective)
    public double BurnTime;  // s
  }

  private const double G0 = 9.80665;
  private const int MaxIterations = 10000;
  private const double Epsilon = 1e-9;

  public static List<StageResult> Run(VirtualVessel vessel, List<StageDefinition> stages, double time = 0) {
    var sim = vessel.Clone(time);
    return RunInternal(sim, stages);
  }

  private static List<StageResult> RunInternal(VirtualVessel sim, List<StageDefinition> stages) {
    var staging = sim.Systems.Staging;
    var results = new List<StageResult>();

    var propellantResources = new HashSet<Resource>();
    foreach (var engine in sim.AllComponents().OfType<Engine>())
      foreach (var prop in engine.Propellants)
        propellantResources.Add(prop.Resource);

    for (int i = 0; i < stages.Count; i++) {
      var stageDef = stages[i];

      // Jettison this stage's decoupler tiers.
      foreach (var partId in stageDef.DecouplerPartIds) {
        var node = staging.Nodes.FirstOrDefault(n => n.Id == (long)partId);
        if (node != null)
          foreach (var n in node.AllSubtreeNodes())
            n.Jettisoned = true;
      }

      // Activate engines in this stage.
      foreach (var enginePartId in stageDef.EnginePartIds) {
        foreach (var cmp in sim.GetComponents(enginePartId).OfType<Engine>())
          cmp.Throttle = 1.0;
      }

      // Trigger: next stage's decoupler tiers are spent.
      var triggerNodes = new HashSet<StagingFlowSystem.Node>();
      var nextDecouplerStage = stages.Skip(i + 1).FirstOrDefault(s => s.DecouplerPartIds.Count > 0);
      if (nextDecouplerStage != null) {
        foreach (var partId in nextDecouplerStage.DecouplerPartIds) {
          var node = staging.Nodes.FirstOrDefault(n => n.Id == (long)partId);
          if (node != null)
            foreach (var n in node.AllSubtreeNodes().Where(x => !x.Jettisoned))
              triggerNodes.Add(n);
        }
      } else {
        triggerNodes = new HashSet<StagingFlowSystem.Node>(staging.ActiveNodes());
      }

      var stageResult = Burn(sim, staging, propellantResources, triggerNodes);
      if (stageResult != null) {
        stageResult.InverseStageIndex = stageDef.InverseStageIndex;
        results.Add(stageResult);
      }
    }

    return results;
  }

  private static StageResult Burn(
      VirtualVessel sim,
      StagingFlowSystem staging,
      HashSet<Resource> propellantResources,
      HashSet<StagingFlowSystem.Node> triggerNodes) {

    double stageDeltaV = 0, stageBurnTime = 0;
    double stageStartMass = staging.ActiveNodes().Sum(n => n.Mass());
    double lastThrust = 0, lastMassFlow = 0;
    int iterations = 0;

    while (iterations++ < MaxIterations) {
      SolveWithStarvationIteration(sim);

      if (AllTiersSpent(triggerNodes, propellantResources)) break;

      var (thrust, massFlow) = ComputeThrustAndFlow(sim);
      if (thrust > Epsilon) { lastThrust = thrust; lastMassFlow = massFlow; }

      // No engine producing thrust → no more dV is reachable. The
      // starvation iteration above zeros demands for engines that
      // can't fully fire, so this only catches the degenerate case
      // where the stage genuinely has no thrust source (no engines
      // activated, all engines starved, etc.).
      if (thrust <= Epsilon) break;

      // dt = horizon to the next staging-buffer event (a tank empties
      // or fills). Bounds the "rates valid for" window.
      var dt = staging.MaxTickDt();
      if (dt <= 0 || double.IsPositiveInfinity(dt)) break;

      var massStart = staging.ActiveNodes().Sum(n => n.Mass());
      // Advance the cloned vessel's clock — buffer Contents lerps
      // forward against the new UT. No per-buffer mutation needed.
      sim.Systems.Clock.UT += dt;
      var massEnd = staging.ActiveNodes().Sum(n => n.Mass());

      // Belt-and-suspenders against div-by-zero: skip the dV term if
      // thrust and massFlow somehow leak through as zero with a
      // non-trivial mass change. Shouldn't happen given the thrust
      // break above, but cheap to guard.
      if (massEnd > Epsilon && massStart > massEnd && massFlow > Epsilon) {
        var ispEff = thrust * 1000 / (G0 * massFlow);
        stageDeltaV += ispEff * G0 * Math.Log(massStart / massEnd);
      }
      stageBurnTime += dt;
    }

    if (stageDeltaV > Epsilon) {
      return new StageResult {
        DeltaV = stageDeltaV,
        StartMass = stageStartMass,
        EndMass = staging.ActiveNodes().Sum(n => n.Mass()),
        Thrust = lastThrust,
        Isp = lastMassFlow > Epsilon ? lastThrust * 1000 / (G0 * lastMassFlow) : 0,
        BurnTime = stageBurnTime,
      };
    }
    return null;
  }

  // Maximum starvation re-solve passes per dV-burn iteration. Each
  // pass either zeros at least one consumer or terminates, so the
  // bound is O(consumers) but 8 covers any realistic vessel.
  private const int MaxStarvationIters = 8;

  // Solve the vessel and iteratively zero the staging demands of any
  // Engine / Rcs whose coupled inputs weren't fully satisfied. Rerun
  // staging until stable. Without this, a partially-starved
  // coupled-input engine would let the unstuck propellants over-drain
  // their tanks (per-resource staging demands are independent), which
  // both wastes fuel in the simulation and can credit phantom dV via
  // the mass-change calc.
  //
  // Lives in DeltaVSimulation rather than VirtualVessel so the dV
  // simulator owns its own solver-iteration policy. The live in-flight
  // path doesn't need this — engine flameout naturally drives Throttle
  // to 0 within one frame, so over-drain is bounded by a single
  // physics tick.
  private static void SolveWithStarvationIteration(VirtualVessel sim) {
    sim.Solve();
    for (int iter = 0; iter < MaxStarvationIters; iter++) {
      bool anyZeroed = false;
      foreach (var engine in sim.AllComponents().OfType<Engine>()) {
        if (engine.IsStarved) {
          engine.ZeroDemands();
          anyZeroed = true;
        }
      }
      foreach (var rcs in sim.AllComponents().OfType<Rcs>()) {
        if (rcs.IsStarved) {
          rcs.ZeroDemands();
          anyZeroed = true;
        }
      }
      if (!anyZeroed) return;
      sim.Systems.Staging.Solve();
    }
  }

  private static bool AllTiersSpent(
      HashSet<StagingFlowSystem.Node> tierNodes,
      HashSet<Resource> propellantResources) {

    // "Spent" = no propellant buffer on any tier node is currently
    // flowing. Engines that can't be supplied report Activity = 0 and
    // their buffer rates collapse to zero too — captured by the same
    // check.
    foreach (var node in tierNodes) {
      if (node.Jettisoned) continue;
      foreach (var buffer in node.Buffers) {
        if (!propellantResources.Contains(buffer.Resource)) continue;
        if (Math.Abs(buffer.Rate) > Epsilon)
          return false;
      }
    }
    return true;
  }

  private static (double thrust, double massFlow) ComputeThrustAndFlow(VirtualVessel sim) {
    double thrust = 0, massFlow = 0;
    foreach (var engine in sim.AllComponents().OfType<Engine>()) {
      var output = engine.NormalizedOutput;
      if (output > Epsilon) {
        thrust += engine.Thrust * output;
        massFlow += engine.Thrust * 1000 * output / (engine.Isp * G0);
      }
    }
    return (thrust, massFlow);
  }
}
