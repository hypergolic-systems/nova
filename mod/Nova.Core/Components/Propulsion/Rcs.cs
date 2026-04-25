using System.Collections.Generic;
using System.Linq;
using Nova.Core.Resources;
using Nova.Core.Flight;
using Nova.Core.Utils;

namespace Nova.Core.Components.Propulsion;

public class Rcs : VirtualComponent {
  public double ThrusterPower; // kN per nozzle (vacuum)
  public double Isp; // s (vacuum)
  public int ThrusterCount; // set by KSP module after counting transforms
  public double Throttle; // 0-1, aggregate from RCS solver

  private ResourceSolver.Device device;

  public double Satisfaction => device != null ? device.Satisfaction : 0;
  public double NormalizedOutput => device != null ? device.Activity : 0;

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

  public override void OnBuildSolver(ResourceSolver solver, ResourceSolver.Node node) {
    if (ThrusterCount > 0 && Isp > 0) {
      var maxThrust = ThrusterPower * ThrusterCount;
      var massFlow = maxThrust * 1000 / (Isp * G0);
      var batchMass = Propellants.Sum(p => p.Ratio * p.Resource.Density);
      if (batchMass > 0) {
        var maxBatchRate = massFlow / batchMass;
        foreach (var prop in Propellants)
          prop.MaxFlow = maxBatchRate * prop.Ratio;
      }
    }

    device = node.AddDevice(ResourceSolver.Priority.Low);
    foreach (var prop in Propellants)
      device.AddInput(prop.Resource, prop.MaxFlow);
  }

  public override void OnPreSolve() {
    if (device != null)
      device.Demand = Throttle;
  }}
