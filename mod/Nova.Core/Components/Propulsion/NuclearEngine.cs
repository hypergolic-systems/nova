using System;
using Nova.Core.Persistence.Protos;
using Nova.Core.Resources;
using Nova.Core.Systems;
using Buffer = Nova.Core.Resources.Buffer;

namespace Nova.Core.Components.Propulsion;

// Reactor state. Cast-equivalent to the proto NuclearEngineState.state
// int. The order matters — Save/Load round-trips through (int)state.
//
// Idle (legacy = 2) is no longer set by the state machine: "Idle" is a
// UI label inferred from a low ThrottleActual within the Throttled
// state. The enum value is kept so saved states from earlier builds
// keep deserialising; Load() migrates `Idle` → `Throttled` so no
// runtime code path sees Idle past load time.
public enum ReactorState {
  Cold      = 0,  // core at ambient, no fission, no LH₂ flow
  Warming   = 1,  // fission @ idle power, LH₂ flowing for cooling, core warming toward IdleTempK
  Idle      = 2,  // [deprecated] migrates to Throttled on Load
  Throttled = 3,  // engine engaged; throttle ∈ [0, 1]; reactor power, T target, flow, thrust all functions of throttle
  Cooling   = 4,  // fission off, LH₂ stopped, core cooling radiatively toward ColdThresholdK
}

// LV-N — Nuclear Thermal Rocket.
//
// An Engine subclass with a reactor state machine layered over the
// existing propellant-flow plumbing. The four live states are:
//
//   Cold       inert; no fission, no flow
//   Warming    starting up; fission at idle power, LH₂ flowing for
//              cooling, core climbs toward IdleTempK
//   Throttled  engine engaged; throttle in [0, 1] controls reactor
//              power, target temperature, propellant flow, and thrust
//   Cooling    shutting down; fission off, no flow, core falls toward
//              ColdThresholdK via the linear-radiative term
//
// Within Throttled, behaviour is fully a function of `ThrottleActual`:
//
//   FissionPowerW   = IdlePowerW + (MaxPowerW − IdlePowerW) × throttle
//
//   TargetTempK     ramp:    IdleTempK → OperatingTempK over the
//                            spool zone (throttle ∈ [0, SpoolEndThrottle])
//                   plateau: OperatingTempK above that
//
//   FlowDemandKgs   derived from the steady-state heat balance at
//                   TargetTempK and FissionPowerW. The reactor's
//                   coolant valve is modelled implicitly: whatever
//                   flow it takes to hold the target.
//
//   Isp(T)          IspRated × √(T / OperatingTempK) — exhaust velocity
//                   scales with √T, so cold-core thrust is derated.
//
//   ThrustKn        Lh2FlowKgs × Isp(CoreTempK) × g₀, gated to zero
//                   when ThrottleActual < ε (player-friendly "Idle" =
//                   no impulse; the hot gas is treated as venting
//                   laterally for force cancellation).
//
// Heat model (unchanged from prior version):
//   Private HeatBuffer (Joules), not registered with any system —
//   CoreTempK = AmbientK + HeatBuffer.Contents / ThermalMassJK.
//   Each tick the rate is set at a temperature-tier midpoint
//   (50 K-wide tiers) so the lerp traces an exponential approach as
//   piecewise-linear segments. At the equilibrium tier we pin rate
//   to 0 and snap the buffer to the analytic T_eq. See
//   ComputeThermalRateAndEvent.
//
// Throttle slew: ThrottleActual is a clock-anchored property that
// lerps toward `TargetThrottle()` at SlewRatePerSec. ForecastSlewEvent
// fires a ValidUntil at every 1% throttle quantum crossing, so the LP
// re-solves often enough that mass-flow and thrust track the slew
// smoothly rather than freezing between the slew-start and slew-end
// events.
//
// `ShutdownRequested` forces `TargetThrottle()` to 0 regardless of
// player input. When the slew lands at 0 with the flag set, the state
// machine transitions Throttled → Cooling.
public class NuclearEngine : Engine {

  // ── Config (declared in cfg, parsed by ComponentFactory) ────────────

  public double ThermalMassJK;       // J/K — heat capacity of the core
  public double AmbientK;            // K — ambient / cold-soak temperature
  public double ColdThresholdK;      // K — Cooling → Cold transition temp
  public double WarmupDurationSec;   // s — wall-clock duration of the
                                     //     procedural linear-ramp warmup.
                                     //     Temperature climbs in a
                                     //     straight line from AmbientK
                                     //     to IdleTempK over this many
                                     //     seconds; state transitions
                                     //     Warming → Throttled at the
                                     //     end. Heat-balance physics
                                     //     resumes once in Throttled.
  public double IdleTempK;           // K — target temp at throttle=0.
                                     //      Idle equilibrium = IdleTempK.
  public double OperatingTempK;      // K — target temp at throttle ≥ SpoolEnd.
                                     //      Rated Isp is at this temperature.
  public double SpoolEndThrottle;    // 0..1 — throttle at which T target
                                     //      reaches OperatingTempK. Below it,
                                     //      T target lerps IdleTempK → OpTemp.
  public double IdlePowerW;          // W — fission power at throttle=0.
  public double MaxPowerW;           // W — fission power at throttle=1.
  public double CoolingCoeffWK;      // W/K — linear "radiative" cooling
  public double InletTempK;          // K — LH₂ inlet temperature
  public double CpH2JKgK;            // J/kg/K — hot-H₂ specific heat
  public double SlewRatePerSec;      // 1/s — throttle slew rate
  public double DecayTauSeconds;     // s — exponential-decay time constant
                                     //     for fission-product decay heat
                                     //     during Cooling. Real NTRs keep
                                     //     producing heat after SCRAM;
                                     //     quantized into multiplicative
                                     //     power tiers so the LP only
                                     //     re-solves at tier boundaries.
                                     //     LH₂ cooling flow continues
                                     //     until decay heat drops below
                                     //     what radiative cooling can
                                     //     absorb. See FissionPowerW for
                                     //     the Cooling case.

