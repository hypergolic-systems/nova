using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nova.Core.Communications;
using Nova.Core.Components.Communications;

namespace Nova.Tests.Communications;

[TestClass]
public class AntennaTests {

  [TestMethod]
  public void RefSnr_MatchesClosedForm() {
    // TxPower · Gain² / (RefDistance² · N_0)
    var a = new Antenna {
      TxPower = 100, Gain = 10, MaxRate = 1000, RefDistance = 10,
    };
    var n0 = 0.5;
    var expected = 100.0 * 10 * 10 / (10.0 * 10 * n0);
    Assert.AreEqual(expected, a.RefSnr(n0), 1e-9);
  }

  [TestMethod]
  public void SelfLink_AtDesignDistance_AchievesMaxRate() {
    // A→A at exactly RefDistance: SNR == SNR_ref → ratio = 1 → rate = MaxRate.
    var a = new Antenna {
      TxPower = 100, Gain = 10, MaxRate = 1000, RefDistance = 10,
    };
    var n0 = CommunicationsParameters.NoiseFloor;
    var snrAtRef = a.TxPower * a.Gain * a.Gain / (a.RefDistance * a.RefDistance * n0);
    Assert.AreEqual(a.RefSnr(n0), snrAtRef, 1e-9);

    var ratio = Math.Log(1 + snrAtRef) / Math.Log(1 + a.RefSnr(n0));
    Assert.AreEqual(1.0, ratio, 1e-12);
  }

  [TestMethod]
  public void DegenerateAntenna_RefSnrIsZero_WhenGainZero() {
    var a = new Antenna { TxPower = 100, Gain = 0, MaxRate = 1000, RefDistance = 10 };
    Assert.AreEqual(0, a.RefSnr(1.0));
  }
}
