using System;
using Nova.Core.Persistence.Protos;
using Nova.Core.Resources;
using Nova.Core.Systems;
using Buffer = Nova.Core.Resources.Buffer;

namespace Nova.Core.Components.Electrical;

// Pu-238 radioisotope thermoelectric generator. Output decays
// exponentially with the isotope half-life, but quantized to discrete
// steps so the LP sees a piecewise-constant supply. The step duration
// is derived from a fixed fractional drop per step:
//
//   stepDur = halfLife × log₂(1 / (1 − stepDrop))
//
// At StepDropFraction = 0.001 and Pu-238's 87.7-year half-life, that's
// ~46.2 days per step. Demand is set each PreSolve to the current
// decay factor, so the device's effective supply is
// ReferencePower × (1 − stepDrop)^stepIndex.
//
// Thermal model: real RTGs convert ~6% of decay heat to electricity
// (TEG efficiency); the rest leaves as waste heat. ThermalOutput is
// the full thermal power; the EC device's ReferencePower is the
// electrical fraction. Both decay with the same factor since they're
// both Pu-driven. Heat lands in a private HeatBuffer (NOT in the LP
// pool — that would let one RTG's heat re-route into another RTG's
// buffer through proportional fill distribution). The bus sees only
// busOutletDevice as a producer; production and env dissipation are
// manual buffer updates.
public class Rtg : VirtualComponent {
  private const double StepDropFraction = 0.001;
  private const double SecondsPerDay = 86400.0;
  private const double Epsilon = 1e-9;

  // Number of discrete rejection levels between empty (R=0) and
  // saturated (R=envFactor). Re-solves fire each time the buffer
  // crosses a tier boundary — at most N during a full-range
  // transient, which is cheap.
  private const int RejectionTiers = 10;

  // Config (from prefab MODULE — populated by ComponentFactory.CreateRtg).
  public double ReferencePower;       // W (EC/s) at t = ReferenceUT
  public double HalfLifeDays;         // T½ in Earth days

  // Thermal config. Buffer is internally Joules; the device's
  // temperature is derived (T °C = Contents / ThermalMassJK,
  // implicit ambient = 0 °C). MaxOperatingTempC sets the upper limit
  // — at 100% buffer the device is at this temperature. Future work:
  // damage / explode above this. ThermalMassJK is the device's heat
  // capacity (joules per kelvin) — controls how fast temperature
  // tracks production / cooling balance.
  //
  // VacuumRejectionW / AtmRejectionW are the asymptotic passive
  // heat-rejection rates (at the saturated full-buffer state) into
  // vacuum and 1 atm respectively; lerped between by static pressure.
  // Below saturation, rejection is quantized into RejectionTiers
  // discrete levels — R_i = envFactor × i / N for tier i. The buffer
  // fraction maps to the tier via floor(f × N). Within a tier the
  // rejection rate is constant (so Buffer.Rate is too — the lerp
  // stays valid for the whole interval until the buffer crosses a
  // tier boundary, which forces a re-solve). When the equilibrium
  // tier is reached (smallest tier where R_i ≥ surplus), Buffer.Rate
  // is pinned to 0 — that's the load-bearing trick that keeps the
  // model from cycling at marginal cooling. Hysteresis falls out
  // for free: heating-up settles at the lower edge of the eq tier;
  // cooling-down settles at the upper edge. Linear-in-T is a coarse
  // approximation of Stefan-Boltzmann (real radiation goes as T⁴),
  // but the operating range of an RTG is narrow enough that the
  // error is acceptable in exchange for the simulation simplicity.
  public double ThermalOutput;        // W of total decay heat at t = ReferenceUT
  public double MaxOperatingTempC;    // upper limit, °C
  public double ThermalMassJK;        // device heat capacity, J/K
  public double MaxHeatRateOut;       // W conduction limit from buffer to bus
  public double VacuumRejectionW;     // W passive rejection at full buffer in vacuum
  public double AtmRejectionW;        // W passive rejection at full buffer at 1 atm

  // Persisted state. Sentinel 0 means "not yet anchored" — first
  // OnBuildSystems in flight pins it to current UT.
  public double ReferenceUT;

  internal Device device;             // EC producer
  internal Device busOutletDevice;    // Heat bus outlet
  public Buffer HeatBuffer;           // private — NOT in ProcessFlowSystem pool

  public double StepDurationSeconds =>
    HalfLifeDays * SecondsPerDay
      * Math.Log(1.0 / (1.0 - StepDropFraction)) / Math.Log(2.0);

  public int StepIndex(double ut) =>
    Math.Max(0, (int)Math.Floor((ut - ReferenceUT) / StepDurationSeconds));

  public double DecayFactor(double ut) =>
    Math.Pow(1.0 - StepDropFraction, StepIndex(ut));

  public double CurrentPower =>
    Vessel == null ? ReferencePower
                   : ReferencePower * DecayFactor(Vessel.Systems.Clock.UT);

  public double CurrentRate =>
    device == null ? 0.0 : device.Activity * ReferencePower;

