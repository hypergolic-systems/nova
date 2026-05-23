using System;
using Nova.Core.Persistence.Protos;
using Nova.Core.Resources;
using Nova.Core.Systems;
using Buffer = Nova.Core.Resources.Buffer;

namespace Nova.Core.Components.Propulsion;

// Trip-latch reason. Cast-equivalent to the proto IonEngineState.trip_reason int.
public enum IonTripReason {
  None         = 0,
  XeStarvation = 1,
  Overtemp     = 2,
}

// NSTAR-class ion thruster. Two cross-system inputs (Xenon topological
// via the inherited Engine.device, ElectricCharge uniform via ecDevice)
// and one bus-side output (Heat to ProcessFlowSystem at Critical
// priority, dissipated by Radiators). Both inputs declare Demand =
// Throttle and the solvers run simultaneously; post-gate combines them
// via min(activity) so the binding pool sets thrust.
//
// Coupling:
//   thrust  =  Throttle × MaxThrust × min(xe.Activity, ec.Activity) / Throttle
//           =  MaxThrust × min(xe.Activity, ec.Activity)
//   (both activities are 0..Throttle when supplied)
//
// Waste heat:
//   waste(t) = ec.Activity(t) × RatedPowerW × (1 - JetEfficiency)
// Heat is injected into a private HeatBuffer; exported via
// heatOutletDevice on the Process Heat bus. Buffer pattern is the same
// as Rtg.HeatBuffer — not registered with the LP, manual rate update
// in OnPostSolve, Critical-priority outlet device handles export.
//
// Trip semantics:
//   XeStarvation when xe.Satisfaction < ec.Satisfaction - threshold
//     (i.e. xenon couldn't supply requested flow despite EC available
//     — protects against firing the accelerator into vacuum).
//   Overtemp     when CoreTempK >= MaxOperatingTempK
//     (heat balance lost — no/insufficient radiator capacity).
// On trip: Active=false, Throttle=0, latched. Cleared via
// `setIonResetTrip` op (NovaPartTopic) — player must then re-stage.
public class IonEngine : Engine {

  // ── Config (declared in cfg, parsed by ComponentFactory) ────────────

  public double RatedPowerW;                // W of EC at full throttle
  public double JetEfficiency;              // 0..1 — fraction of EC → kinetic
  public double ThermalMassJK;              // J/K — engine heat capacity
  public double AmbientK;                   // K — ambient / cold-soak temp
  public double MaxOperatingTempK;          // K — overtemp trip threshold
  public double MaxHeatRejectionW;          // W — heat outlet bandwidth
  public double TripXeShortfallThreshold;   // 0..1 — Δsatisfaction for Xe trip

  // ── Runtime / persisted state ───────────────────────────────────────

  public bool          Tripped;
  public IonTripReason TripReason = IonTripReason.None;

  internal Device ecDevice;          // EC input  (Process, Low priority)
  internal Device heatOutletDevice;  // Heat output (Process, Critical)
  public   Buffer HeatBuffer;        // private — NOT in ProcessFlowSystem pool

  private const double Epsilon = 1e-9;

  // Populated by Load() and read by OnBuildSystems to seed the buffer.
  // Without this, the loaded core temp would be lost because the buffer
  // doesn't exist when Load runs.
  private double pendingLoadedCoreTempK = double.NaN;

  // ── Initialization ──────────────────────────────────────────────────

  public void InitializeIon(
      double thrust, double isp,
      Resource propellant,
      double ratedPowerW,
      double jetEfficiency,
      double thermalMassJK,
      double ambientK,
      double maxOperatingTempK,
      double maxHeatRejectionW,
      double tripXeShortfallThreshold) {

    // Single propellant (Xenon). Same one-prop construction as NTR.
    Initialize(thrust, isp,
      new System.Collections.Generic.List<(Resource, double)> {
        (propellant, 1.0),
      });

    RatedPowerW              = ratedPowerW;
    JetEfficiency            = jetEfficiency;
    ThermalMassJK            = thermalMassJK;
    AmbientK                 = ambientK;
    MaxOperatingTempK        = maxOperatingTempK;
    MaxHeatRejectionW        = maxHeatRejectionW;
    TripXeShortfallThreshold = tripXeShortfallThreshold;
  }

