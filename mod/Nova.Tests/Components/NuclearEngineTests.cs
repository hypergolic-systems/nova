using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nova.Core.Components;
using Nova.Core.Components.Propulsion;
using Nova.Core.Resources;
using Buffer = Nova.Core.Resources.Buffer;

namespace Nova.Tests.Components;

[TestClass]
public class NuclearEngineTests {

  // Reference cfg numbers — kept in sync with
  // configs/overrides/propulsion/liquidEngineLV-N.cfg so the calibration
  // tests check the shipping balance, not a synthetic one.
  private const double ThrustKn        = 60;
  private const double IspS            = 800;
  private const double ThermalMassJK   = 125_000;
  private const double AmbientK        = 290;
  private const double ColdThresholdK  = 320;
  private const double WarmupDurationSec = 45;
  private const double IdleTempK       = 1500;
  private const double IdlePowerW      = 14_575_000;
  private const double MaxPowerW       = 300_000_000;
  private const double CoolingCoeffWK  = 3300;
  private const double InletTempK      = 20;
  private const double CpH2JKgK        = 14300;
  private const double SlewRatePerSec  = 0.1;
  private const double OperatingTempK  = 2700;
  private const double SpoolEndThrottle = 0.25;
  private const double DecayTauSeconds  = 30;

  private static NuclearEngine MakeReactor() {
    var r = new NuclearEngine();
    r.InitializeNuclear(
      thrust: ThrustKn,
      isp: IspS,
      propellant: Resource.LiquidHydrogen,
      thermalMassJK: ThermalMassJK,
      ambientK: AmbientK,
      coldThresholdK: ColdThresholdK,
      warmupDurationSec: WarmupDurationSec,
      idleTempK: IdleTempK,
      operatingTempK: OperatingTempK,
      spoolEndThrottle: SpoolEndThrottle,
      idlePowerW: IdlePowerW,
      maxPowerW: MaxPowerW,
      coolingCoeffWK: CoolingCoeffWK,
      inletTempK: InletTempK,
      cpH2JKgK: CpH2JKgK,
      slewRatePerSec: SlewRatePerSec,
      decayTauSeconds: DecayTauSeconds);
    return r;
  }

  // Big LH₂ tank wired into the vessel so the reactor's StagingFlow
  // demand always solves at full satisfaction. Capacity chosen large
  // enough that no test drains it.
  private static TankVolume MakeLh2Tank(double liters = 100_000) {
    return new TankVolume {
      Volume = liters,
      MaxRate = 10_000,
      Tanks = {
        new Buffer {
          Resource = Resource.LiquidHydrogen,
          Capacity = liters,
          Contents = liters,
        },
      },
    };
  }

  private static VirtualVessel BuildVessel(NuclearEngine reactor,
                                           TankVolume tank) {
    var vv = new VirtualVessel();
    vv.AddPart(1u, "engine", 1.0, new List<VirtualComponent> { reactor });
    vv.AddPart(2u, "tank",   1.0, new List<VirtualComponent> { tank });
    // Parent chain: tank as root, engine attached. LH₂ flows up the tree.
    vv.UpdatePartTree(new Dictionary<uint, uint?> {
      { 2u, null },
      { 1u, 2u },
    });
    vv.InitializeSolver(0);
    return vv;
  }

  // ─── Initialization / round-trip ────────────────────────────────────

  [TestMethod]
  public void IdleCoolingFlow_DerivedFromHeatBalance() {
    var r = MakeReactor();
    // Idle balance: idlePower = mdot × cp × (T_idle − T_inlet) + rad × (T_idle − T_amb)
    // → mdot = (idlePower − rad × ΔT_amb) / (cp × ΔT_inlet)
    double expected = (IdlePowerW - CoolingCoeffWK * (IdleTempK - AmbientK))
                    / (CpH2JKgK * (IdleTempK - InletTempK));
    Assert.AreEqual(expected, r.IdleCoolingFlowKgs, 1e-6);
  }

