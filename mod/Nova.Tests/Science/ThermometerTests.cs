using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nova.Core.Components;
using Nova.Core.Components.Electrical;
using Nova.Core.Components.Science;
using Nova.Core.Persistence.Protos;
using Nova.Core.Resources;
using Nova.Core.Science;
using Nova.Tests.TestHelpers;
using Buffer = Nova.Core.Resources.Buffer;

namespace Nova.Tests.Science;

[TestClass]
public class ThermometerTests {

  private static (VirtualVessel vessel, Thermometer therm, DataStorage storage) Build(
      double ecRate = 0.0075, double batteryContents = 100) {
    var battery = new Battery {
      Buffer = new Buffer {
        Resource = Resource.ElectricCharge,
        Capacity = 100, Contents = batteryContents,
        MaxRateIn = 10, MaxRateOut = 10,
      },
    };
    var therm = new Thermometer { EcRate = ecRate };
    var storage = new DataStorage { CapacityBytes = 100_000 };
    var vessel = new VirtualVessel { Context = new StubVesselContext() };
    vessel.AddPart(1, "pod", 0, new List<VirtualComponent> { battery, therm, storage });
    vessel.UpdatePartTree(new Dictionary<uint, uint?> { { 1u, null } });
    vessel.InitializeSolver(0);
    return (vessel, therm, storage);
  }

  [TestMethod]
  public void Idle_DrawsNoEC() {
    var (vessel, therm, _) = Build();
    therm.AtmActive = false;
    therm.LtsActive = false;
    vessel.Solve();
    Assert.AreEqual(0, therm.Activity, 1e-6);
    Assert.AreEqual(0, therm.ActualEcRate, 1e-9);
  }

  [TestMethod]
  public void AtmActive_DrawsAtFullSatisfaction() {
    var (vessel, therm, _) = Build();
    therm.AtmActive = true;
    vessel.Solve();
    Assert.AreEqual(1.0, therm.Activity, 1e-6);
    Assert.AreEqual(0.0075, therm.ActualEcRate, 1e-9);
  }

  [TestMethod]
  public void EmitAtmFile_DepositsToStorage() {
    var (_, therm, storage) = Build();
    therm.EmitAtmFile(new SubjectKey("atm-profile", "Kerbin", "troposphere"), 100);
    Assert.AreEqual(1, storage.Files.Count);
    Assert.AreEqual("atm-profile@Kerbin:troposphere", storage.Files[0].SubjectId);
    Assert.AreEqual(1.0, storage.Files[0].Fidelity, 1e-9);
  }

  [TestMethod]
  public void StateRoundTrip() {
    var src = new Thermometer {
      EcRate = 0.05,
      AtmActive = true,
      LtsActive = true,
      LtsSubjectId = "lts@Kerbin:SrfLanded:7",
      LtsAccumulatedSeconds = 1234.5,
      LtsLastUpdateUT = 9876,
    };
    var dst = new Thermometer { EcRate = 0.05 };
    var v = new VirtualVessel { Context = new StubVesselContext() };
    v.AddPart(1, "pod", 0, new List<VirtualComponent> { src });
    v.AddPart(2, "pod2", 0, new List<VirtualComponent> { dst });

    var pst = new PartState();
    src.Save(pst);
    dst.Load(pst);

    Assert.IsTrue(dst.AtmActive);
    Assert.IsTrue(dst.LtsActive);
    Assert.AreEqual("lts@Kerbin:SrfLanded:7", dst.LtsSubjectId);
    Assert.AreEqual(1234.5, dst.LtsAccumulatedSeconds, 1e-9);
    Assert.AreEqual(9876,   dst.LtsLastUpdateUT, 1e-9);
  }
}