  public override VirtualComponent Clone() {
    // Engine.Clone() would construct a fresh `new Engine` and lose our
    // type. Build an IonEngine directly so DV-sim sees a working
    // ion-class engine.
    var clone = new IonEngine {
      Thrust = Thrust,
      Isp = Isp,
      Throttle = Throttle,
      Class = Class,
      Active = Active,
      GimbalRangeRad = GimbalRangeRad,
      GimbalPitchDeflection = GimbalPitchDeflection,
      GimbalYawDeflection = GimbalYawDeflection,
      RatedPowerW = RatedPowerW,
      JetEfficiency = JetEfficiency,
      ThermalMassJK = ThermalMassJK,
      AmbientK = AmbientK,
      MaxOperatingTempK = MaxOperatingTempK,
      MaxHeatRejectionW = MaxHeatRejectionW,
      TripXeShortfallThreshold = TripXeShortfallThreshold,
      Tripped = Tripped,
      TripReason = TripReason,
    };
    foreach (var p in Propellants) {
      clone.Propellants.Add(new Propellant {
        Resource = p.Resource, Ratio = p.Ratio, MaxFlow = p.MaxFlow,
      });
    }
    if (HeatBuffer != null) {
      clone.HeatBuffer = new Buffer {
        Resource = HeatBuffer.Resource,
        Capacity = HeatBuffer.Capacity,
        MaxRateIn = HeatBuffer.MaxRateIn,
        MaxRateOut = HeatBuffer.MaxRateOut,
        BaselineContents = HeatBuffer.BaselineContents,
        BaselineUT = HeatBuffer.BaselineUT,
      };
    }
    return clone;
  }

  // ── Derived observables ────────────────────────────────────────────

  public double CoreTempK =>
    HeatBuffer == null || ThermalMassJK <= Epsilon
      ? AmbientK
      : AmbientK + HeatBuffer.Contents / ThermalMassJK;

  // Actual EC drawn this tick (W).
  public double CurrentEcW =>
    ecDevice == null ? 0 : ecDevice.Activity * RatedPowerW;

  // Waste heat generated this tick (W). Tracks ecDevice.Activity, not
  // Throttle — when EC is starved we make less heat.
  public double CurrentWasteHeatW =>
    ecDevice == null ? 0 : ecDevice.Activity * RatedPowerW * (1 - JetEfficiency);

  // Heat exported to the bus this tick (W). Activity is the achieved
  // fraction of MaxHeatRejectionW.
  public double CurrentRejectionW =>
    heatOutletDevice == null ? 0 : heatOutletDevice.Activity * MaxHeatRejectionW;

  // Cross-pool coupling: actual thrust fraction = min of EC and Xe
  // activities, scaled into the [0, Throttle] range. Tripped engines
  // produce zero regardless of demand.
  public override double ThrustOutputFraction {
    get {
      if (Tripped) return 0;
      double xeAct = NormalizedOutput;        // device?.Activity
      double ecAct = ecDevice?.Activity ?? 0;
      return Math.Min(xeAct, ecAct);
    }
  }

  // Engine-map status byte. Tripped reads as shutdown — UI clearly
  // shows the engine isn't operable until reset.
  public override byte EngineStatus =>
    Tripped ? (byte)3 : base.EngineStatus;

  // ── Systems wiring ─────────────────────────────────────────────────