  [TestMethod]
  public void MaxFlow_DerivedFromThrustAndIsp() {
    var r = MakeReactor();
    const double G0 = 9.80665;
    double expected = ThrustKn * 1000 / (IspS * G0);  // kg/s
    Assert.AreEqual(expected, r.MaxFlowKgs, 1e-6);
  }

  [TestMethod]
  public void DefaultsToCold_NoFlow_NoThrust() {
    var r = MakeReactor();
    var v = BuildVessel(r, MakeLh2Tank());
    v.Tick(0.1);
    Assert.AreEqual(ReactorState.Cold, r.State);
    Assert.AreEqual(0, r.ComputeFlowDemandKgs(), 1e-9);
    Assert.AreEqual(0, r.ThrustOutputFraction, 1e-9);
    Assert.IsFalse(r.Ignited);
  }

  // Regression: a Cold reactor sitting on the pad must NOT schedule a
  // re-solve at `now` every tick. Pre-fix, ComputeThermalRateAndEvent
  // fell through to the tier-based branch with Contents=0, computed a
  // spurious −82 kW "radiative cooling" rate, and returned dt = 0
  // (target contents == current contents) → ValidUntil = now → the
  // VirtualVessel loop spun to its 100-iter cap every FixedUpdate.
  // In-game this showed as continuous "Tick() exceeded 100 iterations"
  // log spam and visible 4× slowdown on a vessel that should have been
  // free-coasting at full speed.
  [TestMethod]
  public void Cold_DoesNotScheduleNowExpiry_AcrossManyTicks() {
    var r = MakeReactor();
    var v = BuildVessel(r, MakeLh2Tank());
    // Mirror the in-flight cadence: 200 FixedUpdates × 0.02s = 4s.
    // The bug fired 100 iters per call, so any spin shows up as
    // SolveCount blowing past the tick count by ~100×.
    int beforeSolves = v.SolveCount;
    for (int i = 0; i < 200; i++) v.Tick((i + 1) * 0.02);
    int solves = v.SolveCount - beforeSolves;
    Assert.AreEqual(ReactorState.Cold, r.State, "reactor must stay Cold");
    Assert.IsTrue(solves < 10,
        $"Cold reactor should solve at most a handful of times across 200 ticks " +
        $"(no scheduled events); got {solves} solves — loop is spinning.");
  }

  [TestMethod]
  public void SaveLoad_RoundTripsAllFields() {
    var src = MakeReactor();
    src.State = ReactorState.Throttled;
    src.ThrottleActual = 0.7;
    src.ShutdownRequested = true;
    // Compose a state with a hot core; CoreTempK feeds into HeatBuffer
    // on load via the pending-load shim, then OnBuildSystems primes it.
    var srcState = new Nova.Core.Persistence.Protos.PartState();
    // Fake HeatBuffer so CoreTempK reports a real value at Save time.
    src.HeatBuffer = new Buffer {
      Resource = Resource.Heat,
      Capacity = double.PositiveInfinity,
      BaselineContents = ThermalMassJK * (1200 - AmbientK),
    };
    src.Save(srcState);

    Assert.AreEqual((int)ReactorState.Throttled, srcState.NuclearEngine.State);
    Assert.AreEqual(0.7, srcState.NuclearEngine.ThrottleActual, 1e-9);
    Assert.IsTrue(srcState.NuclearEngine.ShutdownRequested);
    Assert.AreEqual(1200, srcState.NuclearEngine.CoreTempK, 1e-6);

    var dst = MakeReactor();
    dst.Load(srcState);
    Assert.AreEqual(ReactorState.Throttled, dst.State);
    Assert.AreEqual(0.7, dst.ThrottleActual, 1e-9);
    Assert.IsTrue(dst.ShutdownRequested);
    // CoreTempK is loaded after OnBuildSystems via the pending-load
    // shim; without a vessel build we round-trip it on load by stashing
    // into the heat buffer when present.
  }

  // ─── State-machine transitions ──────────────────────────────────────

  [TestMethod]
  public void SetReactorActive_True_ColdToWarming() {
    var r = MakeReactor();
    var v = BuildVessel(r, MakeLh2Tank());
    v.Tick(0.1);
    Assert.IsTrue(r.SetReactorActive(true));
    Assert.AreEqual(ReactorState.Warming, r.State);
  }

