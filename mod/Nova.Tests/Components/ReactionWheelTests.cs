using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nova.Core.Components;
using Nova.Core.Components.Electrical;
using Nova.Core.Components.Propulsion;
using Nova.Core.Resources;
using Buffer = Nova.Core.Resources.Buffer;
namespace Nova.Tests.Components;

[TestClass]
public class ReactionWheelTests {

  private const double Er = 250.0;        // 5 kN·m wheel — mk1pod_v2 baseline
  private const double Capacity = 2500.0; // 10 × Er
  private const double RefillRate = 250.0; // 1 × Er

  private static ReactionWheel MakeWheel(double bufferContents = Capacity, bool refillActive = false) {
    return new ReactionWheel {
      PitchTorque = 5, YawTorque = 5, RollTorque = 5,
      ElectricRate = Er,
      RefillActive = refillActive,
      // Buffer can be primed before OnBuildSolver to override fresh-spawn full.
      Buffer = new Accumulator { Capacity = Capacity, Contents = bufferContents },
    };
  }

  private static Battery MakeBattery(double capacity, double contents) {
    return new Battery {
      Buffer = new Buffer {
        Resource = Resource.ElectricCharge,
        Capacity = capacity,
        Contents = contents,
        // Big enough to source/sink the wheel's W-scale draw without throttling.
        MaxRateIn = 1e6,
        MaxRateOut = 1e6,
      },
    };
  }

  private static VirtualVessel BuildVessel(params VirtualComponent[] components) {
    var vv = new VirtualVessel();
    vv.AddPart(1u, "test", 1.0, components.ToList());
    vv.UpdatePartTree(new Dictionary<uint, uint?> { { 1u, null } });
    vv.InitializeSolver(0);
    return vv;
  }

  // ---------- Construction + derived constants ----------

  [TestMethod]
  public void DerivedConstants_ScaleWithElectricRate() {
    var w = new ReactionWheel { ElectricRate = 750 };
    Assert.AreEqual(7500, w.BufferCapacityJoules,
        "Capacity = 10 s × ElectricRate");
    Assert.AreEqual(750, w.RefillRateWatts,
        "RefillRate = 1 × ElectricRate (burst-only)");
  }

  [TestMethod]
  public void OnBuildSolver_PrimesBufferToFull() {
    var w = new ReactionWheel { ElectricRate = Er };
    var vessel = BuildVessel(w, MakeBattery(1e6, 5e5));
    vessel.Tick(0.001);
    Assert.AreEqual(Capacity, w.Buffer.Capacity);
    Assert.AreEqual(Capacity, w.Buffer.Contents,
        "Fresh spawn should prime buffer to full");
  }

  [TestMethod]
  public void OnBuildSolver_ResyncsCapacityFromCfg() {
    // Saved buffer with stale capacity — cfg edit bumped ElectricRate.
    // OnBuildSolver should adopt the new capacity, preserving Contents.
    var w = new ReactionWheel {
      ElectricRate = 500,  // implies capacity 5000
      Buffer = new Accumulator { Capacity = 2500, Contents = 1500 },
    };
    var vessel = BuildVessel(w, MakeBattery(1e6, 5e5));
    vessel.Tick(0.001);
    Assert.AreEqual(5000, w.Buffer.Capacity);
    // Contents preserved since old contents (1500) < new capacity (5000).
    // (We don't assert exact value because the first tick's integration
    // might shave off a sliver depending on solver wiring, but it should
    // remain well below 1500 + a few ms of any plausible drift.)
    Assert.IsTrue(w.Buffer.Contents > 1499 && w.Buffer.Contents < 1501);
  }