  // Heat that lands in the buffer per tick — total decay heat minus
  // what the TEG diverts to electricity. Both decay together (the
  // efficiency ratio is roughly constant; both are Pu-driven). The
  // thermal subsystem only cares about this waste fraction; the
  // 125 W (BOL) of electrical energy exits the RTG on wires and gets
  // dissipated wherever the consumer eventually thermalizes it.
  private double WasteHeatBOL => Math.Max(0, ThermalOutput - ReferencePower);

  public double CurrentWasteHeatW =>
    Vessel == null ? WasteHeatBOL
                   : WasteHeatBOL * DecayFactor(Vessel.Systems.Clock.UT);

  public double CurrentBufferFraction =>
    HeatBuffer == null || HeatBuffer.Capacity <= Epsilon
      ? 0.0
      : HeatBuffer.Contents / HeatBuffer.Capacity;

  public double CurrentEnvFactor {
    get {
      var atm = Vessel?.Context?.StaticPressureAtm ?? 0;
      return VacuumRejectionW
           + (AtmRejectionW - VacuumRejectionW) * Math.Min(1.0, atm);
    }
  }

  // Quantized rejection: piecewise-constant function of buffer state.
  // Within a tier the rate is constant (so Buffer.Rate is too — the
  // lerp stays valid for the whole interval); at the equilibrium
  // tier we pin to surplus exactly so the math reads cleanly
  // (production − cooling − rejection = dT/dt = 0 at equilibrium).
  public double CurrentRejectionW {
    get {
      if (HeatBuffer == null || busOutletDevice == null) return 0;
      double envFactor = CurrentEnvFactor;
      double production = CurrentWasteHeatW;
      double exportRate = busOutletDevice.Activity * MaxHeatRateOut;
      double surplus = Math.Max(0, production - exportRate);

      double f = HeatBuffer.Capacity > Epsilon
        ? HeatBuffer.Contents / HeatBuffer.Capacity : 0;
      int currentTier = BufferTier(f);
      int eqTier = EquilibriumTier(surplus, envFactor);

      if (eqTier >= 0 && currentTier == eqTier) {
        // Pinned at equilibrium — display the actual flow (= surplus).
        return surplus;
      }
      return TierRejection(currentTier, envFactor);
    }
  }

  // Map a buffer fraction into a discrete rejection tier. Tier i
  // covers [i/N, (i+1)/N); the saturated tier (= N) is reserved for
  // the full-buffer clamp.
  private int BufferTier(double bufferFraction) {
    if (bufferFraction >= 1.0 - Epsilon) return RejectionTiers;
    int t = (int)Math.Floor(bufferFraction * RejectionTiers);
    if (t < 0) return 0;
    if (t > RejectionTiers - 1) return RejectionTiers - 1;
    return t;
  }

  // R_i = envFactor × i / N. Tier 0 → 0; tier N → envFactor.
  private double TierRejection(int tier, double envFactor) {
    if (tier <= 0) return 0;
    if (tier >= RejectionTiers) return envFactor;
    return envFactor * tier / (double)RejectionTiers;
  }

  // Smallest tier i where R_i ≥ surplus. Returns -1 if surplus
  // exceeds envFactor (saturated — buffer fills to full and clamps).
  private int EquilibriumTier(double surplus, double envFactor) {
    if (surplus <= Epsilon) return 0;
    if (surplus > envFactor + Epsilon) return -1;
    for (int i = 0; i <= RejectionTiers; i++) {
      if (TierRejection(i, envFactor) >= surplus) return i;
    }
    return -1;
  }

  public double CurrentExportW =>
    busOutletDevice == null ? 0.0 : busOutletDevice.Activity * MaxHeatRateOut;

  // Temperature observables. Internal storage is Joules; conversion
  // uses thermal mass (J/K). Implicit ambient is 0 °C, so T °C maps
  // to "joules above ambient / thermal mass".
  public double CurrentTempC =>
    HeatBuffer == null || ThermalMassJK <= Epsilon
      ? 0.0
      : HeatBuffer.Contents / ThermalMassJK;

  public double DTdtCps =>
    HeatBuffer == null || ThermalMassJK <= Epsilon
      ? 0.0
      : HeatBuffer.Rate / ThermalMassJK;

  public override VirtualComponent Clone() {
    var clone = (Rtg)MemberwiseClone();
    if (HeatBuffer != null) {
      clone.HeatBuffer = new Buffer {
        Resource = HeatBuffer.Resource,
        Capacity = HeatBuffer.Capacity,
        MaxRateIn = HeatBuffer.MaxRateIn,
        MaxRateOut = HeatBuffer.MaxRateOut,
      };
    }
    return clone;
  }