  [TestMethod]
  public void SetReactorActive_True_NoOpWhileWarmingOrThrottled() {
    var r = MakeReactor();
    var v = BuildVessel(r, MakeLh2Tank());
    v.Tick(0.1);
    r.SetReactorActive(true);
    // Already Warming — re-activating shouldn't re-trigger.
    bool changed = r.SetReactorActive(true);
    Assert.IsFalse(changed);
    Assert.AreEqual(ReactorState.Warming, r.State);
  }

  [TestMethod]
  public void SetReactorActive_True_FromCooling_RestartsWarmup() {
    // Player started a shutdown mid-mission and changed their mind
    // while still cooling — toggling on should pick up where we are
    // and bring the reactor back to operating temp.
    var r = MakeReactor();
    var v = BuildVessel(r, MakeLh2Tank());
    v.Tick(0.1);
    r.SetReactorActive(true);
    v.Invalidate();
    v.Tick(200);
    Assert.AreEqual(ReactorState.Throttled, r.State);
    r.SetReactorActive(false);
    // Throttled-at-zero with the shutdown latch fires the
    // transition on the next tick (slew is already at 0).
    v.Invalidate();
    v.Tick(200.5);
    Assert.AreEqual(ReactorState.Cooling, r.State);

    // Mid-cooldown — toggle back on. State should leave Cooling.
    Assert.IsTrue(r.SetReactorActive(true));
    Assert.AreEqual(ReactorState.Warming, r.State);
    // Core is still hot from before the shutdown, so the
    // Warming → Throttled transition fires almost immediately.
    v.Invalidate();
    v.Tick(201);
    Assert.AreEqual(ReactorState.Throttled, r.State);
  }

  [TestMethod]
  public void SetReactorActive_True_CancelsPendingShutdown() {
    var r = MakeReactor();
    var v = BuildVessel(r, MakeLh2Tank());
    v.Tick(0.1);
    r.SetReactorActive(true);
    v.Invalidate();
    v.Tick(200);
    r.PlayerThrottle = 0.5;
    v.Invalidate();
    v.Tick(205);
    Assert.AreEqual(ReactorState.Throttled, r.State);

    // Queue shutdown while throttled.
    Assert.IsTrue(r.SetReactorActive(false));
    Assert.IsTrue(r.ShutdownRequested);

    // Player changes mind — toggle back on clears the latch, state
    // stays Throttled.
    Assert.IsTrue(r.SetReactorActive(true));
    Assert.IsFalse(r.ShutdownRequested);
    Assert.AreEqual(ReactorState.Throttled, r.State);
  }

  [TestMethod]
  public void Warming_ToThrottled_AtWarmupThreshold() {
    var r = MakeReactor();
    var v = BuildVessel(r, MakeLh2Tank());
    v.Tick(0.1);
    r.SetReactorActive(true);
    v.Invalidate();
    // Forced linear ramp completes at WarmupDurationSec; transition
    // to Throttled fires at end-of-ramp with T pinned at IdleTempK.
    v.Tick(200);
    Assert.AreEqual(ReactorState.Throttled, r.State);
    Assert.AreEqual(IdleTempK, r.CoreTempK, 5,
        $"CoreTempK = {r.CoreTempK:F1} should land at IdleTempK");
  }

  [TestMethod]
  public void WarmupDuration_HitsTargetWithinTolerance() {
    var r = MakeReactor();
    var v = BuildVessel(r, MakeLh2Tank());
    v.Tick(0.1);
    r.SetReactorActive(true);
    v.Invalidate();
    v.Tick(120);
    Assert.AreEqual(ReactorState.Throttled, r.State);
    // Throttled should have been entered between 50 and 90 s — generous
    // tolerance around the modelled 64 s.
    Assert.IsTrue(r.Vessel.Systems.Clock.UT > 50,
        "Warmup should take real time, not snap on first tick");
  }