  // ── Runtime / persisted state ───────────────────────────────────────

  public ReactorState State = ReactorState.Cold;
  public double PlayerThrottle;       // 0..1 — last setpoint from FixedUpdate
  public bool   ShutdownRequested;    // latched flag — drains throttle to 0 then Cools

  // ThrottleActual is clock-anchored: lerps off (slewBaselineThrottle,
  // slewBaselineUT) toward lastSlewTarget at SlewRatePerSec. Same lerp
  // pattern as Buffer — between events at warp the value stays correct
  // because the getter is the only source of truth. Setter re-anchors
  // (used by Load / tests).
  public double ThrottleActual {
    get {
      var clock = Vessel?.Systems?.Clock;
      if (clock == null || SlewRatePerSec <= 0) return slewBaselineThrottle;
      double elapsed = Math.Max(0, clock.UT - slewBaselineUT);
      double delta = lastSlewTarget - slewBaselineThrottle;
      double maxAdvance = SlewRatePerSec * elapsed;
      if (Math.Abs(delta) <= maxAdvance) return lastSlewTarget;
      return slewBaselineThrottle + Math.Sign(delta) * maxAdvance;
    }
    set {
      slewBaselineThrottle = value;
      slewBaselineUT = Vessel?.Systems?.Clock?.UT ?? 0;
      // lastSlewTarget unchanged — caller may also set state/player
      // input separately and AdvanceSlewToNow will re-anchor target
      // on its next call if the target changed.
    }
  }

  // Internal energy store (Joules). NOT registered with any system —
  // pure clock-lerp tracker. CoreTempK is derived. We mirror RTG's
  // private-buffer setup pattern (Resources/Rtg.cs:225-232) but the
  // resource label is informational; nothing reads it.
  public Buffer HeatBuffer;

  // LH₂ idle cooling flow demand (kg/s) — derived from idle heat
  // balance, cached for convenient access by callers (formerly used
  // for the demand calc directly; now subsumed by ComputeFlowDemandKgs
  // which evaluates the heat balance at the current throttle).
  public double IdleCoolingFlowKgs { get; private set; }

  // Mass flow at full throttle (kg/s) — Engine.Initialize sets MaxFlow
  // (volumetric) per propellant; we re-derive the mass version for
  // the heat balance. There's exactly one propellant (LH₂) so this is
  // simple; the cfg-side gates that invariant.
  public double MaxFlowKgs { get; private set; }

  // Most recent solved mass flow (kg/s). Cached during OnPostSolve
  // for telemetry; the engine-map wire keeps using NormalizedOutput.
  public double Lh2FlowKgs { get; private set; }

  // Clock-anchored slew state. Captures (baseline-throttle, baseline-
  // UT, target). The lerp evaluates `ThrottleActual` against the live
  // clock; AdvanceSlewToNow re-anchors whenever the effective target
  // changes (player input, state transition, shutdown latch).
  private double slewBaselineThrottle;
  private double slewBaselineUT;
  private double lastSlewTarget;
  private bool clockAnchored;

  // Below this throttle, thrust is gated to zero — gameplay concession
  // so the "Idle" UI position doesn't produce stray impulse. We
  // hand-wave the hot LH₂ as venting symmetrically. Set deliberately
  // small so anything the player actually types as a throttle value
  // gives real thrust.
  private const double ThrustGateThrottle = 1e-3;

  // Slew quantum — every 1% crossing fires a re-solve so the LP-solved
  // flow tracks the smoothly-lerping ThrottleActual. Without this, the
  // LP only re-solves at slew-start and slew-end, and the displayed
  // thrust + flow stay frozen for the whole transit (only ACT moves).
  private const double SlewQuantum = 0.01;

  // Decay-heat quantization. Each tier holds for `τ × ln(1/ratio)`
  // seconds, then the power steps down by `ratio`. The thermal-tier
  // solver does the within-step heat balance; each tier crossing
  // re-solves with the new (lower) P_decay → new equilibrium T.
  // Picked at 0.85 so cooldown traces ~20 visible steps from idle
  // power to floor over ~150 s (τ = 30 s); fewer steps look stepped,
  // more steps cost more solves with no visible benefit.
  private const double DecayHeatTierFrac = 0.85;
  // Below this power, decay heat is effectively zero — radiative
  // cooling alone can absorb it without needing LH₂ flow. Picked so
  // the rad term at IdleTempK − AmbientK (≈ 4 MW) is well above this
  // floor; once we drop past it the core can radiate down to
  // ColdThresholdK without active cooling.
  private const double DecayHeatFloorW = 100_000;  // 100 kW

  // Decay-heat baseline captured at Cooling-entry. Not persisted —
  // a reload mid-Cooling restarts the decay timer (single-cycle
  // approximation).
  private double decayStartPowerW;
  private double decayStartUT;

