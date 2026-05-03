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

  // Coupled-input consumer registered with the staging system. Single
  // input today (Hydrazine), but the same pattern handles future
  // bipropellant RCS modules cleanly.
  internal StagingFlowSystem.Consumer consumer;

  // Fraction of requested rate actually delivered (1.0 = full supply).
  public double Satisfaction => Throttle > 1e-12 ? (consumer?.Activity ?? 0) / Throttle : 0;

  // Effective throttle achieved this tick.
  public double NormalizedOutput => consumer?.Activity ?? 0;

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

    consumer = systems.Staging.RegisterConsumer(node);
    foreach (var prop in Propellants)
      consumer.AddInput(prop.Resource, prop.MaxFlow);
  }

  public override void OnPreSolve() {
    if (consumer != null) consumer.Throttle = Throttle;
  }
}