  [TestMethod]
  public void Throttled_AfterWarmup_StartsAtZeroThrottle() {
    // The merged-Idle model — once Warming completes, we're in
    // Throttled but the player hasn't asked for thrust yet, so
    // ThrottleActual should be 0.
    var r = MakeReactor();
    var v = BuildVessel(r, MakeLh2Tank());
    v.Tick(0.1);
    r.SetReactorActive(true);
    v.Invalidate();
    v.Tick(200);
    Assert.AreEqual(ReactorState.Throttled, r.State);
    Assert.AreEqual(0.0, r.ThrottleActual, 1e-6);
  }

  [TestMethod]
  public void Throttled_PlayerThrottle_DrivesSlewTarget() {
    var r = MakeReactor();
    var v = BuildVessel(r, MakeLh2Tank());
    v.Tick(0.1);
    r.SetReactorActive(true);
    v.Invalidate();
    v.Tick(200);
    Assert.AreEqual(ReactorState.Throttled, r.State);

    r.PlayerThrottle = 0.5;
    v.Invalidate();
    // After ~5 s the slew (rate=0.1/s) should be at half-way to 0.5.
    v.Tick(202.5);
    Assert.AreEqual(0.25, r.ThrottleActual, 0.05);
  }

  [TestMethod]
  public void Throttled_SlewsActualThrottleTowardSetpoint() {
    var r = MakeReactor();
    var v = BuildVessel(r, MakeLh2Tank());
    v.Tick(0.1);
    r.SetReactorActive(true);
    v.Invalidate();
    v.Tick(200);
    r.PlayerThrottle = 1.0;
    v.Invalidate();
    // After 5 s of sim, slew should be at 50 % (rate = 0.1/s).
    v.Tick(205);
    Assert.AreEqual(0.5, r.ThrottleActual, 0.05);

    // After 11 more s of sim, slew should be saturated at 1.0.
    v.Tick(216);
    Assert.AreEqual(1.0, r.ThrottleActual, 1e-3);
  }

  [TestMethod]
  public void Throttled_LowThrottle_StaysInThrottled() {
    // With the merged-Idle model, a low throttle is still the
    // Throttled state — there's no auto-transition out. The reactor
    // sits at idle equilibrium with whatever idle flow + zero thrust
    // (gated by ThrustGateThrottle).
    var r = MakeReactor();
    var v = BuildVessel(r, MakeLh2Tank());
    v.Tick(0.1);
    r.SetReactorActive(true);
    v.Invalidate();
    v.Tick(200);

    r.PlayerThrottle = 0.0;
    v.Invalidate();
    v.Tick(230);
    Assert.AreEqual(ReactorState.Throttled, r.State,
        "No Idle state — Throttled covers the entire engaged envelope.");
    Assert.AreEqual(0.0, r.ThrottleActual, 1e-3);
  }

  [TestMethod]
  public void SetReactorActive_False_FromThrottledAtZero_GoesToCooling() {
    // From Throttled at throttle=0 (the "Idle" UI state), the
    // shutdown latch fires and the auto-sequence transitions to
    // Cooling on the next tick (slew is already at 0).
    var r = MakeReactor();
    var v = BuildVessel(r, MakeLh2Tank());
    v.Tick(0.1);
    r.SetReactorActive(true);
    v.Invalidate();
    v.Tick(200);
    Assert.AreEqual(ReactorState.Throttled, r.State);
    Assert.AreEqual(0.0, r.ThrottleActual, 1e-6);

    Assert.IsTrue(r.SetReactorActive(false));
    Assert.IsTrue(r.ShutdownRequested);
    v.Invalidate();
    v.Tick(200.5);
    Assert.AreEqual(ReactorState.Cooling, r.State);
    Assert.IsFalse(r.ShutdownRequested);
  }

