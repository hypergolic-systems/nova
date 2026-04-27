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
      Resource = Resource.RP1, Capacity = 200, Contents = 200,
    };
    var lox = new Buffer {
      Resource = Resource.LiquidOxygen, Capacity = 300, Contents = 300,
    };

    // RP-1: 200 L * 0.8 kg/L = 160 kg
    // LOX:  300 L * 1.2 kg/L = 360 kg
    // Total: 520 kg = 0.520 t
    Assert.AreEqual(0.520, FuelMassTonnes(rp1, lox), 0.001);
  }

  [TestMethod]
  public void EmptyTankHasZeroMass() {
    var rp1 = new Buffer {
      Resource = Resource.RP1, Capacity = 200, Contents = 0,
    };
    var lox = new Buffer {
      Resource = Resource.LiquidOxygen, Capacity = 300, Contents = 0,
    };

    Assert.AreEqual(0.0, FuelMassTonnes(rp1, lox));
  }

  [TestMethod]
  public void PartiallyFilledTank() {
    var rp1 = new Buffer {
      Resource = Resource.RP1, Capacity = 200, Contents = 100,
    };
    var lox = new Buffer {
      Resource = Resource.LiquidOxygen, Capacity = 300, Contents = 150,
    };

    // RP-1: 100 * 0.8 = 80 kg
    // LOX:  150 * 1.2 = 180 kg
    // Total: 260 kg = 0.260 t
    Assert.AreEqual(0.260, FuelMassTonnes(rp1, lox), 0.001);
  }

  [TestMethod]
  public void ZeroDensityResourceAddsNoMass() {
    var ec = new Buffer {
      Resource = Resource.ElectricCharge, Capacity = 1000, Contents = 1000,
    };

    Assert.AreEqual(0.0, FuelMassTonnes(ec));
  }
}