  [TestMethod]
  public void Clone_CopiesAllState() {
    var src = new ReactionWheel {
      PitchTorque = 5, YawTorque = 5, RollTorque = 5,
      ElectricRate = Er,
      ThrottlePitch = 0.3,
      RefillActive = true,
      Buffer = new Accumulator { Capacity = Capacity, Contents = 1234 },
    };
    var dst = (ReactionWheel)src.Clone();
    Assert.AreEqual(5, dst.PitchTorque);
    Assert.AreEqual(Er, dst.ElectricRate);
    Assert.AreEqual(0.3, dst.ThrottlePitch);
    Assert.IsTrue(dst.RefillActive);
    Assert.AreEqual(Capacity, dst.Buffer.Capacity);
    Assert.AreEqual(1234, dst.Buffer.Contents);
  }

  [TestMethod]
  public void State_RoundTripsAllFields() {
    var src = new ReactionWheel {
      ElectricRate = Er,
      RefillActive = true,
      Buffer = new Accumulator { Capacity = Capacity, Contents = 1234 },
    };
    var state = new Nova.Core.Persistence.Protos.PartState();
    src.Save(state);

    var dst = new ReactionWheel { ElectricRate = Er };
    dst.Load(state);
    Assert.IsTrue(dst.RefillActive);
    Assert.AreEqual(Capacity, dst.Buffer.Capacity, "Capacity is cfg-derived, not persisted");
    Assert.AreEqual(1234, dst.Buffer.Contents);
  }

  // ---------- Buffer drain ----------

  [TestMethod]
  public void Buffer_DrainsWhileRefillOff() {
    var w = MakeWheel(bufferContents: Capacity, refillActive: false);
    w.ThrottlePitch = 1.0;  // single-axis full → 250 W draw
    var vessel = BuildVessel(w, MakeBattery(1e6, 5e5));

    vessel.Tick(0.001);  // establish initial Activities
    double initial = w.Buffer.Contents;
    w.ThrottlePitch = 1.0;  // re-set after potential SolveAttitude reset (we don't run SA)
    vessel.Tick(2.001);

    // 2 s × 250 W = 500 J drained.
    Assert.IsTrue(w.Buffer.Contents < initial,
        $"Expected drain, got {initial} → {w.Buffer.Contents}");
    Assert.IsTrue(w.Buffer.Contents > initial - 600,
        $"Drain should be ≈500 J, got Δ={initial - w.Buffer.Contents}");
  }

  // ---------- Hysteresis ----------

  [TestMethod]
  public void Hysteresis_FlipsOnAtTenPercent() {
    var w = MakeWheel(bufferContents: 0.11 * Capacity, refillActive: false);
    w.ThrottlePitch = 1.0;
    var vessel = BuildVessel(w, MakeBattery(1e6, 5e5));

    // Drain past 10% — at 250 W it takes (0.01 × 2500) / 250 = 0.1 s.
    vessel.Tick(0.001);
    w.ThrottlePitch = 1.0;
    vessel.Tick(0.5);

    Assert.IsTrue(w.RefillActive,
        $"Buffer dropped to {w.Buffer.Contents} J (={w.Buffer.FillFraction:P}); refill should be ON");
  }

  [TestMethod]
  public void Hysteresis_FlipsOffAtFull() {
    // Buffer just below full, refill ON, no drain. One tick of refill at
    // 250 W fills the gap and crosses 100%.
    var w = MakeWheel(bufferContents: 0.99 * Capacity, refillActive: true);
    w.ThrottlePitch = 0;  // no drain
    var vessel = BuildVessel(w, MakeBattery(1e6, 5e5));

    // Bridging tick to establish refill.Activity = 1.
    vessel.Tick(0.001);
    w.ThrottlePitch = 0;
    // (0.01 × 2500) / 250 = 0.1 s to fill.
    vessel.Tick(0.5);

    Assert.IsFalse(w.RefillActive,
        $"Buffer at {w.Buffer.FillFraction:P}; refill should be OFF");
  }

