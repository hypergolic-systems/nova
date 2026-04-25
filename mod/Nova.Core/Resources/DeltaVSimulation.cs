using System;
using System.Collections.Generic;
using System.Linq;
using Nova.Core.Components;
using Nova.Core.Components.Propulsion;

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
    var solver = sim.Solver;
    var results = new List<StageResult>();

    var propellantResources = new HashSet<Resource>();
    foreach (var engine in sim.AllComponents().OfType<Engine>())
      foreach (var prop in engine.Propellants)
        propellantResources.Add(prop.Resource);

    for (int i = 0; i < stages.Count; i++) {
      var stageDef = stages[i];

      // Jettison this stage's decoupler tiers.
      foreach (var partId in stageDef.DecouplerPartIds) {
        var node = solver.AllNodes().FirstOrDefault(n => n.id == (long)partId);
        if (node != null)
          foreach (var n in node.AllNodes())
            n.Jettisoned = true;
      }

      // Activate engines in this stage.
      foreach (var enginePartId in stageDef.EnginePartIds) {
        foreach (var cmp in sim.GetComponents(enginePartId).OfType<Engine>())
          cmp.Throttle = 1.0;
      }

      // Trigger: next stage's decoupler tiers are spent.
      var triggerNodes = new HashSet<ResourceSolver.Node>();
      var nextDecouplerStage = stages.Skip(i + 1).FirstOrDefault(s => s.DecouplerPartIds.Count > 0);
      if (nextDecouplerStage != null) {
        foreach (var partId in nextDecouplerStage.DecouplerPartIds) {
          var node = solver.AllNodes().FirstOrDefault(n => n.id == (long)partId);
          if (node != null)
            foreach (var n in node.AllNodes().Where(x => !x.Jettisoned))
              triggerNodes.Add(n);
        }
      } else {
        triggerNodes = new HashSet<ResourceSolver.Node>(solver.ActiveNodes());
      }

      var stageResult = Burn(sim, solver, propellantResources, triggerNodes);
      if (stageResult != null) {
        stageResult.InverseStageIndex = stageDef.InverseStageIndex;
        results.Add(stageResult);
      }
    }

    return results;
  }

  private static StageResult Burn(
      VirtualVessel sim,
      ResourceSolver solver,
      HashSet<Resource> propellantResources,
      HashSet<ResourceSolver.Node> triggerNodes) {

    double stageDeltaV = 0, stageBurnTime = 0;
    double stageStartMass = solver.ActiveNodes().Sum(n => n.Mass());
    double lastThrust = 0, lastMassFlow = 0;
    int iterations = 0;

    while (iterations++ < MaxIterations) {
      sim.Solve();

      if (AllTiersSpent(triggerNodes, propellantResources))
        break;

      var (thrust, massFlow) = ComputeThrustAndFlow(sim);
      if (thrust > Epsilon) { lastThrust = thrust; lastMassFlow = massFlow; }

      var dt = solver.ActiveNodes().Min(n => n.TimeToNextExpiry());
      if (dt <= 0 || double.IsPositiveInfinity(dt)) break;

      var massStart = solver.ActiveNodes().Sum(n => n.Mass());
      foreach (var node in solver.ActiveNodes())
        node.IntegrateBuffers(dt);
      var massEnd = solver.ActiveNodes().Sum(n => n.Mass());

      if (massEnd > Epsilon && massStart > massEnd) {
        var ispEff = thrust * 1000 / (G0 * massFlow);
        stageDeltaV += ispEff * G0 * Math.Log(massStart / massEnd);
      }
      stageBurnTime += dt;
    }

    if (stageDeltaV > Epsilon) {
      return new StageResult {
        DeltaV = stageDeltaV,
        StartMass = stageStartMass,
        EndMass = solver.ActiveNodes().Sum(n => n.Mass()),
        Thrust = lastThrust,
        Isp = lastMassFlow > Epsilon ? lastThrust * 1000 / (G0 * lastMassFlow) : 0,
        BurnTime = stageBurnTime,
      };
    }
    return null;
  }

  private static bool AllTiersSpent(
      HashSet<ResourceSolver.Node> tierNodes,
      HashSet<Resource> propellantResources) {

    foreach (var node in tierNodes) {
      if (node.Jettisoned) continue;

      // Check if any device on this node has activity.
      foreach (var device in node.Devices) {
        if (device.Activity > Epsilon)
          return false;
      }

      // Check if any propellant buffer is flowing.
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