  public override void OnBuildSystems(VesselSystems systems, StagingFlowSystem.Node node) {
    // Base registers the Xenon staging consumer via the Propellants list.
    base.OnBuildSystems(systems, node);

    // EC input. Low priority — yields to avionics/wheels/active
    // cooling, which is the right behaviour for a discretionary
    // deep-space thruster.
    if (RatedPowerW > 0) {
      ecDevice = systems.AddDevice(node,
          inputs: new[] { (Resource.ElectricCharge, RatedPowerW) },
          priority: ProcessFlowSystem.Priority.Low);
    }

    // Heat outlet. Critical priority — matches Rtg.busOutletDevice and
    // Radiator intake. Production lives in a private HeatBuffer; the
    // outlet device is the only LP-visible Heat element on this engine.
    if (MaxHeatRejectionW > 0 && ThermalMassJK > 0) {
      double now = Vessel.Systems.Clock.UT;
      double seedTempK = double.IsNaN(pendingLoadedCoreTempK)
          ? AmbientK : pendingLoadedCoreTempK;
      HeatBuffer = new Buffer {
        Resource = Resource.Heat,           // informational label
        // Capacity sized large so it never clamps before the overtemp
        // trip fires. The buffer is a thermal mass accumulator, not a
        // capacity-limited storage — the engine is what fails, not the
        // heat reservoir. Mirrors NuclearEngine's HeatBuffer sizing.
        Capacity = ThermalMassJK * Math.Max(1, (MaxOperatingTempK - AmbientK) * 10),
        MaxRateIn = double.PositiveInfinity,
        MaxRateOut = MaxHeatRejectionW,
        Clock = systems.Clock,
        BaselineUT = now,
        BaselineContents = Math.Max(0, ThermalMassJK * (seedTempK - AmbientK)),
      };

      heatOutletDevice = systems.AddDevice(node,
          outputs: new[] { (Resource.Heat, MaxHeatRejectionW) },
          priority: ProcessFlowSystem.Priority.Critical);
    }
  }

  public override void OnPreSolve() {
    // Trip gate: clear Throttle before the base sets xeDevice.Demand,
    // and before we set the EC and heat-outlet demands. Belt-and-braces
    // on top of the part-module guard that already forces mainThrottle
    // to 0 when Tripped.
    if (Tripped) Throttle = 0;

    base.OnPreSolve();  // sets xeDevice (this.device).Demand = Throttle

    if (ecDevice != null)
      ecDevice.Demand = Throttle;

    if (heatOutletDevice != null && MaxHeatRejectionW > 0) {
      // Projected waste-heat production this tick based on the *demand*
      // (worst case at Throttle). Actual production in OnPostSolve
      // tracks ecDevice.Activity — any over-projection just means the
      // buffer cools faster than necessary, which is harmless.
      // Override to full demand whenever the buffer is hot, so a
      // saturated engine drains as aggressively as possible.
      double projWaste = Throttle * RatedPowerW * (1 - JetEfficiency);
      double bufferFrac = HeatBuffer != null && HeatBuffer.Capacity > Epsilon
          ? HeatBuffer.Contents / HeatBuffer.Capacity : 0;
      heatOutletDevice.Demand = bufferFrac > Epsilon
          ? 1.0
          : Math.Min(1.0, projWaste / MaxHeatRejectionW);
    }
  }

