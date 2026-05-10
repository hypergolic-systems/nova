using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nova.Core.Components.Control;

namespace Nova.Tests.Components.Control;

// Probe.StoredCommands ledger — pure-arithmetic tests against the lerp
// + spend interface. Vessel-less construction; CommandClockUT falls
// back to CommandBaselineUT, which the tests advance by hand to model
// time passing.
[TestClass]
public class ProbeTests {

  private static Probe MakeProbe(double capacity, double decay, double refill = 0) {
    return new Probe {
      CommandCapacityBytes  = capacity,
      CommandDecayBps       = decay,
      CommandRefillBps      = refill,
      CommandBaselineBytes  = capacity,
      CommandBaselineUT     = 0,
    };
  }

  [TestMethod]
  public void Lerp_DecayOnly_DrainsLinearly() {
    var p = MakeProbe(capacity: 1000, decay: 1);
    Assert.AreEqual(1000, p.CommandsAt(0),    1e-9);
    Assert.AreEqual( 900, p.CommandsAt(100),  1e-9);
    Assert.AreEqual(   0, p.CommandsAt(1000), 1e-9);
    // Past empty: clamped at 0, never negative.
    Assert.AreEqual(   0, p.CommandsAt(2000), 1e-9);
  }

  [TestMethod]
  public void Lerp_RefillBeyondDecay_FillsAndClampsAtCapacity() {
    var p = MakeProbe(capacity: 1000, decay: 1, refill: 11);
    // Start at capacity; net rate is +10, but the clamp pins to capacity.
    Assert.AreEqual(1000, p.CommandsAt(0),  1e-9);
    Assert.AreEqual(1000, p.CommandsAt(50), 1e-9);
  }

  [TestMethod]
  public void Lerp_RefillFromEmpty_FillsAtNetRate() {
    var p = MakeProbe(capacity: 1000, decay: 1, refill: 11);
    p.CommandBaselineBytes = 0;
    p.CommandBaselineUT    = 0;
    Assert.AreEqual( 100, p.CommandsAt(10),  1e-9);  // 0 + 10 × 10
    Assert.AreEqual(1000, p.CommandsAt(100), 1e-9);  // clamp
    Assert.AreEqual(1000, p.CommandsAt(500), 1e-9);  // still clamped
  }

  [TestMethod]
  public void TrySpend_WithinAvailable_DeductsAndReturnsTrue() {
    var p = MakeProbe(capacity: 1000, decay: 0);
    Assert.IsTrue(p.TrySpendCommands(400));
    Assert.AreEqual(600, p.CommandBaselineBytes, 1e-9);
    Assert.IsTrue(p.TrySpendCommands(600));
    Assert.AreEqual(0,   p.CommandBaselineBytes, 1e-9);
  }

  [TestMethod]
  public void TrySpend_ExceedsAvailable_RejectsAndKeepsLedger() {
    var p = MakeProbe(capacity: 1000, decay: 0);
    p.CommandBaselineBytes = 500;
    Assert.IsFalse(p.TrySpendCommands(501));
    Assert.AreEqual(500, p.CommandBaselineBytes, 1e-9);
  }

  [TestMethod]
  public void TrySpend_ZeroCost_NoOp() {
    var p = MakeProbe(capacity: 1000, decay: 0);
    Assert.IsTrue(p.TrySpendCommands(0));
    Assert.AreEqual(1000, p.CommandBaselineBytes, 1e-9);
  }

  [TestMethod]
  public void SetCommandRefillRate_RebaselinesAtCurrentClockBeforeSwapping() {
    var p = MakeProbe(capacity: 1000, decay: 1, refill: 0);
    // Decay alone for 100 s — without manual baseline advance, the
    // clock fallback equals CommandBaselineUT, so SetCommandRefillRate
    // rebaselines at UT 0 with bytes still at 1000 and then the refill
    // applies forward from there.
    p.SetCommandRefillRate(5);
    Assert.AreEqual(  5, p.CommandRefillBps,     1e-9);
    Assert.AreEqual(1000, p.CommandBaselineBytes, 1e-9);
    // New net rate +4 → +400 over 100 s, capped by capacity=1000 (we're
    // already at capacity). Confirms the rebaseline didn't drift.
    Assert.AreEqual(1000, p.CommandsAt(100), 1e-9);
  }

  [TestMethod]
  public void Clone_CopiesLedgerFields() {
    var p = MakeProbe(capacity: 1000, decay: 0.5, refill: 2);
    p.CommandBaselineBytes = 700;
    p.CommandBaselineUT    = 250;
    p.InputCostBps         = 50;
    var c = (Probe)p.Clone();
    Assert.AreEqual(p.CommandCapacityBytes,  c.CommandCapacityBytes);
    Assert.AreEqual(p.CommandDecayBps,       c.CommandDecayBps);
    Assert.AreEqual(p.CommandRefillBps,      c.CommandRefillBps);
    Assert.AreEqual(p.CommandBaselineBytes,  c.CommandBaselineBytes);
    Assert.AreEqual(p.CommandBaselineUT,     c.CommandBaselineUT);
    Assert.AreEqual(p.InputCostBps,          c.InputCostBps);
  }
}