  [TestMethod]
  public void Hysteresis_NoFlipInBand_SkipsResolve() {
    // Buffer at 50%, refill OFF. Drain at 50 W (intensity 0.2) for 1 s
    // takes 50 J off — buffer goes 50 % → 48 %. Doesn't cross 10 %, so
    // no hysteresis flip and no extra solve.
    var w = MakeWheel(bufferContents: 0.5 * Capacity, refillActive: false);
    w.ThrottlePitch = 0.2;
    var vessel = BuildVessel(w, MakeBattery(1e6, 5e5));

    // Warmup tick + capture solve count baseline.
    vessel.Tick(0.001);
    int baselineSolves = vessel.SolveCount;

    // Now jiggle intensity across multiple ticks WITHOUT crossing a
    // threshold. Each value drains a tiny amount; nothing should
    // re-solve until the buffer hits 10% (way later).
    for (int i = 0; i < 10; i++) {
      w.ThrottlePitch = 0.1 + 0.05 * i;
      vessel.Tick(0.001 + 0.05 * (i + 1));
    }

    // Buffer should still be well above 10% — no crossings happened.
    Assert.IsTrue(w.Buffer.FillFraction > 0.45,
        $"Sanity: buffer at {w.Buffer.FillFraction:P}, didn't cross 10%");
    // The whole point of buffering: intensity changes don't re-solve.
    Assert.AreEqual(baselineSolves, vessel.SolveCount,
        "Intensity changes within hysteresis band must not re-solve LP");
  }

  [TestMethod]
  public void Hysteresis_CrossingDownTriggersResolve() {
    // Same intensity changes as above, but start near 10% so the drain
    // crosses the threshold. We expect EXACTLY ONE additional solve.
    var w = MakeWheel(bufferContents: 0.105 * Capacity, refillActive: false);
    w.ThrottlePitch = 1.0;
    var vessel = BuildVessel(w, MakeBattery(1e6, 5e5));

    vessel.Tick(0.001);
    int baselineSolves = vessel.SolveCount;
    w.ThrottlePitch = 1.0;
    vessel.Tick(1.0);  // 1 s × 250 W = 250 J — crosses 10% (at 250 J).

    Assert.IsTrue(w.RefillActive, "Should have flipped");
    Assert.IsTrue(vessel.SolveCount > baselineSolves,
        "Crossing 10% must trigger at least one re-solve");
  }

  // ---------- ValidUntil-based scheduling ----------

  [TestMethod]
  public void Forecast_TracksIntensityChange_Without_Resolve() {
    // Buffer at 50%, refill OFF. Initial intensity 0.2 → buffer drains
    // slowly, forecast says ~minutes-to-threshold. Bump intensity to 1
    // (much faster drain) — forecast should shorten without an LP solve.
    var w = MakeWheel(bufferContents: 0.5 * Capacity, refillActive: false);
    w.ThrottlePitch = 0.2;
    var vessel = BuildVessel(w, MakeBattery(1e6, 5e5));

    // Warmup gets one solve in to set refill.Activity.
    vessel.Tick(0.001);
    int baselineSolves = vessel.SolveCount;
    double slowForecast = w.RefillValidUntil;

    // New intensity, no Tick yet — forecast shouldn't have moved.
    w.ThrottlePitch = 1.0;
    Assert.AreEqual(slowForecast, w.RefillValidUntil, 1e-9,
        "Forecast updates only on Tick, not on raw intensity write");

    // Tick start refreshes the forecast against the new intensity.
    vessel.Tick(0.002);
    Assert.IsTrue(w.RefillValidUntil < slowForecast,
        $"5× drain should bring the threshold closer: was {slowForecast}, now {w.RefillValidUntil}");
    // And critically: that update did NOT cause a new solve.
    Assert.AreEqual(baselineSolves, vessel.SolveCount,
        "Forecast refresh on intensity change must not re-solve LP");
  }

