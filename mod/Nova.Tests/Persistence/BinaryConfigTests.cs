using System.Collections.Generic;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProtoBuf;
using Nova.Core.Persistence;
using Nova.Core.Persistence.Protos;
namespace Nova.Tests.Persistence;

[TestClass]
public class ProtobufTests {

  [TestMethod]
  public void Vessel_StructureAndState_RoundTrip() {
    var vessel = new Vessel {
      Structure = new VesselStructure {
        Parts = {
          new PartStructure {
            Id = 0, PartName = "mk1pod", ParentIndex = -1,
            Battery = new BatteryStructure { Capacity = 100 },
          },
          new PartStructure {
            Id = 1, PartName = "fuelTank", ParentIndex = 0,
            TankVolume = new TankVolumeStructure {
              Volume = 600,
              Tanks = {
                new TankStructure { Resource = "RP-1", Capacity = 400 },
                new TankStructure { Resource = "Liquid Oxygen", Capacity = 200 },
              },
            },
          },
        },
      },
      State = new VesselState {
        Parts = {
          new PartState { Id = 0, Battery = new BatteryState { Value = 80 } },
          new PartState { Id = 1, TankVolume = new TankVolumeState {
            Amounts = new double[] { 350, 175 },
          }},
        },
      },
    };

    using var ms = new MemoryStream();
    Serializer.Serialize(ms, vessel);
    ms.Position = 0;
    var loaded = Serializer.Deserialize<Vessel>(ms);

    // Structure
    Assert.AreEqual(2, loaded.Structure.Parts.Count);
    Assert.AreEqual("mk1pod", loaded.Structure.Parts[0].PartName);
    Assert.AreEqual(100, loaded.Structure.Parts[0].Battery.Capacity);
    Assert.IsNull(loaded.Structure.Parts[0].TankVolume);
    Assert.AreEqual(600, loaded.Structure.Parts[1].TankVolume.Volume);
    Assert.AreEqual("RP-1", loaded.Structure.Parts[1].TankVolume.Tanks[0].Resource);
    Assert.AreEqual(400, loaded.Structure.Parts[1].TankVolume.Tanks[0].Capacity);

    // State
    Assert.AreEqual(80, loaded.State.Parts[0].Battery.Value);
    Assert.AreEqual(350, loaded.State.Parts[1].TankVolume.Amounts[0]);
    Assert.AreEqual(175, loaded.State.Parts[1].TankVolume.Amounts[1]);

    Assert.IsNull(loaded.State.Flight);
  }

  [TestMethod]
  public void TankVolumeStructure_RoundTrip() {
    var structure = new TankVolumeStructure {
      Volume = 2080,
      Tanks = {
        new TankStructure { Resource = "RP-1", Capacity = 1560, MaxRateOut = 50, MaxRateIn = 100 },
        new TankStructure { Resource = "Liquid Oxygen", Capacity = 520 },
      },
    };

    using var ms = new MemoryStream();
    Serializer.Serialize(ms, structure);
    ms.Position = 0;
    var loaded = Serializer.Deserialize<TankVolumeStructure>(ms);

    Assert.AreEqual(2080, loaded.Volume);
    Assert.AreEqual(2, loaded.Tanks.Count);
    Assert.AreEqual("RP-1", loaded.Tanks[0].Resource);
    Assert.AreEqual(1560, loaded.Tanks[0].Capacity);
    Assert.AreEqual(50, loaded.Tanks[0].MaxRateOut);
    Assert.AreEqual(100, loaded.Tanks[0].MaxRateIn);
    Assert.AreEqual("Liquid Oxygen", loaded.Tanks[1].Resource);
    Assert.AreEqual(520, loaded.Tanks[1].Capacity);
  }

  [TestMethod]
  public void TankVolumeState_RoundTrip() {
    var state = new TankVolumeState {
      Amounts = new double[] { 800, 260 },
    };

    using var ms = new MemoryStream();
    Serializer.Serialize(ms, state);
    ms.Position = 0;
    var loaded = Serializer.Deserialize<TankVolumeState>(ms);

    Assert.AreEqual(2, loaded.Amounts.Length);
    Assert.AreEqual(800, loaded.Amounts[0]);
    Assert.AreEqual(260, loaded.Amounts[1]);
  }

  [TestMethod]
  public void BatteryStructure_RoundTrip() {
    var structure = new BatteryStructure { Capacity = 500 };

    using var ms = new MemoryStream();
    Serializer.Serialize(ms, structure);
    ms.Position = 0;
    var loaded = Serializer.Deserialize<BatteryStructure>(ms);

    Assert.AreEqual(500, loaded.Capacity);
  }

  [TestMethod]
  public void BatteryState_RoundTrip() {
    var state = new BatteryState { Value = 237.5 };

    using var ms = new MemoryStream();
    Serializer.Serialize(ms, state);
    ms.Position = 0;
    var loaded = Serializer.Deserialize<BatteryState>(ms);

    Assert.AreEqual(237.5, loaded.Value);
  }

  [TestMethod]
  public void PrefabOnlyPart_NoComponentStructureOrState() {
    // An engine part: prefab defines everything, nothing in structure or state
    var vessel = new Vessel {
      Structure = new VesselStructure {
        Parts = {
          new PartStructure { Id = 0, PartName = "liquidEngine_v2", ParentIndex = -1 },
        },
      },
      State = new VesselState {
        Parts = {
          new PartState { Id = 0 },
        },
      },
    };

    using var ms = new MemoryStream();
    Serializer.Serialize(ms, vessel);
    ms.Position = 0;
    var loaded = Serializer.Deserialize<Vessel>(ms);

    Assert.IsNull(loaded.Structure.Parts[0].TankVolume);
    Assert.IsNull(loaded.Structure.Parts[0].Battery);
    Assert.IsNull(loaded.State.Parts[0].TankVolume);
    Assert.IsNull(loaded.State.Parts[0].Battery);
    Assert.AreEqual(0u, loaded.State.Parts[0].Id);
  }
}