  // Warmup-ramp anchor — captured each time the reactor enters
  // Warming (from Cold or restart-from-Cooling). The state machine
  // ramps Buffer.Contents linearly from its value at this UT toward
  // m × (IdleTempK − AmbientK) over `WarmupDurationSec` seconds.
  // Capturing on entry means a restart from Cooling resumes the
  // ramp from whatever T the core currently sits at, which is
  // intuitive (a hot core warms up faster than a cold one).
  private double warmingStartUT;
  private double warmingStartContentsJ;

  // ── Initialization ──────────────────────────────────────────────────

  public void InitializeNuclear(
      double thrust, double isp,
      Resource propellant,
      double thermalMassJK,
      double ambientK,
      double coldThresholdK,
      double warmupDurationSec,
      double idleTempK,
      double operatingTempK,
      double spoolEndThrottle,
      double idlePowerW,
      double maxPowerW,
      double coolingCoeffWK,
      double inletTempK,
      double cpH2JKgK,
      double slewRatePerSec,
      double decayTauSeconds) {

    // The NTR has exactly one propellant — LH₂. Reuse the base Engine's
    // Initialize for the mass/volume flow derivation; we hand it a
    // single propellant at ratio = 1.0 and read MaxFlow back off.
    Initialize(thrust, isp,
      new System.Collections.Generic.List<(Resource, double)> {
        (propellant, 1.0),
      });

    ThermalMassJK      = thermalMassJK;
    AmbientK           = ambientK;
    ColdThresholdK     = coldThresholdK;
    WarmupDurationSec  = warmupDurationSec;
    IdleTempK          = idleTempK;
    OperatingTempK     = operatingTempK;
    SpoolEndThrottle   = spoolEndThrottle;
    IdlePowerW         = idlePowerW;
    MaxPowerW          = maxPowerW;
    CoolingCoeffWK     = coolingCoeffWK;
    InletTempK         = inletTempK;
    CpH2JKgK           = cpH2JKgK;
    SlewRatePerSec     = slewRatePerSec;
    DecayTauSeconds    = decayTauSeconds;

    // Convert the single propellant's MaxFlow (L/s at full throttle)
    // to a mass-flow figure for the heat balance.
    MaxFlowKgs = Propellants[0].MaxFlow * Propellants[0].Resource.Density;

    // Idle cooling flow is the mdot that, at CoreTempK = IdleTempK,
    // balances IdlePowerW against convection + linear radiative loss.
    // Cached for telemetry / tests; the live demand calc derives the
    // current mdot from the same formula at TargetTempK(throttle).
    IdleCoolingFlowKgs = FlowToHoldTemp(IdleTempK, IdlePowerW);
  }

  public override VirtualComponent Clone() {
    // Engine.Clone() constructs a fresh `new Engine`, which loses our
    // type. Build a NuclearEngine directly. (DeltaVSimulation clones
    // the vessel before stepping it; the Clone has to be a working
    // NuclearEngine with state copied.)
    var clone = new NuclearEngine {
      Thrust = Thrust,
      Isp = Isp,
      Throttle = Throttle,
      Active = Active,
      GimbalRangeRad = GimbalRangeRad,
      GimbalPitchDeflection = GimbalPitchDeflection,
      GimbalYawDeflection = GimbalYawDeflection,
      ThermalMassJK = ThermalMassJK,
      AmbientK = AmbientK,
      ColdThresholdK = ColdThresholdK,
      WarmupDurationSec = WarmupDurationSec,
      IdleTempK = IdleTempK,
      OperatingTempK = OperatingTempK,
      SpoolEndThrottle = SpoolEndThrottle,
      IdlePowerW = IdlePowerW,
      MaxPowerW = MaxPowerW,
      CoolingCoeffWK = CoolingCoeffWK,
      InletTempK = InletTempK,
      CpH2JKgK = CpH2JKgK,
      SlewRatePerSec = SlewRatePerSec,
      State = State,
      PlayerThrottle = PlayerThrottle,
      ShutdownRequested = ShutdownRequested,
      IdleCoolingFlowKgs = IdleCoolingFlowKgs,
      MaxFlowKgs = MaxFlowKgs,
    };
    foreach (var p in Propellants) {
      clone.Propellants.Add(new Propellant {
        Resource = p.Resource, Ratio = p.Ratio, MaxFlow = p.MaxFlow,
      });
    }
    clone.slewBaselineThrottle = this.ThrottleActual;
    clone.slewBaselineUT = 0;
    clone.lastSlewTarget = this.lastSlewTarget;
    return clone;
  }

  // ── Throttle-driven physics ─────────────────────────────────────────

  // Fission power (W) for the current state + throttle. Cold/Cooling
  // produce zero (per spec — Cooling cools radiatively, no decay heat
  // term). Warming holds at IdlePowerW (the reactor is bringing the
  // core up to idle temp). Throttled scales linearly with throttle
  // from IdlePowerW (at 0) to MaxPowerW (at 1).
  public double FissionPowerW() {
    switch (State) {
      case ReactorState.Cold:
        return 0;
      case ReactorState.Warming: {
        // During the forced linear ramp the implicit reactor power
        // is whatever sustains dT/dt = (IdleTempK−AmbientK)/duration
        // against the current cooling losses. Reported on the wire
        // as the live THERMAL value — it climbs from a few MW (cold
        // core, small cooling) to ~IdlePowerW at end-of-ramp.
        double dTdt = WarmupDurationSec > 0
            ? (IdleTempK - AmbientK) / WarmupDurationSec : 0;
        double mdot = IdleCoolingFlowKgs;
        double cooling = mdot * CpH2JKgK * Math.Max(0, CoreTempK - InletTempK)
                       + CoolingCoeffWK * Math.Max(0, CoreTempK - AmbientK);
        return ThermalMassJK * dTdt + cooling;
      }
      case ReactorState.Throttled: {
        double t = Math.Max(0, Math.Min(1, ThrottleActual));
        return IdlePowerW + (MaxPowerW - IdlePowerW) * t;
      }
      case ReactorState.Cooling: {
        return DecayHeatAt(Vessel?.Systems?.Clock?.UT ?? decayStartUT);
      }
      default:
        return 0;
    }
  }

