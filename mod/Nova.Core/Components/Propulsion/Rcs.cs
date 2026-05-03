using System.Collections.Generic;
using System.Linq;
using Nova.Core.Resources;
using Nova.Core.Systems;

namespace Nova.Core.Components.Propulsion;

public class Rcs : VirtualComponent {
  public double ThrusterPower; // kN per nozzle (vacuum)
  public double Isp; // s (vacuum)
  public int ThrusterCount; // set by KSP module after counting transforms
  public double Throttle; // 0-1, aggregate from RCS solver

  // Per-propellant staging demands. Order matches Propellants.
  private List<StagingFlowSystem.Demand> demands;

  internal IReadOnlyList<StagingFlowSystem.Demand> Demands => demands;

  internal bool IsStarved {
    get {
      if (demands == null || Throttle <= 1e-12) return false;
      foreach (var d in demands) {
        if (d.Node.Jettisoned) continue;
        if (d.Rate > 1e-12 && d.Satisfied < d.Rate - 1e-9) return true;
      }
      return false;
    }
  }

  internal void ZeroDemands() {
    if (demands == null) return;
    foreach (var d in demands) d.Rate = 0;
  }

  // Min activity across propellants — fraction of requested rate
  // delivered. Maps to the old `device.Satisfaction`.
  public double Satisfaction {
    get {
      if (demands == null || demands.Count == 0) return 0;
      double minAct = 1.0;
      foreach (var d in demands) if (d.Activity < minAct) minAct = d.Activity;
      return minAct;
    }
  }

  // Effective throttle achieved this tick (Throttle × Satisfaction).
  public double NormalizedOutput => Throttle * Satisfaction;

  public class Propellant {
    public Resource Resource;
    public double Ratio;
    public double MaxFlow; // max volumetric flow (all thrusters, full throttle)
  }

  public List<Propellant> Propellants = new();

  private const double G0 = 9.80665;

  public void Initialize(double thrusterPower, double isp,
      List<(Resource resource, double ratio)> propellants) {
    ThrusterPower = thrusterPower;
    Isp = isp;

    Propellants.Clear();
    foreach (var (resource, ratio) in propellants) {
      Propellants.Add(new Propellant {
        Resource = resource,
        Ratio = ratio,
      });
    }
  }

  public override VirtualComponent Clone() {
    var clone = new Rcs {
      ThrusterPower = ThrusterPower,
      Isp = Isp,
      ThrusterCount = ThrusterCount,
      Throttle = Throttle,
    };
    clone.Propellants = Propellants.Select(p => new Propellant {
      Resource = p.Resource,
      Ratio = p.Ratio,
      MaxFlow = p.MaxFlow,
    }).ToList();
    return clone;
  }

  public override void OnBuildSystems(VesselSystems systems, StagingFlowSystem.Node node) {
    if (ThrusterCount > 0 && Isp > 0) {
      var maxThrust = ThrusterPower * ThrusterCount;
      var totalMassFlow = maxThrust * 1000 / (Isp * G0);
      var batchMass = Propellants.Sum(p => p.Ratio * p.Resource.Density);
      if (batchMass > 0) {
        var maxBatchRate = totalMassFlow / batchMass;
        foreach (var prop in Propellants)
          prop.MaxFlow = maxBatchRate * prop.Ratio;
      }
    }

    demands = new List<StagingFlowSystem.Demand>(Propellants.Count);
    foreach (var prop in Propellants)
      demands.Add(systems.Staging.RegisterDemand(node, prop.Resource));
  }

  public override void OnPreSolve() {
    if (demands == null) return;
    for (int i = 0; i < demands.Count; i++)
      demands[i].Rate = Throttle * Propellants[i].MaxFlow;
  }
}