  public override void OnPostSolve() {
    if (this.device == null) return;

    double xeAct = NormalizedOutput;
    double ecAct = ecDevice?.Activity ?? 0;

    // Flameout: wanted thrust but coupling delivered noticeably less.
    // Mirrors NuclearEngine.OnPostSolve line 568.
    Flameout = Active
            && Throttle > 1e-9
            && Math.Min(xeAct, ecAct) < Throttle - 1e-6;

    // Thermal integration.
    if (HeatBuffer != null && heatOutletDevice != null) {
      double now = Vessel.Systems.Clock.UT;
      double wasteW  = ecAct * RatedPowerW * (1 - JetEfficiency);
      double exportW = heatOutletDevice.Activity * MaxHeatRejectionW;
      double rate = wasteW - exportW;

      // Buffer-edge clamps mirror Rtg.OnPostSolve lines 296-299.
      bool bufferFull  = HeatBuffer.Contents >= HeatBuffer.Capacity - Epsilon;
      bool bufferEmpty = HeatBuffer.Contents <= Epsilon;
      if (bufferFull  && rate > 0) rate = 0;
      if (bufferEmpty && rate < 0) rate = 0;

      HeatBuffer.Rate = rate;

      // Forecast next state-change: time to cross the overtemp trip
      // threshold (if heating) or to drain back to ambient (if
      // cooling). The trip threshold matters more than buffer
      // capacity — once we cross MaxOperatingTempK the engine trips
      // and stops producing heat, so scheduling re-solve at the trip
      // boundary is the load-bearing forecast. Buffer is sized large
      // enough that capacity never clamps before trip fires.
      double dt = double.PositiveInfinity;
      if (rate > Epsilon) {
        double tripContentsJ = ThermalMassJK * Math.Max(0, MaxOperatingTempK - AmbientK);
        if (HeatBuffer.Contents < tripContentsJ)
          dt = (tripContentsJ - HeatBuffer.Contents) / rate;
        else
          dt = 0;  // already past trip threshold
      } else if (rate < -Epsilon) {
        if (HeatBuffer.Contents > Epsilon) dt = HeatBuffer.Contents / -rate;
      }
      heatOutletDevice.ValidUntil = now + dt;
      ValidUntil = now + dt;
    }

    // Trip detection. Sticky — once latched, only the explicit reset op
    // clears it. Guard on Throttle to avoid spurious trips at idle.
    if (!Tripped && Throttle > 1e-6) {
      double xeSat = Throttle > Epsilon ? xeAct / Throttle : 0;
      double ecSat = Throttle > Epsilon ? ecAct / Throttle : 0;
      if (xeSat < ecSat - TripXeShortfallThreshold) {
        Trip(IonTripReason.XeStarvation);
      } else if (CoreTempK >= MaxOperatingTempK) {
        Trip(IonTripReason.Overtemp);
      }
    } else if (!Tripped && CoreTempK >= MaxOperatingTempK) {
      // Overtemp can trip even at zero throttle if the buffer is still
      // hot at load time (or just-finished burn that overshot).
      Trip(IonTripReason.Overtemp);
    }
  }

  // Latch the trip. Forces the engine off until the player calls the
  // reset op. Active cleared so stock SAS / staging UI see a stopped
  // engine; Throttle cleared so a stale value can't drive demand next
  // FixedUpdate before the part module re-syncs from mainThrottle.
  private void Trip(IonTripReason reason) {
    Tripped = true;
    TripReason = reason;
    Active = false;
    Throttle = 0;
  }

  // DV-sim hook — clear trip latch before the base activates so the
  // simulator never inherits a tripped state from the live craft.
  public override void ActivateForBurn() {
    Tripped = false;
    TripReason = IonTripReason.None;
    if (HeatBuffer != null) HeatBuffer.Contents = 0;
    base.ActivateForBurn();
  }

  // ── Save / Load ─────────────────────────────────────────────────────

  public override void Save(PartState state) {
    state.IonEngine = new IonEngineState {
      Active     = Active,
      Tripped    = Tripped,
      TripReason = (int)TripReason,
      CoreTempK  = CoreTempK,
    };
  }

  public override void Load(PartState state) {
    if (state.IonEngine == null) return;
    Active     = state.IonEngine.Active;
    Tripped    = state.IonEngine.Tripped;
    TripReason = (IonTripReason)state.IonEngine.TripReason;
    pendingLoadedCoreTempK = state.IonEngine.CoreTempK;
    // Throttle is a per-tick input — NovaIonEngineModule.FixedUpdate
    // refreshes from ctrlState.mainThrottle. Resetting to 0 ensures any
    // solve between Load and the next FixedUpdate sees a stopped
    // engine (mirrors Engine.Load).
    Throttle = 0;
    // If the buffer already exists (load-after-build path), reseed
    // contents in place so the lerp baseline reflects the loaded temp.
    if (HeatBuffer != null) {
      HeatBuffer.Contents = Math.Max(0,
          ThermalMassJK * (state.IonEngine.CoreTempK - AmbientK));
    }
  }
}
