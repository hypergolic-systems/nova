using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nova.Core.Resources;
using Nova.Core.Flight;
using Nova.Core.Utils;
namespace Nova.Tests.Components;

[TestClass]
public class TankMassTests {

  private static double FuelMassTonnes(params Buffer[] tanks) {
    var massKg = 0.0;
    foreach (var tank in tanks)
      massKg += tank.Contents * tank.Resource.Density;
    return massKg / 1000.0;
  }

  [TestMethod]
  public void FullTankHasCorrectMass() {
    var rp1 = new Buffer {
      Resource = Resource.RP1, Capacity = 390, Contents = 390,
    };
    var lox = new Buffer {
      Resource = Resource.LiquidOxygen, Capacity = 130, Contents = 130,
    };

    // RP-1: 390 L * 0.9 kg/L = 351 kg
    // LOX:  130 L * 1.1 kg/L = 143 kg
    // Total: 494 kg = 0.494 t
    Assert.AreEqual(0.494, FuelMassTonnes(rp1, lox), 0.001);
  }

  [TestMethod]
  public void EmptyTankHasZeroMass() {
    var rp1 = new Buffer {
      Resource = Resource.RP1, Capacity = 390, Contents = 0,
    };
    var lox = new Buffer {
      Resource = Resource.LiquidOxygen, Capacity = 130, Contents = 0,
    };

    Assert.AreEqual(0.0, FuelMassTonnes(rp1, lox));
  }

  [TestMethod]
  public void PartiallyFilledTank() {
    var rp1 = new Buffer {
      Resource = Resource.RP1, Capacity = 390, Contents = 195,
    };
    var lox = new Buffer {
      Resource = Resource.LiquidOxygen, Capacity = 130, Contents = 65,
    };

    // RP-1: 195 * 0.9 = 175.5 kg
    // LOX:  65 * 1.1  = 71.5 kg
    // Total: 247 kg = 0.247 t
    Assert.AreEqual(0.247, FuelMassTonnes(rp1, lox), 0.001);
  }

  [TestMethod]
  public void ZeroDensityResourceAddsNoMass() {
    var ec = new Buffer {
      Resource = Resource.ElectricCharge, Capacity = 1000, Contents = 1000,
    };

    Assert.AreEqual(0.0, FuelMassTonnes(ec));
  }
}
