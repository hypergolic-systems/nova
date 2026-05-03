using System.Collections.Generic;
using System.Linq;
using Nova.Core.Resources;
using Nova.Core.Systems;

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

  // Effective throttle achieved this tick. Equals consumer.Activity,
  // which the staging system computes as Throttle × min(per-propellant
  // satisfaction). When all propellants are fully supplied, Activity ==
  // Throttle and NormalizedOutput == Throttle. When any propellant is
  // starved, the staging system's coupling pass scales Activity down
  // and the unstuck propellants stop being allocated — so the engine
  // doesn't over-drain anything, even when other engines on the same
  // stage continue to fire.
  public double NormalizedOutput => consumer?.Activity ?? 0;

  // Fraction of requested Throttle actually achieved (1.0 = fully
  // supplied; 0 = fully starved). Useful for telemetry that wants
  // satisfaction independent of the throttle setting.
  public double Satisfaction => Throttle > 1e-12 ? (consumer?.Activity ?? 0) / Throttle : 0;

  public class Propellant {
    public Resource Resource;
    public double Ratio; // volume ratio
    public double MaxFlow; // max volumetric flow at full throttle
  }

  public List<Propellant> Propellants = new();

  private const double G0 = 9.80665;

  private double massFlow; // kg/s at full throttle
  private double batchMass; // kg per recipe batch

  // Coupled-input consumer registered with the staging system. One
  // input per propellant; the staging solver couples them natively
  // (min Activity across inputs gates whether the engine fires at all).
  internal StagingFlowSystem.Consumer consumer;

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

  // The staging node this engine sits on. Set once by OnBuildSystems
  // and cleared whenever the topology is rebuilt. Surfaced for
  // telemetry — `NovaEngineTopic` walks reach from here to compute
  // the engine's fuel pool.
  public StagingFlowSystem.Node Node { get; private set; }

  public override void OnBuildSystems(VesselSystems systems, StagingFlowSystem.Node node) {
    Node = node;
    consumer = systems.Staging.RegisterConsumer(node);
    foreach (var prop in Propellants)
      consumer.AddInput(prop.Resource, prop.MaxFlow);
  }

  public override void OnPreSolve() {
    if (consumer != null) consumer.Throttle = Throttle;
  }
}
