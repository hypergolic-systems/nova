using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nova.Core.Components.Propulsion;
using Nova.Core.Resources;
namespace Nova.Tests.Components;

[TestClass]
public class TankPresetsTests {

  private static double MassKg(Buffer b) => b.Contents * b.Resource.Density;

  [TestMethod]
  public void Pure_Hydrazine_FillsEntireVolume() {
    var tanks = TankPresets.GetById("n2h4").Build(1000);
    Assert.AreEqual(1, tanks.Count);
    Assert.AreEqual("Hydrazine", tanks[0].Resource.Name);
    Assert.AreEqual(1000, tanks[0].Capacity);
    Assert.AreEqual(1000, tanks[0].Contents);
  }

  [TestMethod]
  public void Pure_RP1_FillsEntireVolume() {
    var tanks = TankPresets.GetById("rp1").Build(1000);
    Assert.AreEqual(1, tanks.Count);
    Assert.AreEqual("RP-1", tanks[0].Resource.Name);
    Assert.AreEqual(1000, tanks[0].Capacity);
  }

  [TestMethod]
  public void Pure_LOx_FillsEntireVolume() {
    var tanks = TankPresets.GetById("lox").Build(1000);
    Assert.AreEqual(1, tanks.Count);
    Assert.AreEqual("Liquid Oxygen", tanks[0].Resource.Name);
    Assert.AreEqual(1000, tanks[0].Capacity);
  }

  [TestMethod]
  public void Pure_LH2_FillsEntireVolume() {
    var tanks = TankPresets.GetById("lh2").Build(1000);
    Assert.AreEqual(1, tanks.Count);
    Assert.AreEqual("Liquid Hydrogen", tanks[0].Resource.Name);
    Assert.AreEqual(1000, tanks[0].Capacity);
  }

  [TestMethod]
  public void Kerolox_60pctLOx_40pctRP1_ByVolume() {
    var tanks = TankPresets.GetById("kerolox").Build(1000);
    Assert.AreEqual(2, tanks.Count);
    Assert.AreEqual("RP-1", tanks[0].Resource.Name);
    Assert.AreEqual(400, tanks[0].Capacity);
    Assert.AreEqual("Liquid Oxygen", tanks[1].Resource.Name);
    Assert.AreEqual(600, tanks[1].Capacity);
  }

  [TestMethod]
  public void Kerolox_MassRatio_2_25To1_LOxToRP1() {
    var tanks = TankPresets.GetById("kerolox").Build(1000);
    var rp1Mass = MassKg(tanks[0]); // 400 L * 0.8 kg/L = 320 kg
    var loxMass = MassKg(tanks[1]); // 600 L * 1.2 kg/L = 720 kg
    Assert.AreEqual(2.25, loxMass / rp1Mass, 0.001);
  }

  [TestMethod]
  public void Hydrolox_74pctLH2_26pctLOx_ByVolume() {
    var tanks = TankPresets.GetById("hydrolox").Build(1000);
    Assert.AreEqual(2, tanks.Count);
    Assert.AreEqual("Liquid Hydrogen", tanks[0].Resource.Name);
    Assert.AreEqual(740, tanks[0].Capacity);
    Assert.AreEqual("Liquid Oxygen", tanks[1].Resource.Name);
    Assert.AreEqual(260, tanks[1].Capacity);
  }

  [TestMethod]
  public void Hydrolox_MassRatio_RoughlySixToOne_LOxToLH2() {
    var tanks = TankPresets.GetById("hydrolox").Build(1000);
    var lh2Mass = MassKg(tanks[0]); // 740 L * 0.07 kg/L = 51.8 kg
    var loxMass = MassKg(tanks[1]); // 260 L * 1.2 kg/L = 312 kg
    // Engine-typical (RS-25, J-2): ~6:1 LOx:LH2 by mass.
    Assert.AreEqual(6.0, loxMass / lh2Mass, 0.05);
  }

  [TestMethod]
  public void GetById_UnknownId_ReturnsNull() {
    Assert.IsNull(TankPresets.GetById("methalox"));
    Assert.IsNull(TankPresets.GetById(""));
  }

  [TestMethod]
  public void All_Presets_DefaultFull_WithEnvelopeFlow() {
    foreach (var p in TankPresets.All) {
      foreach (var b in p.Build(1000)) {
        Assert.AreEqual(b.Capacity, b.Contents, $"{p.Id} contents != capacity");
        Assert.AreEqual(TankVolume.DefaultMaxRate, b.MaxRateIn, $"{p.Id} MaxRateIn");
        Assert.AreEqual(TankVolume.DefaultMaxRate, b.MaxRateOut, $"{p.Id} MaxRateOut");
      }
    }
  }

  [TestMethod]
  public void All_Presets_PreserveTotalVolume() {
    foreach (var p in TankPresets.All) {
      var totalCapacity = p.Build(2080).Sum(b => b.Capacity);
      Assert.AreEqual(2080, totalCapacity, 0.001, $"{p.Id} total capacity");
    }
  }
}