  public override void OnBuildSystems(VesselSystems systems, StagingFlowSystem.Node node) {
    double now = Vessel.Systems.Clock.UT;
    if (ReferenceUT == 0)
      ReferenceUT = now;

    device = systems.AddDevice(node,
        outputs: new[] { (Resource.ElectricCharge, ReferencePower) });
    device.Demand = DecayFactor(now);

    if (ThermalOutput > 0) {
      // Private heat buffer — owned by this RTG, NOT registered with
      // ProcessFlowSystem. We mirror AddBuffer's clock+baseline setup
      // manually so the lerp evaluates against the live clock.
      // Capacity (J) derives from heat capacity × max ΔT above ambient.
      HeatBuffer = new Buffer {
        Resource = Resource.Heat,
        Capacity = ThermalMassJK * MaxOperatingTempC,
        MaxRateIn = double.PositiveInfinity,
        MaxRateOut = MaxHeatRateOut,
        Clock = systems.Clock,
        BaselineUT = now,
      };

      // Bus outlet — the only LP-visible heat element on this RTG.
      // Producers + buffer drain both flow through here, capped by
      // MaxHeatRateOut. Demand-gating in OnPreSolve enforces "can only
      // sustain export at production rate when buffer is empty."
      busOutletDevice = systems.AddDevice(node,
          outputs: new[] { (Resource.Heat, MaxHeatRateOut) },
          priority: ProcessFlowSystem.Priority.Critical);
      busOutletDevice.Demand = WasteHeatBOL * DecayFactor(now) / MaxHeatRateOut;
    }
  }

  public override void OnPreSolve() {
    if (device == null) return;
    double now = Vessel.Systems.Clock.UT;
    device.Demand = DecayFactor(now);

    if (busOutletDevice != null) {
      double bufferFraction = CurrentBufferFraction;
      double production = CurrentWasteHeatW;
      busOutletDevice.Demand = bufferFraction > Epsilon
          ? 1.0
          : production / MaxHeatRateOut;
    }
  }

  public override void OnPostSolve() {
    if (device == null) return;
    double now = Vessel.Systems.Clock.UT;
    double decayBoundary =
      ReferenceUT + (StepIndex(now) + 1) * StepDurationSeconds;
    device.ValidUntil = decayBoundary;

    double busBoundary = double.PositiveInfinity;
    if (busOutletDevice != null && HeatBuffer != null) {
      double production = CurrentWasteHeatW;
      double envFactor = CurrentEnvFactor;
      double exportRate = busOutletDevice.Activity * MaxHeatRateOut;
      double surplus = Math.Max(0, production - exportRate);

      double f = HeatBuffer.Capacity > Epsilon
        ? HeatBuffer.Contents / HeatBuffer.Capacity : 0;
      int currentTier = BufferTier(f);
      int eqTier = EquilibriumTier(surplus, envFactor);

      double rate;
      if (eqTier >= 0 && currentTier == eqTier) {
        // At equilibrium tier: pin rate to 0. Buffer holds. This is
        // the load-bearing trick that prevents cycling — without it
        // the rate would flip sign at every tier crossing and we'd
        // bang between full and empty.
        rate = 0;
      } else {
        // Below or above equilibrium: rate is constant within this
        // tier. Buffer migrates toward equilibrium tier; re-solve at
        // each tier boundary updates the rate to the new tier's R.
        double rejection = TierRejection(currentTier, envFactor);
        rate = production - exportRate - rejection;
      }

      // Boundary clamps mirror Buffer.ContentsAt. At full with
      // positive rate: excess vaporizes. At empty with negative
      // rate: phantom rejection vanishes.
      bool bufferFull  = HeatBuffer.Contents >= HeatBuffer.Capacity - Epsilon;
      bool bufferEmpty = HeatBuffer.Contents <= Epsilon;
      if (bufferFull && rate > 0) rate = 0;
      if (bufferEmpty && rate < 0) rate = 0;

      HeatBuffer.Rate = rate;

      // ValidUntil: time until the buffer reaches the next tier
      // boundary at this rate. At equilibrium (rate=0) this is ∞;
      // during transient migration, each tier crossing fires a
      // re-solve and the rate updates.
      double dt = double.PositiveInfinity;
      if (rate > Epsilon) {
        double nextBoundary = (currentTier + 1) / (double)RejectionTiers;
        if (nextBoundary > 1.0) nextBoundary = 1.0;
        double targetContents = nextBoundary * HeatBuffer.Capacity;
        if (HeatBuffer.Contents < targetContents - Epsilon)
          dt = (targetContents - HeatBuffer.Contents) / rate;
        else
          dt = 0;
      } else if (rate < -Epsilon) {
        double prevBoundary = currentTier / (double)RejectionTiers;
        if (prevBoundary < 0) prevBoundary = 0;
        double targetContents = prevBoundary * HeatBuffer.Capacity;
        if (HeatBuffer.Contents > targetContents + Epsilon)
          dt = (HeatBuffer.Contents - targetContents) / -rate;
        else
          dt = 0;
      }
      busBoundary = now + dt;
      busOutletDevice.ValidUntil = busBoundary;
    }

    ValidUntil = Math.Min(decayBoundary, busBoundary);
  }

  public override void Save(PartState state) {
    state.Rtg = new RtgState { ReferenceUt = ReferenceUT };
  }

  public override void Load(PartState state) {
    if (state.Rtg == null) return;
    ReferenceUT = state.Rtg.ReferenceUt;
  }
}