  [TestMethod]
  public void Forecast_AdvancesTimeToEvent() {
    // Buffer at 50%, refill OFF, drain at 250 W (intensity 1). Forecast
    // says (40% × 2500)/250 = 4 s to hit 10%. Tick(10) should step
    // simulationTime to ~4 s, hit the threshold, flip, and continue.
    var w = MakeWheel(bufferContents: 0.5 * Capacity, refillActive: false);
    w.ThrottlePitch = 1.0;
    var vessel = BuildVessel(w, MakeBattery(1e6, 5e5));

    vessel.Tick(0.001);
    int baselineSolves = vessel.SolveCount;
    w.ThrottlePitch = 1.0;
    vessel.Tick(10);

    Assert.IsTrue(w.RefillActive,
        "Buffer should have crossed 10% within the 10 s window and flipped");
    // Exactly one extra solve from the threshold flip — verifies the
    // Tick scheduler stepped to the event rather than over-integrating
    // through it.
    Assert.AreEqual(baselineSolves + 1, vessel.SolveCount,
        $"Expected one new solve at the threshold, got {vessel.SolveCount - baselineSolves}");
  }

  [TestMethod]
  public void Forecast_LongTickInterval_HitsEvent() {
    // Background-catch-up scenario: a single Tick(60) call must still
    // step to the threshold at ~4 s and re-solve, not over-integrate
    // the whole 60 s as a starved buffer.
    var w = MakeWheel(bufferContents: 0.5 * Capacity, refillActive: false);
    w.ThrottlePitch = 1.0;
    var vessel = BuildVessel(w, MakeBattery(1e6, 5e5));

    // No warmup — single big jump from t=0.
    vessel.Tick(60);

    Assert.IsTrue(w.RefillActive, "Should have flipped during the long Tick");
    // Buffer should be filling (refill ON, intensity 1, fill = drain →
    // net 0, buffer holds at threshold). Without forecast-based stepping
    // the wheel would have been starved (Satisfaction < 1) for most of
    // the 60 s; with it, refill kicked in at the right time.
    Assert.IsTrue(w.Satisfaction > 0.99,
        $"Wheel should be powered after the flip, Satisfaction = {w.Satisfaction}");
  }

  // ---------- Satisfaction (torque-scaling) ----------

  [TestMethod]
  public void Satisfaction_FullWhenSupplyMatchesDemand() {
    // Refill ON (just-flipped), drain matches refill: 1×ER = 250W. Buffer
    // at 0 with steady refill should sustain Satisfaction = 1.
    var w = MakeWheel(bufferContents: 0, refillActive: true);
    w.ThrottlePitch = 1.0;  // 250 W desired = refill rate
    var vessel = BuildVessel(w, MakeBattery(1e6, 5e5));

    // Two ticks: first establishes refill.Activity, second exercises
    // the sustained drain.
    vessel.Tick(0.001);
    w.ThrottlePitch = 1.0;
    vessel.Tick(0.5);

    Assert.AreEqual(1.0, w.Satisfaction, 0.01,
        $"Drain matches refill, Satisfaction should be ≈1, got {w.Satisfaction}");
  }

  [TestMethod]
  public void Satisfaction_PartialWhenBusStarved() {
    // Empty buffer + battery dead → refill.Activity = 0 even with
    // RefillActive=true. Desired drain (250 W) exceeds available (0).
    // Expect Satisfaction = 0.
    var w = MakeWheel(bufferContents: 0, refillActive: true);
    w.ThrottlePitch = 1.0;
    var vessel = BuildVessel(w, MakeBattery(1e6, 0));  // empty battery

    vessel.Tick(0.001);
    w.ThrottlePitch = 1.0;
    vessel.Tick(0.5);

    Assert.AreEqual(0, w.Satisfaction, 0.01,
        $"Bus starved + empty buffer → Satisfaction = 0, got {w.Satisfaction}");
  }

