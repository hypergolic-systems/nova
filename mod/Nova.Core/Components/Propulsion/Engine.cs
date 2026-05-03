using System;
using System.Collections.Generic;
using System.Linq;
using Nova.Core.Resources;
using Nova.Core.Flight;
using Nova.Core.Utils;

namespace Nova.Core.Components.Propulsion;

public class Engine : VirtualComponent {
  public double Thrust; // kN (vacuum) — also serves as MaxThrust on the wire
  public double Isp; // s (vacuum)
  public double Throttle;

  public double GimbalRangeRad;
  public double GimbalPitchDeflection;
  public double GimbalYawDeflection;

  public bool Ignited;
  public bool Flameout;

  public double Satisfaction => device != null ? device.Satisfaction : 0;
  public double NormalizedOutput => device != null ? device.Activity : 0;

  public class Propellant {
    public Resource Resource;
    public double Ratio; // volume ratio
    public double MaxFlow; // max volumetric flow at full throttle
  }

  public List<Propellant> Propellants = new();

  private const double G0 = 9.80665;

  private double massFlow; // kg/s at full throttle
  private double batchMass; // kg per recipe batch

  private ResourceSolver.Device device;

  public void Initialize(double thrust, double isp,
      List<(Resource resource, double ratio)> propellants) {
    Thrust = thrust;
    Isp = isp;

    Propellants.Clear();
    foreach (var (resource, ratio) in propellants) {
      Propellants.Add(new Propellant {
        Resource = resource,
        Ratio = ratio,
      });
    }

    ComputeDerivedFields();
  }

  private void ComputeDerivedFields() {
    // Constant mass flow: F = Isp * g0 * mdot → mdot = F / (Isp * g0)
    massFlow = Thrust * 1000 / (Isp * G0);
    batchMass = Propellants.Sum(p => p.Ratio * p.Resource.Density);

    // Pre-compute max volumetric flow per propellant (at full throttle).
    if (batchMass > 0) {
      var maxBatchRate = massFlow / batchMass;
      foreach (var prop in Propellants)
        prop.MaxFlow = maxBatchRate * prop.Ratio;
    }
  }

  public override VirtualComponent Clone() {
    var clone = new Engine {
      Thrust = Thrust,
      Isp = Isp,
      Throttle = Throttle,
      GimbalRangeRad = GimbalRangeRad,
      GimbalPitchDeflection = GimbalPitchDeflection,
      GimbalYawDeflection = GimbalYawDeflection,
    };
    clone.Propellants = Propellants.Select(p => new Propellant {
      Resource = p.Resource,
      Ratio = p.Ratio,
      MaxFlow = p.MaxFlow,
    }).ToList();
    clone.massFlow = massFlow;
    clone.batchMass = batchMass;
    return clone;
  }

  // The solver node this engine's device sits on. Set once by
  // `OnBuildSolver` and cleared whenever the topology is rebuilt
  // (the next OnBuildSolver call will replace it with the fresh
  // node). Surfaced for telemetry — `NovaEngineTopic` walks reach
  // from here to compute the engine's fuel pool.
  public ResourceSolver.Node Node { get; private set; }

  public override void OnBuildSolver(ResourceSolver solver, ResourceSolver.Node node) {
    Node = node;
    device = node.AddDevice(ResourceSolver.Priority.Low);
    foreach (var prop in Propellants)
      device.AddInput(prop.Resource, prop.MaxFlow);
  }

  public override void OnPreSolve() {
    if (device != null)
      device.Demand = Throttle;
  }
}
