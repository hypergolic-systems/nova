using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nova.Core.Components.Propulsion;
using Nova.Core.Persistence.Protos;
using Nova.Core.Resources;
namespace Nova.Tests.Components;

[TestClass]
public class TankVolumeReconfigureTests {

  [TestMethod]
  public void Reconfigure_KeepsInstanceIdentity() {
    // Critical: NovaTankModule.tankVolume and NovaPartModule.Components
    // hold the same TankVolume reference. Reconfigure must mutate Tanks
    // in place, not replace the TankVolume itself.
    var tank = new TankVolume { Volume = 6400 };
    tank.Tanks.Add(new Buffer { Resource = Resource.RP1, Capacity = 2560, Contents = 2560 });
    tank.Tanks.Add(new Buffer { Resource = Resource.LiquidOxygen, Capacity = 3840, Contents = 3840 });
    var listRef = tank.Tanks;

    tank.Reconfigure(TankPresets.GetById("hydrolox").Build(tank.Volume));

    Assert.AreSame(listRef, tank.Tanks);
  }

  [TestMethod]
  public void Reconfigure_KeroloxToHydrolox_ReplacesTankList() {
    var tank = new TankVolume { Volume = 6400 };
    tank.Tanks.Add(new Buffer { Resource = Resource.RP1, Capacity = 2560 });
    tank.Tanks.Add(new Buffer { Resource = Resource.LiquidOxygen, Capacity = 3840 });

    tank.Reconfigure(TankPresets.GetById("hydrolox").Build(tank.Volume));

    Assert.AreEqual(2, tank.Tanks.Count);
    Assert.AreEqual("Liquid Hydrogen", tank.Tanks[0].Resource.Name);
    Assert.AreEqual(4736, tank.Tanks[0].Capacity, 0.001);
    Assert.AreEqual("Liquid Oxygen", tank.Tanks[1].Resource.Name);
    Assert.AreEqual(1664, tank.Tanks[1].Capacity, 0.001);
  }

  [TestMethod]
  public void Reconfigure_PreservesTopLevelVolume() {
    var tank = new TankVolume { Volume = 2080 };
    tank.Tanks.Add(new Buffer { Resource = Resource.Hydrazine, Capacity = 2080 });

    tank.Reconfigure(TankPresets.GetById("kerolox").Build(tank.Volume));

    Assert.AreEqual(2080, tank.Volume);
  }

  [TestMethod]
  public void Reconfigure_RoundTripsThroughStructure() {
    // Reconfigure → SaveStructure → load into a fresh TankVolume:
    // the new shape must survive a proto round-trip (the path that
    // carries an editor-time mutation through .hgc into flight).
    var tank = new TankVolume { Volume = 4000 };
    tank.Tanks.Add(new Buffer { Resource = Resource.RP1, Capacity = 1600 });
    tank.Tanks.Add(new Buffer { Resource = Resource.LiquidOxygen, Capacity = 2400 });

    tank.Reconfigure(TankPresets.GetById("hydrolox").Build(tank.Volume));

    var ps = new PartStructure();
    tank.SaveStructure(ps);

    var fresh = new TankVolume();
    fresh.LoadStructure(ps);

    Assert.AreEqual(4000, fresh.Volume);
    Assert.AreEqual(2, fresh.Tanks.Count);
    Assert.AreEqual("Liquid Hydrogen", fresh.Tanks[0].Resource.Name);
    Assert.AreEqual(2960, fresh.Tanks[0].Capacity, 0.001);
    Assert.AreEqual("Liquid Oxygen", fresh.Tanks[1].Resource.Name);
    Assert.AreEqual(1040, fresh.Tanks[1].Capacity, 0.001);
  }
}