  // Quantized-tier decay heat. Power steps down by DecayHeatTierFrac
  // every TierDurationSec; underneath each step the value is constant
  // so the thermal-tier solver sees a step input and asymptotes to a
  // new equilibrium. Pinned to 0 once the schedule drops below
  // DecayHeatFloorW — radiative cooling alone takes it from there.
  private double DecayHeatAt(double ut) {
    if (decayStartPowerW <= DecayHeatFloorW) return 0;
    if (DecayTauSeconds <= 0) return decayStartPowerW;
    double tierDuration = DecayTauSeconds * Math.Log(1.0 / DecayHeatTierFrac);
    if (tierDuration <= 0) return decayStartPowerW;
    double elapsed = Math.Max(0, ut - decayStartUT);
    int tier = (int)Math.Floor(elapsed / tierDuration);
    double power = decayStartPowerW * Math.Pow(DecayHeatTierFrac, tier);
    return power < DecayHeatFloorW ? 0 : power;
  }

  // Next decay-heat step boundary. The thermal-tier solver only fires
  // ValidUntil at temperature crossings, so we add this here to make
  // sure the LP re-solves when P_decay drops to its next value
  // (otherwise we'd integrate the buffer's rate against a stale
  // higher-P_decay value across many tier durations).
  private double ForecastDecayTierEvent(double now) {
    if (State != ReactorState.Cooling) return double.PositiveInfinity;
    if (decayStartPowerW <= DecayHeatFloorW) return double.PositiveInfinity;
    if (DecayTauSeconds <= 0) return double.PositiveInfinity;
    double tierDuration = DecayTauSeconds * Math.Log(1.0 / DecayHeatTierFrac);
    if (tierDuration <= 0) return double.PositiveInfinity;
    double elapsed = Math.Max(0, now - decayStartUT);
    int currentTier = (int)Math.Floor(elapsed / tierDuration);
    double nextTierPower = decayStartPowerW * Math.Pow(DecayHeatTierFrac, currentTier + 1);
    if (nextTierPower < DecayHeatFloorW) return double.PositiveInfinity;
    return decayStartUT + (currentTier + 1) * tierDuration;
  }

  // Capture decay-heat baseline + entry UT before flipping into the
  // Cooling state. Called from all paths that enter Cooling
  // (Warming → Cooling on cancel, Throttled → Cooling on shutdown
  // latch). FissionPowerW reads current state, so we capture BEFORE
  // changing it.
  private void EnterCooling() {
    double powerNow = FissionPowerW();
    State = ReactorState.Cooling;
    decayStartPowerW = powerNow;
    decayStartUT = Vessel?.Systems?.Clock?.UT ?? 0;
  }

  // Capture the warmup-ramp anchor as we enter Warming. The buffer's
  // current contents become the start of the linear ramp; the ramp
  // ends at IdleTempK at warmingStartUT + WarmupDurationSec.
  private void EnterWarming() {
    State = ReactorState.Warming;
    warmingStartUT = Vessel?.Systems?.Clock?.UT ?? 0;
    warmingStartContentsJ = HeatBuffer?.Contents ?? 0;
  }

  // Target core temperature the coolant valve regulates toward. In
  // Throttled the spool zone (throttle ∈ [0, SpoolEnd]) lerps from
  // IdleTempK at throttle=0 to OperatingTempK at SpoolEnd; above
  // SpoolEnd we plateau at OperatingTempK. Warming uses IdleTempK so
  // the cooling flow during startup matches idle-equilibrium math.
  public double TargetTempK() {
    if (State == ReactorState.Cold) return AmbientK;
    if (State == ReactorState.Warming) return IdleTempK;
    // Cooling: regulate cooling flow against decay heat at IdleTempK
    // until decay drops below what radiative can handle. The flow
    // demand formula clamps to ≥ 0 so once heat balance no longer
    // needs convective cooling, mdot drops to 0 and the core cools
    // radiatively below IdleTempK on its way to ColdThresholdK.
    if (State == ReactorState.Cooling) return IdleTempK;
    // Throttled
    double t = Math.Max(0, Math.Min(1, ThrottleActual));
    if (SpoolEndThrottle <= 0 || t >= SpoolEndThrottle) return OperatingTempK;
    return IdleTempK + (OperatingTempK - IdleTempK) * (t / SpoolEndThrottle);
  }

  // Mass flow (kg/s) needed to hold `targetT` against `power` via
  // the steady-state heat balance:
  //   power = mdot × cp × (T − T_inlet) + rad × (T − T_amb)
  // Returns 0 when convective ΔT is non-positive (degenerate).
  private double FlowToHoldTemp(double targetT, double power) {
    double convectiveCapacity = CpH2JKgK * Math.Max(0, targetT - InletTempK);
    if (convectiveCapacity <= 0) return 0;
    double radiative = CoolingCoeffWK * Math.Max(0, targetT - AmbientK);
    return Math.Max(0, (power - radiative) / convectiveCapacity);
  }

