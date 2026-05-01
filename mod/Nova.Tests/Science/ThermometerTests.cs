using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nova.Core.Components;
using Nova.Core.Components.Electrical;
using Nova.Core.Components.Science;
using Nova.Core.Persistence.Protos;
using Nova.Core.Resources;
using Buffer = Nova.Core.Resources.Buffer;

namespace Nova.Tests.Science;

[TestClass]
public class ThermometerTests {

  // Vessel with one part hosting a battery + thermometer. Used to
  // verify the thermometer's EC device behaves correctly when active vs.
  // inactive.
  private static (VirtualVessel vessel, Thermometer therm) BuildVessel(
      double ecRate = 0.0075, double batteryCapacity = 100, double batteryContents = 100) {
    var battery = new Battery {
      Buffer = new Buffer {
        Resource   = Resource.ElectricCharge,
        Capacity   = batteryCapacity,
        Contents   = batteryContents,
        MaxRateIn  = 10,
        MaxRateOut = 10,
      },
    };
    var therm = new Thermometer { EcRate = ecRate };
    var vessel = new VirtualVessel();
    vessel.AddPart(1, "pod", 0, new List<VirtualComponent> { battery, therm });
    vessel.UpdatePartTree(new Dictionary<uint, uint?> { { 1u, null } });
    vessel.InitializeSolver(0);
    return (vessel, therm);
  }

  [TestMethod]
  public void Inactive_DrawsNoEC() {
    var (vessel, therm) = BuildVessel();
    therm.IsActive = false;
    vessel.Solve();
    Assert.AreEqual(0, therm.Activity, 1e-6, "Inactive thermometer demands nothing");
    Assert.AreEqual(0, therm.ActualEcRate, 1e-9);
  }

  [TestMethod]
  public void Active_WithBattery_RunsAtFullSatisfaction() {
    var (vessel, therm) = BuildVessel();
    therm.IsActive = true;
    vessel.Solve();
    Assert.AreEqual(1.0, therm.Activity,     1e-6);
    Assert.AreEqual(1.0, therm.Satisfaction, 1e-6);
    Assert.AreEqual(0.0075, therm.ActualEcRate, 1e-9);
  }

  [TestMethod]
  public void Active_NoBattery_DrawsZero() {
    var (vessel, therm) = BuildVessel(batteryContents: 0);
    therm.IsActive = true;
    vessel.Solve();
    Assert.AreEqual(0, therm.Activity, 1e-6, "Empty battery → no EC supply → zero activity");
  }

  [TestMethod]
  public void StructureAndStateRoundTrip() {
    var src = new Thermometer {
      EcRate = 0.05,
      ActiveSubjectId = "lts@Kerbin:SrfLanded:7",
      AccumulatedActiveSeconds = 1234.5,
      LastUpdateUT = 9876,
      LastKnownSatisfaction = 0.75,
      LastKnownSituation = Nova.Core.Science.Situation.SrfLanded,
      LastKnownBody = 1,
    };

    var ps = new PartStructure();
    var pst = new PartState();
    src.SaveStructure(ps);
    src.Save(pst);

    var dst = new Thermometer();
    dst.LoadStructure(ps);
    dst.Load(pst);

    Assert.AreEqual(0.05, dst.EcRate, 1e-9);
    Assert.AreEqual("lts@Kerbin:SrfLanded:7", dst.ActiveSubjectId);
    Assert.AreEqual(1234.5, dst.AccumulatedActiveSeconds, 1e-9);
    Assert.AreEqual(9876,   dst.LastUpdateUT, 1e-9);
    Assert.AreEqual(0.75,   dst.LastKnownSatisfaction, 1e-9);
    Assert.AreEqual(Nova.Core.Science.Situation.SrfLanded, dst.LastKnownSituation);
    Assert.AreEqual(1u, dst.LastKnownBody);
  }
}
