using System.Collections.Generic;
using System.Linq;
using Nova.Core.Persistence.Protos;
using Nova.Core.Resources;
using Nova.Core.Systems;

namespace Nova.Core.Components.Propulsion;

public class Engine : VirtualComponent {
  public double Thrust; // kN (vacuum) — also serves as MaxThrust on the wire
  public double Isp; // s (vacuum)
  public double Throttle;

  // Engine class label declared in the cfg ("Booster", "Sustainer",
  // "Vacuum", "Ionic", ...). The catalogue is open — anything the cfg
  // writer chooses ends up on the wire verbatim. Surfaced in the editor
  // part-info popup as the marquee class line; never inferred from Isp /
  // propellant heuristics.
  public string Class;

  public double GimbalRangeRad;
  public double GimbalPitchDeflection;
  public double GimbalYawDeflection;

  // Staging-activated flag — true once the part has been staged and
  // the engine has been added to the player's throttle chain. Set in
  // NovaEngineModule.OnActive; persists through saves so a quicksave/
  // reload mid-burn (or vessel unload/reload) doesn't strand an
  // activated engine. Orthogonal to the reactor state machine on the
  // NuclearEngine subclass, which separately gates effective thrust.
  public bool Active;

  public bool Ignited;
  public bool Flameout;

  // Effective throttle achieved this tick. Equals device.Activity,
  // which the staging system computes as Demand × min(per-propellant
  // satisfaction). When all propellants are fully supplied, Activity ==
  // Demand and NormalizedOutput == Throttle. When any propellant is
  // starved, the staging system's coupling pass scales Activity down
  // and the unstuck propellants stop being allocated — so the engine
  // doesn't over-drain anything, even when other engines on the same
  // stage continue to fire.
  public double NormalizedOutput => device?.Activity ?? 0;

  // Thrust-producing output fraction this tick — what the part module
  // multiplies Thrust by to apply to the rigidbody, and what the engine
  // map wire emits as the throttle field. For a normal liquid engine,
  // any solver flow is thrust-producing, so this just equals
  // NormalizedOutput. The NuclearEngine subclass gates this by reactor
  // state so its idle-coolant LH₂ flow doesn't render as thrust.
  public virtual double ThrustOutputFraction => NormalizedOutput;

  // Status byte for the engine-map wire (NovaEngineTopic).
  //   0 burning, 1 flameout, 2 failed (reserved), 3 shutdown, 4 idle.
  // The NuclearEngine subclass overrides to reflect reactor state
  // (warming and cooling read as idle on the engine map; details
  // travel on the per-part NovaPartTopic "N" frame).
  public virtual byte EngineStatus {
    get {
      if (Ignited && Flameout) return 1;
      if (Ignited && NormalizedOutput > 0) return 0;
      if (Ignited) return 4;
      return 3;
    }
  }

  // Fraction of requested Throttle actually achieved (1.0 = fully
  // supplied; 0 = fully starved). Useful for telemetry that wants
  // satisfaction independent of the throttle setting.
  public double Satisfaction => Throttle > 1e-12 ? (device?.Activity ?? 0) / Throttle : 0;

  public class Propellant {
    public Resource Resource;
    public double Ratio; // volume ratio
    public double MaxFlow; // max volumetric flow at full throttle
  }

  public List<Propellant> Propellants = new();

  private const double G0 = 9.80665;

  private double massFlow; // kg/s at full throttle
  private double batchMass; // kg per recipe batch

  // Coupled-input device on the staging system. One input per
  // propellant; the staging solver couples them natively (min
  // Activity across inputs gates whether the engine fires at all).
  internal Device device;

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
      Class = Class,
      Active = Active,
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
    device = systems.AddDevice(node,
        inputs: Propellants.Select(p => (p.Resource, p.MaxFlow)).ToArray());
  }

  public override void OnPreSolve() {
    if (device != null) device.Demand = Throttle;
  }

  // Hook for DeltaVSimulation. The DV sim wants every staged engine
  // firing at full throttle for the duration. For a plain liquid
  // engine that's just `Throttle = 1`; the NuclearEngine subclass
  // overrides to force its state machine into Throttled with
  // ThrottleActual = 1 (otherwise the reactor would sit Cold and
  // contribute nothing to ΔV).
  public virtual void ActivateForBurn() {
    Throttle = 1.0;
    Ignited = true;
  }

  public override void Save(PartState state) {
    state.Engine = new EngineState { Active = Active };
  }

  public override void Load(PartState state) {
    if (state.Engine == null) return;
    Active = state.Engine.Active;
  }
}