  // Live mdot demand (kg/s) for the LP. Cold pulls zero; the hot
  // states (including Cooling under decay-heat load) derive demand
  // from the current target temperature and fission power. The
  // reactor's coolant valve is modelled implicitly — whatever flow it
  // takes to hold the target. During Cooling, FissionPowerW returns
  // the decay-heat schedule and FlowToHoldTemp naturally drops to 0
  // once decay heat falls below what radiative can absorb at idle T.
  public double ComputeFlowDemandKgs() {
    if (State == ReactorState.Cold) return 0;
    return FlowToHoldTemp(TargetTempK(), FissionPowerW());
  }

  // Isp as a function of core temperature. Exhaust velocity scales
  // with √T (gas dynamics — V_exhaust ∝ √(T/M)). Rated Isp is the
  // value at OperatingTempK; at lower T the engine is derated.
  public double IspAt(double tempK) {
    if (OperatingTempK <= 0) return Isp;
    double ratio = Math.Sqrt(Math.Max(0, tempK) / OperatingTempK);
    return Isp * ratio;
  }

  // ── Derived observables ────────────────────────────────────────────

  public double CoreTempK =>
    HeatBuffer == null || ThermalMassJK <= 0
        ? AmbientK
        : AmbientK + HeatBuffer.Contents / ThermalMassJK;

  private const double G0 = 9.80665;

  // Reactor-gated thrust fraction. The base Engine assumes thrust is
  // Throttle × NormalizedOutput; the NTR's effective Isp varies with
  // core temperature, so we override to fold that in. Gated to zero
  // below ThrustGateThrottle — gameplay concession for a clean "no
  // thrust at idle" feel.
  //
  // ThrustOutputFraction × MaxThrust = real thrust (kN). With:
  //   thrust = mdot_actual × Isp(T) × g₀ / 1000   (kN)
  //   mdot_actual = NormalizedOutput × MaxFlowKgs
  //   MaxThrust = MaxFlowKgs × IspRated × g₀ / 1000  (by definition)
  // we get:
  //   ThrustOutputFraction = NormalizedOutput × Isp(T) / IspRated
  //                        = NormalizedOutput × √(T / OperatingTempK)
  public override double ThrustOutputFraction {
    get {
      if (State != ReactorState.Throttled) return 0;
      if (ThrottleActual < ThrustGateThrottle) return 0;
      if (OperatingTempK <= 0) return NormalizedOutput;
      double ratio = Math.Sqrt(Math.Max(0, CoreTempK) / OperatingTempK);
      return NormalizedOutput * ratio;
    }
  }

  public override byte EngineStatus {
    get {
      switch (State) {
        case ReactorState.Cold:    return 3;   // shutdown
        case ReactorState.Warming: return 4;   // idle (warming)
        case ReactorState.Cooling: return 4;   // idle (cooling)
        case ReactorState.Throttled:
          if (Flameout) return 1;
          return ThrustOutputFraction > 0 ? (byte)0 : (byte)4;
        default: return 3;
      }
    }
  }

  public double CurrentThrustKn => Thrust * ThrustOutputFraction;

  // Fission power being produced this tick (W). Sent on the wire so
  // the UI doesn't need to know the cfg-side IdlePowerW / MaxPowerW
  // calibration — single source of truth on the C# side.
  public double CurrentThermalPowerW => FissionPowerW();

  // ── Systems wiring ─────────────────────────────────────────────────

  public override void OnBuildSystems(VesselSystems systems, StagingFlowSystem.Node node) {
    base.OnBuildSystems(systems, node);

    double now = Vessel.Systems.Clock.UT;
    HeatBuffer = new Buffer {
      Resource = Resource.Heat,        // informational label only
      Capacity = ThermalMassJK * Math.Max(1, MaxPowerW),  // huge — never clamps
      MaxRateIn = double.PositiveInfinity,
      MaxRateOut = double.PositiveInfinity,
      Clock = systems.Clock,
      BaselineUT = now,
      BaselineContents = Math.Max(0, ThermalMassJK * (Load_CoreTempKOrFallback() - AmbientK)),
    };
    slewBaselineThrottle = ThrottleActual;
    slewBaselineUT = now;
    lastSlewTarget = TargetThrottle();
    clockAnchored = true;
  }

  // Populated by Load() and read by OnBuildSystems to set the buffer
  // baseline. Without this, a load-time core temp would be lost because
  // the buffer doesn't exist when Load runs.
  private double pendingLoadedCoreTempK = double.NaN;
  private double Load_CoreTempKOrFallback() {
    return double.IsNaN(pendingLoadedCoreTempK) ? AmbientK : pendingLoadedCoreTempK;
  }

  // ── State-machine + LP demand wiring (OnPreSolve / OnPostSolve) ─────

  public override void OnPreSolve() {
    if (device == null) return;
    if (!clockAnchored) return;

    AdvanceSlewToNow();
    ApplyStateTransitions();
    AdvanceSlewToNow();  // transitions may have changed the target

    double demandKgs = ComputeFlowDemandKgs();
    Throttle = MaxFlowKgs > 0 ? Math.Min(1.0, demandKgs / MaxFlowKgs) : 0;
    device.Demand = Throttle;
  }

