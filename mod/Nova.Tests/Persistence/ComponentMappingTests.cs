using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nova.Core.Components.Electrical;
using Nova.Core.Components.Propulsion;
using Nova.Core.Persistence;
using Nova.Core.Persistence.Protos;
using Nova.Core.Resources;
using Nova.Core.Flight;
using Nova.Core.Utils;
namespace Nova.Tests.Persistence;

[TestClass]
public class ComponentMappingTests {

  [TestMethod]
  public void TankVolume_ConstructFromStructure() {
    var structure = new TankVolumeStructure {
      Volume = 2080,
      MaxRate = 100,
      Tanks = {
        new TankStructure { Resource = "RP-1", Capacity = 1560 },
        new TankStructure { Resource = "Liquid Oxygen", Capacity = 520 },
      },
    };

    var tank = new TankVolume(structure);

    Assert.AreEqual(2080, tank.Volume);
    Assert.AreEqual(100, tank.MaxRate);
    Assert.AreEqual(2, tank.Tanks.Count);
    Assert.AreEqual("RP-1", tank.Tanks[0].Resource.Name);
    Assert.AreEqual(1560, tank.Tanks[0].Capacity);
    Assert.AreEqual(1560, tank.Tanks[0].Contents);  // defaults full
    Assert.AreEqual("Liquid Oxygen", tank.Tanks[1].Resource.Name);
    // Per-buffer rates aren't set pre-OnBuildSolver — proportioning
    // happens at solver-build time from the part-level MaxRate.
    Assert.AreEqual(0, tank.Tanks[0].MaxRateOut);
    Assert.AreEqual(0, tank.Tanks[1].MaxRateOut);
  }

  [TestMethod]
  public void TankVolume_SaveStructure_WritesToPartStructure() {
    var tank = new TankVolume { Volume = 600, MaxRate = 75 };
    tank.Tanks.Add(new Buffer { Resource = Resource.RP1, Capacity = 400 });
    tank.Tanks.Add(new Buffer { Resource = Resource.LiquidOxygen, Capacity = 200 });

    var ps = new PartStructure();
    tank.SaveStructure(ps);

    Assert.IsNotNull(ps.TankVolume);
    Assert.AreEqual(600, ps.TankVolume.Volume);
    Assert.AreEqual(75, ps.TankVolume.MaxRate);
    Assert.AreEqual(2, ps.TankVolume.Tanks.Count);
    Assert.AreEqual("RP-1", ps.TankVolume.Tanks[0].Resource);
    Assert.AreEqual(400, ps.TankVolume.Tanks[0].Capacity);
  }

  [TestMethod]
  public void TankVolume_SaveAndLoadState() {
    var tank = new TankVolume { Volume = 600 };
    tank.Tanks.Add(new Buffer { Resource = Resource.RP1, Capacity = 400, Contents = 180 });
    tank.Tanks.Add(new Buffer { Resource = Resource.LiquidOxygen, Capacity = 200, Contents = 42.5 });

    var state = new PartState();
    tank.Save(state);

    Assert.AreEqual(180, state.TankVolume.Amounts[0]);
    Assert.AreEqual(42.5, state.TankVolume.Amounts[1]);

    // Construct fresh from structure, then load state
    var ps = new PartStructure();
    tank.SaveStructure(ps);
    var restored = new TankVolume(ps.TankVolume);
    Assert.AreEqual(400, restored.Tanks[0].Contents);  // full from constructor
    restored.Load(state);
    Assert.AreEqual(180, restored.Tanks[0].Contents);
    Assert.AreEqual(42.5, restored.Tanks[1].Contents);
  }

  [TestMethod]
  public void TankVolume_LoadStructure_OverwritesPriorTanks() {
    // Models the editor → flight launch sequence: a tank is first built
    // from its prefab MODULE config (kerolox here), then NovaPartInstantiator
    // calls LoadStructure with the proto-saved structure (hydrolox after
    // the player picked Set Tank Config → LH2 + LOx). The proto must win.
    var tank = new TankVolume { Volume = 6400 };
    tank.Tanks.Add(new Buffer { Resource = Resource.RP1, Capacity = 2560, Contents = 1000 });
    tank.Tanks.Add(new Buffer { Resource = Resource.LiquidOxygen, Capacity = 3840 });

    var ps = new PartStructure {
      TankVolume = new TankVolumeStructure {
        Volume = 6400,
        Tanks = {
          new TankStructure { Resource = "Liquid Hydrogen", Capacity = 4736 },
          new TankStructure { Resource = "Liquid Oxygen", Capacity = 1664 },
        },
      },
    };
    tank.LoadStructure(ps);

    Assert.AreEqual(6400, tank.Volume);
    Assert.AreEqual(2, tank.Tanks.Count);
    Assert.AreEqual("Liquid Hydrogen", tank.Tanks[0].Resource.Name);
    Assert.AreEqual(4736, tank.Tanks[0].Capacity);
    Assert.AreEqual(4736, tank.Tanks[0].Contents);
    Assert.AreEqual("Liquid Oxygen", tank.Tanks[1].Resource.Name);
    Assert.AreEqual(1664, tank.Tanks[1].Capacity);
  }

