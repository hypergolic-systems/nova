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
  public double AlternatorRate; // EC/s at full output (0 = no alternator)

  // Runtime state mirroring KSP's ignition/flameout flags. Written
  // by NovaEngineModule each tick; read by NovaEngineTopic to drive
  // the wire's status byte.
  public bool Ignited;
  public bool Flameout;

  public double Satisfaction => device != null ? device.Satisfaction : 0;
  public double NormalizedOutput => device != null ? device.Activity : 0;
  // Live EC/s the alternator is delivering after the LP throttles it
  // to actual load. Distinct from `AlternatorRate * NormalizedOutput`,
  // which is the *capacity* at this engine throttle — when load is
  // below capacity, the LP scales `alternator.Activity` down and the
  // real output is well below capacity.
  public double AlternatorOutput =>
      alternator != null ? alternator.Activity * AlternatorRate : 0;

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
  private ResourceSolver.Converter alternator;

  public void Initialize(double thrust, double isp, double alternatorRate,
      List<(Resource resource, double ratio)> propellants) {
    Thrust = thrust;
    Isp = isp;
    AlternatorRate = alternatorRate;

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
      AlternatorRate = AlternatorRate,
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

  public override void OnBuildSolver(ResourceSolver solver, ResourceSolver.Node node) {
    device = node.AddDevice(ResourceSolver.Priority.Low);
    foreach (var prop in Propellants)
      device.AddInput(prop.Resource, prop.MaxFlow);

    if (AlternatorRate > 0) {
      alternator = node.AddConverter();
      alternator.AddParent(device);
      alternator.AddOutput(Resource.ElectricCharge, AlternatorRate);
    }
  }

  public override void OnPreSolve() {
    if (device != null)
      device.Demand = Throttle;
  }
}