  public override void OnPostSolve() {
    if (device == null || HeatBuffer == null) return;

    Lh2FlowKgs = NormalizedOutput * MaxFlowKgs;

    // Flameout: we wanted thrust-mode flow but the LP delivered < demand.
    Flameout = State == ReactorState.Throttled
            && Throttle > 1e-9
            && NormalizedOutput < Throttle - 1e-6;

    double now = Vessel.Systems.Clock.UT;

    // Quantized-tier heat balance — see ComputeThermalRateAndEvent for
    // the full reasoning. Pinning at equilibrium keeps the buffer
    // stable; tier crossings re-solve at the new midpoint.
    var (rateW, thermalEventUT) = ComputeThermalRateAndEvent(now);
    HeatBuffer.Rate = rateW;

    double thresholdUT = ForecastStateThresholdEvent(rateW, now);
    double slewUT = ForecastSlewEvent(now);
    double decayUT = ForecastDecayTierEvent(now);

    ValidUntil = Math.Min(
        Math.Min(thermalEventUT, thresholdUT),
        Math.Min(slewUT, decayUT));
  }

  // Tier width in Kelvin for the piecewise-linear heat-balance
  // approximation. 50 K → 64 tiers across the 0..3200 K display range.
  private const double ThermalTierKelvin = 50;

  private (double rateW, double validUntilUT) ComputeThermalRateAndEvent(double now) {
    if (ThermalMassJK <= 0) return (0, double.PositiveInfinity);

    // Cold: no fission, no coolant flow. The core sits at ambient
    // (Contents = 0). The tier-based code below would otherwise compute
    // a spurious −82.5 kW "radiative cooling" rate against the already-
    // floored buffer and schedule `ValidUntil = now` because dt =
    // (tier*tierContentsJ − Contents) / rate = 0 / -rate = 0 — the
    // VirtualVessel loop then spins to its 100-iter cap every
    // FixedUpdate. Cold has nothing to forecast; activation flips the
    // state machine and the next solve picks up the real schedule.
    if (State == ReactorState.Cold) return (0, double.PositiveInfinity);

    // Warming overrides the tier-based heat balance with a forced
    // linear ramp at a FIXED rate (K/s). Buffer.Rate is constant;
    // ValidUntil fires when Contents reaches m × (IdleTempK − AmbientK),
    // at which point the state machine transitions to Throttled.
    // Rate-limiting (not duration-limiting) means a restart from
    // Cooling — where the core is still hot — completes proportional
    // to the ΔT remaining, instead of forcing the player through a
    // full warmup wait. WarmupDurationSec is interpreted as the time
    // the ramp takes when starting from AmbientK; partial warmups
    // finish faster.
    if (State == ReactorState.Warming) {
      double endContentsJ = ThermalMassJK * Math.Max(0, IdleTempK - AmbientK);
      if (WarmupDurationSec <= 0 || HeatBuffer.Contents >= endContentsJ - 1e-3) {
        HeatBuffer.Contents = endContentsJ;
        return (0, now);
      }
      double rate = (IdleTempK - AmbientK) * ThermalMassJK / WarmupDurationSec;
      double timeToEnd = (endContentsJ - HeatBuffer.Contents) / rate;
      return (rate, now + timeToEnd);
    }

    double tierContentsJ = ThermalMassJK * ThermalTierKelvin;
    int tier = (int)Math.Floor(HeatBuffer.Contents / tierContentsJ);
    if (tier < 0) tier = 0;

    double pIn = FissionPowerW();
    double mdot = NormalizedOutput * MaxFlowKgs;

    double NetAt(double T) {
      double conv = mdot * CpH2JKgK * Math.Max(0, T - InletTempK);
      double rad  = CoolingCoeffWK * Math.Max(0, T - AmbientK);
      return pIn - conv - rad;
    }
    double MidOf(int t) => AmbientK + (t + 0.5) * ThermalTierKelvin;

    double rateHere = NetAt(MidOf(tier));

    // Equilibrium tier: rate at the neighbour tier flips sign, so
    // the analytic equilibrium falls between here and there. Switch
    // from tier-midpoint linearization to the analytic rate at the
    // *current* T and schedule short re-solves (τ/8) so the buffer
    // traces an asymptotic exponential approach toward T_eq rather
    // than snapping. Snapping made the transition visually jarring
    // — a 60 K jump as the warmup completed — because the analytic
    // equilibrium can sit well into the next tier (e.g. T_eq=1500 K
    // with a 50 K tier grid). Once we're within a few K of T_eq the
    // rate naturally pins to ≈ 0 and we set ValidUntil = ∞.
    double rateUp   = NetAt(MidOf(tier + 1));
    double rateDown = tier > 0 ? NetAt(MidOf(tier - 1)) : double.NegativeInfinity;
    bool atEqGoingUp   = rateHere > 0 && rateUp   < 0;
    bool atEqGoingDown = rateHere < 0 && rateDown > 0;
    if (atEqGoingUp || atEqGoingDown) {
      double denom = mdot * CpH2JKgK + CoolingCoeffWK;
      if (denom <= 0) return (0, double.PositiveInfinity);

      double tEq = (pIn + mdot * CpH2JKgK * InletTempK
                   + CoolingCoeffWK * AmbientK) / denom;
      double tCurrent = AmbientK + HeatBuffer.Contents / ThermalMassJK;

      const double pinToleranceK = 2.0;
      if (Math.Abs(tCurrent - tEq) <= pinToleranceK) {
        // Within tolerance — pin cleanly at T_eq so state-machine
        // threshold checks see a stable value.
        double eqContentsJ = Math.Max(0, ThermalMassJK * (tEq - AmbientK));
        HeatBuffer.Contents = eqContentsJ;
        return (0, double.PositiveInfinity);
      }
      // Asymptotic approach: analytic rate at T_current, re-solve
      // every τ/8 so the linear approximation tracks the exponential
      // curve closely. Floor at 0.25 s to avoid pathologically tight
      // event scheduling when τ is small (e.g., high-throttle τ ≈ 2 s).
      double tau = ThermalMassJK / denom;
      double rateAtCurrent = NetAt(tCurrent);
      double resolveStep = Math.Max(0.25, tau / 8);
      return (rateAtCurrent, now + resolveStep);
    }

    if (rateHere == 0) return (0, double.PositiveInfinity);
    double targetContentsJ = rateHere > 0
        ? (tier + 1) * tierContentsJ
        : tier * tierContentsJ;
    double dt = (targetContentsJ - HeatBuffer.Contents) / rateHere;
    if (dt < 0) dt = 0;
    return (rateHere, now + dt);
  }