  [TestMethod]
  public void TankVolume_FullRoundTrip() {
    var original = new TankVolume { Volume = 2080 };
    original.Tanks.Add(new Buffer { Resource = Resource.RP1, Capacity = 1560, Contents = 800 });
    original.Tanks.Add(new Buffer { Resource = Resource.LiquidOxygen, Capacity = 520, Contents = 260 });

    var ps = new PartStructure();
    original.SaveStructure(ps);
    var state = new PartState();
    original.Save(state);

    var restored = new TankVolume(ps.TankVolume);
    restored.Load(state);

    Assert.AreEqual(2080, restored.Volume);
    Assert.AreEqual(1560, restored.Tanks[0].Capacity);
    Assert.AreEqual(800, restored.Tanks[0].Contents);
    Assert.AreEqual(520, restored.Tanks[1].Capacity);
    Assert.AreEqual(260, restored.Tanks[1].Contents);
  }

  [TestMethod]
  public void Battery_ConstructFromStructure() {
    var structure = new BatteryStructure { Capacity = 500 };
    var battery = new Battery(structure);

    Assert.AreEqual(500, battery.Buffer.Capacity);
    Assert.AreEqual(500, battery.Buffer.Contents);
    Assert.AreEqual(Resource.ElectricCharge, battery.Buffer.Resource);
  }

  [TestMethod]
  public void Battery_SaveStructure_WritesToPartStructure() {
    var battery = new Battery();
    battery.Buffer.Capacity = 500;

    var ps = new PartStructure();
    battery.SaveStructure(ps);

    Assert.IsNotNull(ps.Battery);
    Assert.AreEqual(500, ps.Battery.Capacity);
  }

  [TestMethod]
  public void Battery_SaveAndLoadState() {
    var battery = new Battery();
    battery.Buffer.Capacity = 500;
    battery.Buffer.Contents = 237.5;

    var state = new PartState();
    battery.Save(state);
    Assert.AreEqual(237.5, state.Battery.Value);

    var ps = new PartStructure();
    battery.SaveStructure(ps);
    var restored = new Battery(ps.Battery);
    Assert.AreEqual(500, restored.Buffer.Contents);  // full from constructor
    restored.Load(state);
    Assert.AreEqual(237.5, restored.Buffer.Contents);
  }

  [TestMethod]
  public void PrefabOnlyComponent_NoopSaveAndLoad() {
    var engine = new Engine();
    var ps = new PartStructure();
    var state = new PartState();

    engine.SaveStructure(ps);
    engine.Save(state);
    engine.Load(state);

    // No fields set
    Assert.IsNull(ps.TankVolume);
    Assert.IsNull(ps.Battery);
    Assert.IsNull(state.TankVolume);
    Assert.IsNull(state.Battery);
  }

  [TestMethod]
  public void MultipleComponents_SamePart() {
    // A pod with both a TankVolume and a Battery
    var tank = new TankVolume { Volume = 40 };
    tank.Tanks.Add(new Buffer { Resource = Resource.Hydrazine, Capacity = 40, Contents = 30 });
    var battery = new Battery();
    battery.Buffer.Capacity = 100;
    battery.Buffer.Contents = 80;

    var ps = new PartStructure();
    var state = new PartState();

    tank.SaveStructure(ps);
    tank.Save(state);
    battery.SaveStructure(ps);
    battery.Save(state);

    Assert.IsNotNull(ps.TankVolume);
    Assert.IsNotNull(ps.Battery);
    Assert.AreEqual(40, ps.TankVolume.Volume);
    Assert.AreEqual(100, ps.Battery.Capacity);
    Assert.AreEqual(30, state.TankVolume.Amounts[0]);
    Assert.AreEqual(80, state.Battery.Value);
  }
}
