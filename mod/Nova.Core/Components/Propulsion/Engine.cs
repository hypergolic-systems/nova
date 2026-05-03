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

  // Effective throttle achieved this tick — Throttle scaled down by
  // the worst-case per-propellant satisfaction. When all propellants
  // are fully reachable: NormalizedOutput == Throttle. When one
  // propellant is starved (e.g. RP-1 reach empty): the engine effective
  // output is gated by the bottleneck — NormalizedOutput == Throttle ×
  // min(d.Activity).
  //
  // Note this means non-bottleneck propellants are slightly over-drained
  // for one tick when a bottleneck arises (we requested at full Throttle
  // but only delivered min Activity worth). The over-draw self-corrects
  // as MaxTickDt shrinks toward the bottleneck and we re-solve.
  public double NormalizedOutput {
    get {
      if (demands == null || demands.Count == 0) return 0;
      double minAct = 1.0;
      foreach (var d in demands) if (d.Activity < minAct) minAct = d.Activity;
      return Throttle * minAct;
    }
  }

  // Min activity across propellants — fraction of *requested* throttle
  // we actually achieved. Equal to 1.0 when fully satisfied.
  public double Satisfaction {
    get {
      if (demands == null || demands.Count == 0) return 0;
      double minAct = 1.0;
      foreach (var d in demands) if (d.Activity < minAct) minAct = d.Activity;
      return minAct;
    }
  }

  public class Propellant {
    public Resource Resource;
    public double Ratio; // volume ratio
    public double MaxFlow; // max volumetric flow at full throttle
  }

  public List<Propellant> Propellants = new();

  private const double G0 = 9.80665;

  private double massFlow; // kg/s at full throttle
  private double batchMass; // kg per recipe batch

  // Per-propellant staging demands. Order matches Propellants order so
  // OnPreSolve can update Rate per index without a lookup.
  private List<StagingFlowSystem.Demand> demands;

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

  // The staging node this engine's demands attach to. Set once by
  // OnBuildSystems and cleared whenever the topology is rebuilt.
  // Surfaced for telemetry — `NovaEngineTopic` walks reach from here
  // to compute the engine's fuel pool.
  public StagingFlowSystem.Node Node { get; private set; }

  public override void OnBuildSystems(VesselSystems systems, StagingFlowSystem.Node node) {
    Node = node;
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