  public override void Update(double nowUT) {
    AdvanceSlewToNow();
    ApplyStateTransitions();
    AdvanceSlewToNow();
    ValidUntil = double.PositiveInfinity;
  }

  // ── State machine implementation ────────────────────────────────────

  private void AdvanceSlewToNow() {
    var clock = Vessel?.Systems?.Clock;
    if (clock == null) return;
    double currentTarget = TargetThrottle();
    if (currentTarget != lastSlewTarget) {
      double currentThrottle = ThrottleActual;
      slewBaselineThrottle = currentThrottle;
      slewBaselineUT = clock.UT;
      lastSlewTarget = currentTarget;
    }
  }

  // The setpoint the slew chases.
  //   Cold / Cooling: 0 (no throttle while inert / shutting down)
  //   Warming:        0 (player throttle ignored during warmup)
  //   Throttled:      PlayerThrottle, clamped to [0, 1]
  // ShutdownRequested forces 0 in any state — used to drain throttle
  // before transitioning to Cooling.
  public double TargetThrottle() {
    if (ShutdownRequested) return 0;
    if (State != ReactorState.Throttled) return 0;
    return Math.Max(0, Math.Min(1, PlayerThrottle));
  }

  private void ApplyStateTransitions() {
    for (int safety = 0; safety < 6; safety++) {
      var prev = State;
      ApplyStateTransitionOnce();
      if (prev == State) return;
    }
  }

  private void ApplyStateTransitionOnce() {
    switch (State) {
      case ReactorState.Cold:
        // Cold → Warming is external (setReactorActive(true)).
        break;

      case ReactorState.Warming: {
        // Warming → Throttled when the forced linear ramp lands at
        // m × (IdleTempK − AmbientK). The ramp runs at a fixed K/s
        // rate (see ComputeThermalRateAndEvent), so a cold start
        // takes ~WarmupDurationSec and a restart from a warm core
        // completes proportionally faster.
        if (HeatBuffer == null) break;
        double endContentsJ = ThermalMassJK * Math.Max(0, IdleTempK - AmbientK);
        if (WarmupDurationSec <= 0 || HeatBuffer.Contents >= endContentsJ - 1e-3) {
          State = ReactorState.Throttled;
        }
        break;
      }

      case ReactorState.Idle:
        // Legacy state — should never be set by current code. Migrate
        // to Throttled defensively so a save from a previous build
        // loads cleanly.
        State = ReactorState.Throttled;
        break;

      case ReactorState.Throttled:
        // Shutdown auto-sequence: when the slew lands at 0 with the
        // shutdown latch set, drop to Cooling. The throttle gating in
        // TargetThrottle forces a slew to 0 immediately on the latch,
        // so this fires once the slew completes.
        if (ShutdownRequested && ThrottleActual <= 1e-9) {
          ShutdownRequested = false;
          EnterCooling();
        }
        break;

      case ReactorState.Cooling:
        if (CoreTempK <= ColdThresholdK) State = ReactorState.Cold;
        break;
    }
  }

  // DeltaVSimulation hook — force the reactor into a thrust-producing
  // state for the duration of the burn so the ΔV math actually sees
  // the engine. Snap to Throttled at full throttle with a hot core.
  public override void ActivateForBurn() {
    Active = true;
    State = ReactorState.Throttled;
    ShutdownRequested = false;
    PlayerThrottle = 1.0;
    ThrottleActual = 1.0;
    if (HeatBuffer != null) {
      HeatBuffer.Contents = ThermalMassJK * Math.Max(0, OperatingTempK - AmbientK);
    }
    lastSlewTarget = 1.0;
  }