  [TestMethod]
  public void Satisfaction_FullFromBufferAlone() {
    // Buffer full, refill OFF, intensity 3 (max). Buffer can sustain
    // 750 W for >3 s — far longer than a tick. Satisfaction = 1.
    var w = MakeWheel(bufferContents: Capacity, refillActive: false);
    w.ThrottlePitch = 1.0;
    w.ThrottleYaw = 1.0;
    w.ThrottleRoll = 1.0;
    var vessel = BuildVessel(w, MakeBattery(1e6, 5e5));

    vessel.Tick(0.001);
    w.ThrottlePitch = 1.0; w.ThrottleYaw = 1.0; w.ThrottleRoll = 1.0;
    vessel.Tick(0.1);

    Assert.AreEqual(1.0, w.Satisfaction, 0.01,
        $"Full buffer should serve 750 W demand, got Satisfaction = {w.Satisfaction}");
  }

  // ---------- CurrentDrain (telemetry) ----------

  [TestMethod]
  public void CurrentDrain_ReflectsActualDraw() {
    var w = MakeWheel(bufferContents: Capacity, refillActive: false);
    w.ThrottlePitch = 0.4;  // 100 W draw
    var vessel = BuildVessel(w, MakeBattery(1e6, 5e5));

    vessel.Tick(0.001);
    w.ThrottlePitch = 0.4;
    vessel.Tick(0.05);

    Assert.AreEqual(100, w.CurrentDrain, 1.0,
        $"Expected 100 W drain, got {w.CurrentDrain}");
  }

  [TestMethod]
  public void CurrentDrain_ZeroAtIdle() {
    var w = MakeWheel(bufferContents: Capacity, refillActive: false);
    w.ThrottlePitch = 0;
    var vessel = BuildVessel(w, MakeBattery(1e6, 5e5));

    vessel.Tick(0.001);
    vessel.Tick(0.05);

    Assert.AreEqual(0, w.CurrentDrain, 1e-6);
  }

  // Regression: a saved vessel that loaded with RefillActive=true and
  // a buffer just shy of capacity used to get stuck at refill=full
  // forever — the LP solved with refill.Activity=1, integration clamped
  // the buffer at capacity (silently absorbing the over-fill), and the
  // forecast `TimeToFraction(1.0, +rate)` returned +∞ because slack hit
  // exactly zero. With +∞ on every wheel, ComputeNextExpiry never
  // scheduled a re-solve, and OnPreSolve never got to flip RefillActive
  // off. Net symptom: phantom 175 W steady-state draw on idle command
  // pods, draining the battery for no torque.
  //
  // Fix: TimeToFraction returns 0 when slack==0 with non-zero netRate,
  // forcing an immediate re-solve so OnPreSolve fires.
  [TestMethod]
  public void Idle_LoadedWithRefillActive_FlipsOffShortly() {
    // Buffer just shy of capacity (sub-1% slack) + RefillActive=true,
    // mirroring a save state captured between a refill kicking on and
    // the manifold reaching 100%.
    var w = MakeWheel(bufferContents: Capacity * 0.997, refillActive: true);
    w.ThrottlePitch = 0;
    w.ThrottleRoll = 0;
    w.ThrottleYaw = 0;
    var vessel = BuildVessel(w, MakeBattery(1e6, 5e5));

    // First Tick primes the LP solve (Tick integrates before solving,
    // so the very first iteration runs with stale zero rates). Second
    // Tick exercises the actual buffer-fills-clamps-resolves cycle.
    vessel.Tick(0.001);
    vessel.Tick(1.0);

    Assert.IsFalse(w.RefillActive,
        "Buffer should reach capacity and OnPreSolve should flip RefillActive off — without the TimeToFraction(slack=0)→0 fix this stayed true forever");
    Assert.AreEqual(0, w.CurrentRefill, 1e-6,
        "After flip, refill device should report zero bus draw");
    Assert.AreEqual(Capacity, w.Buffer.Contents, 1e-6,
        "Buffer should be at capacity (clamp absorbed any over-fill)");
  }
}