  [TestMethod]
  public void SetReactorActive_False_FromThrottled_LatchesShutdown() {
    var r = MakeReactor();
    var v = BuildVessel(r, MakeLh2Tank());
    v.Tick(0.1);
    r.SetReactorActive(true);
    v.Invalidate();
    v.Tick(200);
    r.PlayerThrottle = 0.5;
    v.Invalidate();
    v.Tick(205);
    Assert.AreEqual(ReactorState.Throttled, r.State);

    Assert.IsTrue(r.SetReactorActive(false));
    // Still Throttled but ShutdownRequested latched.
    Assert.AreEqual(ReactorState.Throttled, r.State);
    Assert.IsTrue(r.ShutdownRequested);

    // Auto-sequence: TargetThrottle is 0; once slew lands at 0 the
    // shutdown latch fires Throttled→Cooling in the same tick.
    v.Invalidate();
    v.Tick(225);  // 20 s — enough for 0.5 → 0 slew (5 s) plus margin
    Assert.AreEqual(ReactorState.Cooling, r.State);
    Assert.IsFalse(r.ShutdownRequested,
        "Latch should clear when consumed by the Throttled→Cooling transition");
  }

  [TestMethod]
  public void Cooling_EventuallyReachesCold() {
    var r = MakeReactor();
    var v = BuildVessel(r, MakeLh2Tank());
    v.Tick(0.1);
    r.SetReactorActive(true);
    v.Invalidate();
    v.Tick(200);
    r.SetReactorActive(false);
    v.Invalidate();
    // Cooling phase now has decay heat: ~150 s for decay to fall
    // below the radiative-absorbable floor, then ~4τ_rad (≈ 250 s)
    // for radiative cooling to drop T from IdleTempK to
    // ColdThresholdK. Allow ~800 s of sim margin.
    v.Tick(1000);
    Assert.AreEqual(ReactorState.Cold, r.State);
    Assert.IsTrue(r.CoreTempK <= ColdThresholdK + 5,
        $"CoreTempK = {r.CoreTempK:F1} should be ≤ {ColdThresholdK}");
  }

  [TestMethod]
  public void Cooling_DecayHeatDrivesFlow() {
    // Just after entering Cooling the decay heat is still high
    // (starts at IdlePowerW), so the LH₂ valve should hold the core
    // at IdleTempK — flow demand must be positive.
    var r = MakeReactor();
    var v = BuildVessel(r, MakeLh2Tank());
    v.Tick(0.1);
    r.SetReactorActive(true);
    v.Invalidate();
    v.Tick(200);
    r.SetReactorActive(false);
    v.Invalidate();
    v.Tick(205);
    Assert.AreEqual(ReactorState.Cooling, r.State);
    Assert.IsTrue(r.ComputeFlowDemandKgs() > 0,
        "Decay heat just after entering Cooling needs active flow");
  }

  [TestMethod]
  public void Cooling_DecayHeatDecaysOverTime() {
    // Power schedule is monotonic-decreasing during Cooling.
    var r = MakeReactor();
    var v = BuildVessel(r, MakeLh2Tank());
    v.Tick(0.1);
    r.SetReactorActive(true);
    v.Invalidate();
    v.Tick(200);
    r.SetReactorActive(false);
    v.Invalidate();
    v.Tick(202);
    double p0 = r.FissionPowerW();
    Assert.IsTrue(p0 > 1e6, $"Decay heat should start at ~IdlePower, got {p0:F0} W");
    v.Tick(232);  // +30 s (≈ 1 τ)
    double p1 = r.FissionPowerW();
    Assert.IsTrue(p1 < p0,
        $"Decay heat should drop over time: p0={p0:F0} → p1={p1:F0}");
    Assert.IsTrue(p1 < p0 * 0.5,
        $"After 1 τ decay heat should fall to ~1/e of start; p0={p0:F0}, p1={p1:F0}");
  }

  // ─── Thrust gating ──────────────────────────────────────────────────

  [TestMethod]
  public void ThrustOutputFraction_ZeroAtZeroThrottle() {
    // Throttled at throttle=0 (the "Idle" UI state) — thrust gated to
    // zero by ThrustGateThrottle. Flow demand is still non-zero
    // (idle cooling at 0.5 kg/s).
    var r = MakeReactor();
    var v = BuildVessel(r, MakeLh2Tank());
    v.Tick(0.1);
    r.SetReactorActive(true);
    v.Invalidate();
    v.Tick(200);
    Assert.AreEqual(ReactorState.Throttled, r.State);
    Assert.AreEqual(0.0, r.ThrottleActual, 1e-6);
    Assert.AreEqual(0, r.ThrustOutputFraction, 1e-9);
    Assert.IsTrue(r.ComputeFlowDemandKgs() > 0,
        "Throttle=0 should still demand cooling LH₂ flow");
  }