  // External op entry point — `setReactorActive` from NovaPartTopic.
  //
  //   active=true:  Cold → Warming (cold start).
  //                 Cooling → Warming (restart mid-cooldown — core is
  //                                    still hot; just resume).
  //                 Throttled with ShutdownRequested → clears the
  //                                    latch ("cancel shutdown").
  //                 Other states     → no-op.
  //   active=false: Warming   → Cooling (cancels warmup).
  //                 Throttled → latch ShutdownRequested; the auto-
  //                             sequence forces throttle to 0 and
  //                             then transitions to Cooling.
  //                 Cold/Cooling → no-op.
  //
  // Returns true if the op produced a visible state change (op handler
  // uses this to decide whether to MarkDirty the topic).
  public bool SetReactorActive(bool active) {
    if (active) {
      bool changed = false;
      // Cancel any queued shutdown — the player has changed their
      // mind. This is the path for "I started a shutdown, but I want
      // to keep burning."
      if (ShutdownRequested) {
        ShutdownRequested = false;
        changed = true;
      }
      // Both Cold (initial start) and Cooling (resume) flow into
      // Warming. From Cooling the core is still hot, so Warming will
      // ramp restarts from whatever T the core currently sits at;
      // a hot core (restart-from-Cooling) reaches operating temp
      // faster than a cold one because the ramp covers less ΔT in
      // the same wall-clock duration.
      if (State == ReactorState.Cold || State == ReactorState.Cooling) {
        EnterWarming();
        return true;
      }
      return changed;
    }
    switch (State) {
      case ReactorState.Warming:
        EnterCooling();
        return true;
      case ReactorState.Throttled:
        if (ShutdownRequested) return false;
        ShutdownRequested = true;
        return true;
      case ReactorState.Cold:
      case ReactorState.Cooling:
        return false;
      default:
        return false;
    }
  }

  // ── ValidUntil forecasts ────────────────────────────────────────────

  private double ForecastStateThresholdEvent(double netRateW, double now) {
    if (HeatBuffer == null || ThermalMassJK <= 0) return double.PositiveInfinity;

    // Warming's transition is time-based, scheduled inside
    // ComputeThermalRateAndEvent (ValidUntil = warmingStartUT +
    // WarmupDurationSec). No temperature-threshold check needed here.
    if (State == ReactorState.Cooling) {
      double targetContents = ThermalMassJK
          * Math.Max(0, ColdThresholdK - AmbientK);
      if (HeatBuffer.Contents <= targetContents) return now;
      if (netRateW >= 0) return double.PositiveInfinity;
      return now + (HeatBuffer.Contents - targetContents) / -netRateW;
    }
    return double.PositiveInfinity;
  }

  // Next throttle-quantum crossing. Each 1% throttle step fires a
  // re-solve so the LP's NormalizedOutput tracks ThrottleActual as
  // the lerp evolves — otherwise the LP would only re-run at slew-
  // start and slew-end, and the displayed flow + thrust would freeze
  // for the entire 10 s slew. Quantum width is small enough that
  // step-stair stepping is invisible at 10 Hz wire emission.
  private double ForecastSlewEvent(double now) {
    if (SlewRatePerSec <= 0) return double.PositiveInfinity;
    double currentTarget = TargetThrottle();
    double currentThrottle = ThrottleActual;
    if (Math.Abs(currentThrottle - currentTarget) < 1e-9) return double.PositiveInfinity;

    double sign = currentTarget > currentThrottle ? +1 : -1;
    // Next strictly-greater (going up) or strictly-lesser (going down)
    // quantum boundary, with a small epsilon so we don't land on the
    // current value when it's already exactly on a quantum.
    double nextQuantum = sign > 0
        ? Math.Ceiling(currentThrottle / SlewQuantum + 1e-9) * SlewQuantum
        : Math.Floor  (currentThrottle / SlewQuantum - 1e-9) * SlewQuantum;
    // Clamp so we don't schedule past the target — the target itself
    // is a slew event too (lastSlewTarget reached → no more motion).
    if (sign > 0 && nextQuantum > currentTarget) nextQuantum = currentTarget;
    if (sign < 0 && nextQuantum < currentTarget) nextQuantum = currentTarget;

    double dt = Math.Abs(currentThrottle - nextQuantum) / SlewRatePerSec;
    if (dt <= 0) dt = SlewQuantum / SlewRatePerSec;  // floor — no zero-time events
    return now + dt;
  }

  // ── Save / Load ─────────────────────────────────────────────────────

  public override void Save(PartState state) {
    state.NuclearEngine = new NuclearEngineState {
      State = (int)State,
      CoreTempK = CoreTempK,
      ThrottleActual = ThrottleActual,
      ShutdownRequested = ShutdownRequested,
      Active = Active,
    };
  }

  public override void Load(PartState state) {
    if (state.NuclearEngine == null) return;
    var loaded = (ReactorState)state.NuclearEngine.State;
    // Migrate legacy Idle saves to Throttled — the new state machine
    // collapses Idle into Throttled-at-throttle-0.
    State = loaded == ReactorState.Idle ? ReactorState.Throttled : loaded;
    ThrottleActual = state.NuclearEngine.ThrottleActual;
    ShutdownRequested = state.NuclearEngine.ShutdownRequested;
    Active = state.NuclearEngine.Active;
    // PlayerThrottle is a per-tick input from NovaNuclearEngineModule
    // ApplyPlayerThrottle. Reset to 0 here so a solve between Load and
    // the next FixedUpdate doesn't drive flow against a stale value
    // from before the save.
    PlayerThrottle = 0;
    slewBaselineThrottle = ThrottleActual;
    slewBaselineUT = Vessel?.Systems?.Clock?.UT ?? 0;
    lastSlewTarget = TargetThrottle();
    pendingLoadedCoreTempK = state.NuclearEngine.CoreTempK;
    if (HeatBuffer != null) {
      HeatBuffer.Contents = Math.Max(0,
          ThermalMassJK * (state.NuclearEngine.CoreTempK - AmbientK));
    }
  }
}
