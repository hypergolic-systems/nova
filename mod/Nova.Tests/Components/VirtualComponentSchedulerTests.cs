using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nova.Core.Components;
using Nova.Core.Resources;

namespace Nova.Tests.Components;

[TestClass]
public class VirtualComponentSchedulerTests {

  // Records every Update(now) firing. Default ValidUntil = +Infinity =
  // "never wake me"; tests opt in by setting it.
  private class RecordingComponent : VirtualComponent {
    public List<double> Fires = new();
    public Func<double, double> NextValidUntil;

    public override void Update(double nowUT) {
      Fires.Add(nowUT);
      ValidUntil = NextValidUntil != null
        ? NextValidUntil(nowUT)
        : double.PositiveInfinity;
    }
  }

  // Minimum vessel scaffolding: one part, one component. Solver needs at
  // least one node, so we attach the component to a no-op pod.
  private static (VirtualVessel vessel, RecordingComponent cmp) BuildVessel(double t0) {
    var cmp = new RecordingComponent();
    var vessel = new VirtualVessel();
    vessel.AddPart(1, "pod", 0, new List<VirtualComponent> { cmp });
    vessel.UpdatePartTree(new Dictionary<uint, uint?> { { 1u, null } });
    vessel.InitializeSolver(t0);
    return (vessel, cmp);
  }

  [TestMethod]
  public void DefaultValidUntil_NeverFires() {
    var (vessel, cmp) = BuildVessel(0);
    vessel.Tick(1000);
    Assert.AreEqual(0, cmp.Fires.Count,
        "Component with default ValidUntil = +Infinity must never receive Update");
  }

  [TestMethod]
  public void ValidUntilElapsed_FiresOnce_NoReFireWithoutAdvance() {
    var (vessel, cmp) = BuildVessel(0);
    cmp.ValidUntil = 100;
    cmp.NextValidUntil = _ => double.PositiveInfinity; // one-shot
    vessel.Tick(1000);
    Assert.AreEqual(1, cmp.Fires.Count, "Should fire exactly once");
    Assert.AreEqual(100, cmp.Fires[0], 1e-6, "Should fire at the scheduled UT");
  }

  [TestMethod]
  public void ValidUntilAdvances_FiresOncePerExpiry() {
    var (vessel, cmp) = BuildVessel(0);
    cmp.ValidUntil = 100;
    cmp.NextValidUntil = now => now + 100; // re-arm every fire
    vessel.Tick(550);
    // Expiries at 100, 200, 300, 400, 500. The next ValidUntil after the
    // 500 fire is 600, past targetTime.
    CollectionAssert.AreEqual(
        new[] { 100.0, 200.0, 300.0, 400.0, 500.0 },
        cmp.Fires.ToArray(),
        "Should fire once per scheduled expiry");
  }

  [TestMethod]
  public void TickJumpsDirectlyToValidUntil_NoPerSecondPolling() {
    // Confirms event-driven semantics — Tick should reach ValidUntil in
    // one big jump, not by stepping every fixed-dt tick. We assert this
    // by setting a single far-future ValidUntil and observing the loop
    // doesn't iterate excessively (it'd hit MaxTickIterations and bail).
    var (vessel, cmp) = BuildVessel(0);
    cmp.ValidUntil = 9_200_000; // ~ Kerbin year
    cmp.NextValidUntil = _ => double.PositiveInfinity;
    vessel.Tick(10_000_000);
    Assert.AreEqual(1, cmp.Fires.Count);
    Assert.AreEqual(9_200_000, cmp.Fires[0], 1e-3,
        "Tick should jump directly to ValidUntil even across megaseconds");
  }

  [TestMethod]
  public void MultipleComponents_EachFiresAtOwnExpiry() {
    var a = new RecordingComponent { ValidUntil = 100, NextValidUntil = _ => double.PositiveInfinity };
    var b = new RecordingComponent { ValidUntil = 250, NextValidUntil = _ => double.PositiveInfinity };
    var c = new RecordingComponent { ValidUntil = double.PositiveInfinity }; // never

    var vessel = new VirtualVessel();
    vessel.AddPart(1, "pod", 0, new List<VirtualComponent> { a, b, c });
    vessel.UpdatePartTree(new Dictionary<uint, uint?> { { 1u, null } });
    vessel.InitializeSolver(0);

    vessel.Tick(500);

    Assert.AreEqual(1, a.Fires.Count);
    Assert.AreEqual(100, a.Fires[0], 1e-6);
    Assert.AreEqual(1, b.Fires.Count);
    Assert.AreEqual(250, b.Fires[0], 1e-6);
    Assert.AreEqual(0, c.Fires.Count);
  }

  [TestMethod]
  public void UpdateThatStaysExpired_DoesNotInfiniteLoop() {
    // Defensive: a buggy Update that fails to advance ValidUntil would
    // cause re-fires until some safety net stops it. The Tick loop's
    // MaxTickIterations cap means we get bounded fires (<= 100), not
    // a hang. The component contract still requires advancing ValidUntil;
    // this test just documents the failure mode.
    var (vessel, cmp) = BuildVessel(0);
    cmp.ValidUntil = 100;
    cmp.NextValidUntil = _ => 100; // bug: never advance
    vessel.Tick(1000);
    Assert.IsTrue(cmp.Fires.Count > 0 && cmp.Fires.Count <= 200,
        $"Bounded fire count expected (got {cmp.Fires.Count}) — Tick must not hang");
  }
}
