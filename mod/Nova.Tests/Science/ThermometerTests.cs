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
  public void WriteAtmReading_CreatesFileOnFirstCall() {
    var (_, therm, storage) = Build();
    therm.WriteAtmReading("Kerbin", "troposphere", 5_000, 100);
    Assert.AreEqual(1, storage.Files.Count);
    var f = storage.Files[0];
    Assert.AreEqual("atm-profile@Kerbin:troposphere", f.SubjectId);
    Assert.AreEqual(5_000, f.RecordedMinAltM, 1e-9);
    Assert.AreEqual(5_000, f.RecordedMaxAltM, 1e-9);
    Assert.AreEqual(0, f.Fidelity, 1e-9);  // single sample, no span yet
  }

  [TestMethod]
  public void WriteAtmReading_ExtendsBoundsOnSubsequentCalls() {
    var (_, therm, storage) = Build();
    therm.WriteAtmReading("Kerbin", "troposphere", 5_000,  100);
    therm.WriteAtmReading("Kerbin", "troposphere", 12_000, 110);
    Assert.AreEqual(1, storage.Files.Count, "second call must upsert, not duplicate");
    var f = storage.Files[0];
    Assert.AreEqual(5_000,  f.RecordedMinAltM, 1e-9);
    Assert.AreEqual(12_000, f.RecordedMaxAltM, 1e-9);
    // Span 7000 m of troposphere's effective span (1000–18000 m = 17000 m).
    Assert.AreEqual(7_000.0 / 17_000.0, f.Fidelity, 1e-9);
  }

  [TestMethod]
  public void WriteAtmReading_FullSpanYieldsFullFidelity() {
    var (_, therm, storage) = Build();
    // Surface floor is 1km — full troposphere coverage = 1000→18000 m.
    therm.WriteAtmReading("Kerbin", "troposphere",  1_000, 100);
    therm.WriteAtmReading("Kerbin", "troposphere", 18_000, 200);
    Assert.AreEqual(1.0, storage.Files[0].Fidelity, 1e-9);
  }

  [TestMethod]
  public void LayerAt_BelowSurfaceFloor_ReturnsNull() {
    // Below 1 km on an atmosphere body, no layer ⇒ no subject ⇒ no
    // file ever written. Player gets a "Surface" UI label instead.
    Assert.IsNull(AtmosphericProfileExperiment.LayerAt("Kerbin",   0));
    Assert.IsNull(AtmosphericProfileExperiment.LayerAt("Kerbin", 500));
    Assert.AreEqual("troposphere", AtmosphericProfileExperiment.LayerAt("Kerbin", 1_000));
    Assert.AreEqual("troposphere", AtmosphericProfileExperiment.LayerAt("Kerbin", 5_000));
  }

  [TestMethod]
  public void EnsureLtsFile_CreatesIdempotently() {
    var (_, therm, storage) = Build();
    var subject = new SubjectKey("lts", "Kerbin", "SrfLanded", 5);
    therm.EnsureLtsFile(subject, startUT: 1_000_000, endUT: 1_500_000, sliceDuration: 500_000);
    Assert.AreEqual(1, storage.Files.Count);
    var f = storage.Files[0];
    Assert.AreEqual(1_000_000, f.StartUt, 1e-9);
    Assert.AreEqual(1_500_000, f.EndUt, 1e-9);
    Assert.AreEqual(500_000, f.SliceDurationSeconds, 1e-9);

    // Second call leaves the existing file alone — the original
    // start_ut is the canonical "when observation began".
    therm.EnsureLtsFile(subject, startUT: 1_200_000, endUT: 1_500_000, sliceDuration: 500_000);
    Assert.AreEqual(1, storage.Files.Count);
    Assert.AreEqual(1_000_000, storage.Files[0].StartUt, 1e-9);
  }

  [TestMethod]
  public void StateRoundTrip() {
    var src = new Thermometer {
      EcRate = 0.05,
      AtmEnabled = true,
      LtsEnabled = false,
    };
    var dst = new Thermometer { EcRate = 0.05 };
    var v = new VirtualVessel { Context = new StubVesselContext() };
    v.AddPart(1, "pod", 0, new List<VirtualComponent> { src });
    v.AddPart(2, "pod2", 0, new List<VirtualComponent> { dst });

    var pst = new PartState();
    src.Save(pst);
    dst.Load(pst);

    Assert.IsTrue(dst.AtmEnabled);
    Assert.IsFalse(dst.LtsEnabled);
  }
}