  [TestMethod]
  public void ThrustOutputFraction_ZeroDuringWarming() {
    var r = MakeReactor();
    var v = BuildVessel(r, MakeLh2Tank());
    v.Tick(0.1);
    r.SetReactorActive(true);
    v.Invalidate();
    v.Tick(10);  // partway through warmup
    Assert.AreEqual(ReactorState.Warming, r.State);
    Assert.AreEqual(0, r.ThrustOutputFraction, 1e-9);
  }

  [TestMethod]
  public void ThrustOutputFraction_TracksThrottleAtFullPower() {
    var r = MakeReactor();
    var v = BuildVessel(r, MakeLh2Tank());
    v.Tick(0.1);
    r.SetReactorActive(true);
    v.Invalidate();
    v.Tick(200);
    r.PlayerThrottle = 1.0;
    v.Invalidate();
    // 10 s slew at 0.1/s + 2 s margin for heat balance to settle to
    // 2700 K equilibrium at full throttle. Long enough that
    // NormalizedOutput should be saturated and core at op temp.
    v.Tick(230);
    Assert.AreEqual(ReactorState.Throttled, r.State);
    Assert.AreEqual(1.0, r.ThrottleActual, 1e-2);
    // Full throttle, core at OperatingTempK → √(T/T_op) ≈ 1, and
    // NormalizedOutput ≈ 1 (LP fully supplied). ThrustOutputFraction
    // = NormalizedOutput × √(T/T_op) ≈ 1.
    Assert.IsTrue(r.ThrustOutputFraction > 0.9,
        $"Throttled ThrustOutputFraction = {r.ThrustOutputFraction:F3}");
  }

  // ─── Engine map status mapping ──────────────────────────────────────

  [TestMethod]
  public void EngineStatus_Cold_ReportsShutdown() {
    var r = MakeReactor();
    Assert.AreEqual((byte)3, r.EngineStatus);
  }

  [TestMethod]
  public void EngineStatus_Warming_ReportsIdle() {
    var r = MakeReactor();
    r.State = ReactorState.Warming;
    Assert.AreEqual((byte)4, r.EngineStatus);
  }

  [TestMethod]
  public void EngineStatus_Throttled_WithThrust_ReportsBurning() {
    var r = MakeReactor();
    var v = BuildVessel(r, MakeLh2Tank());
    v.Tick(0.1);
    r.SetReactorActive(true);
    v.Invalidate();
    v.Tick(200);
    r.PlayerThrottle = 1.0;
    v.Invalidate();
    v.Tick(216);
    Assert.AreEqual((byte)0, r.EngineStatus);
  }

  // ─── Clone ──────────────────────────────────────────────────────────

  [TestMethod]
  public void Clone_PreservesTypeAndState() {
    var src = MakeReactor();
    src.State = ReactorState.Throttled;
    src.ThrottleActual = 0.6;
    src.PlayerThrottle = 0.8;
    src.ShutdownRequested = true;
    var dst = src.Clone();
    Assert.IsInstanceOfType(dst, typeof(NuclearEngine));
    var dn = (NuclearEngine)dst;
    Assert.AreEqual(ReactorState.Throttled, dn.State);
    Assert.AreEqual(0.6, dn.ThrottleActual, 1e-9);
    Assert.AreEqual(0.8, dn.PlayerThrottle, 1e-9);
    Assert.IsTrue(dn.ShutdownRequested);
    Assert.AreEqual(ThermalMassJK, dn.ThermalMassJK);
    Assert.AreEqual(MaxPowerW, dn.MaxPowerW);
    Assert.AreEqual(1, dn.Propellants.Count);
    Assert.AreEqual(Resource.LiquidHydrogen, dn.Propellants[0].Resource);
  }
}
