using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nova.Core.Components.Propulsion;
using Nova.Core.Persistence.Protos;
using Nova.Core.Resources;
namespace Nova.Tests.Components;

[TestClass]
public class TankVolumeReconfigureTests {

  private static List<Buffer> Hydrolox4000() => new() {
    new Buffer { Resource = Resource.LiquidHydrogen, Capacity = 2960, Contents = 2960 },
    new Buffer { Resource = Resource.LiquidOxygen,   Capacity = 1040, Contents = 1040 },
  };

  [TestMethod]
  public void Reconfigure_KeepsInstanceIdentity() {
    // Critical: NovaTankModule.tankVolume and NovaPartModule.Components
    // hold the same TankVolume reference. Reconfigure must mutate Tanks
    // in place, not replace the TankVolume itself.
    var tank = new TankVolume { Volume = 4000 };
    tank.Tanks.Add(new Buffer { Resource = Resource.RP1,         Capacity = 1600, Contents = 1600 });
    tank.Tanks.Add(new Buffer { Resource = Resource.LiquidOxygen, Capacity = 2400, Contents = 2400 });
    var listRef = tank.Tanks;

    tank.Reconfigure(Hydrolox4000());

    Assert.AreSame(listRef, tank.Tanks);
  }

  [TestMethod]
  public void Reconfigure_ReplacesTankList() {
    var tank = new TankVolume { Volume = 4000 };
    tank.Tanks.Add(new Buffer { Resource = Resource.RP1,         Capacity = 1600 });
    tank.Tanks.Add(new Buffer { Resource = Resource.LiquidOxygen, Capacity = 2400 });

    tank.Reconfigure(Hydrolox4000());

    Assert.AreEqual(2, tank.Tanks.Count);
    Assert.AreEqual("Liquid Hydrogen", tank.Tanks[0].Resource.Name);
    Assert.AreEqual(2960, tank.Tanks[0].Capacity, 0.001);
    Assert.AreEqual("Liquid Oxygen",   tank.Tanks[1].Resource.Name);
    Assert.AreEqual(1040, tank.Tanks[1].Capacity, 0.001);
  }

  [TestMethod]
  public void Reconfigure_PreservesTopLevelVolume() {
    var tank = new TankVolume { Volume = 4000 };
    tank.Tanks.Add(new Buffer { Resource = Resource.Hydrazine, Capacity = 4000 });

    tank.Reconfigure(Hydrolox4000());

    Assert.AreEqual(4000, tank.Volume);
  }

  [TestMethod]
  public void Reconfigure_PreservesCustomContents() {
    // Custom mix with partial Contents (e.g. user staged an under-fueled
    // launch from the editor) must round-trip the Contents on Reconfigure.
    var tank = new TankVolume { Volume = 4000 };
    tank.Tanks.Add(new Buffer { Resource = Resource.RP1, Capacity = 1600, Contents = 1600 });

    tank.Reconfigure(new List<Buffer> {
      new Buffer { Resource = Resource.RP1,         Capacity = 1600, Contents = 800 },
      new Buffer { Resource = Resource.LiquidOxygen, Capacity = 2400, Contents = 1200 },
    });

    Assert.AreEqual(800, tank.Tanks[0].Contents, 0.001);
    Assert.AreEqual(1200, tank.Tanks[1].Contents, 0.001);
  }

  [TestMethod]
  public void Reconfigure_RoundTripsThroughStructure() {
    // Reconfigure → SaveStructure → load into a fresh TankVolume:
    // the new shape must survive a proto round-trip (the path that
    // carries an editor-time mutation through .nvc into flight).
    var tank = new TankVolume { Volume = 4000 };
    tank.Tanks.Add(new Buffer { Resource = Resource.RP1,         Capacity = 1600 });
    tank.Tanks.Add(new Buffer { Resource = Resource.LiquidOxygen, Capacity = 2400 });

    tank.Reconfigure(Hydrolox4000());

    var ps = new PartStructure();
    tank.SaveStructure(ps);

    var fresh = new TankVolume();
    fresh.LoadStructure(ps);

    Assert.AreEqual(4000, fresh.Volume);
    Assert.AreEqual(2, fresh.Tanks.Count);
    Assert.AreEqual("Liquid Hydrogen", fresh.Tanks[0].Resource.Name);
    Assert.AreEqual(2960, fresh.Tanks[0].Capacity, 0.001);
    Assert.AreEqual("Liquid Oxygen",   fresh.Tanks[1].Resource.Name);
    Assert.AreEqual(1040, fresh.Tanks[1].Capacity, 0.001);
  }
}
