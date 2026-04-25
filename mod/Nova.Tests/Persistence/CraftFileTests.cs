using System.Collections.Generic;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProtoBuf;
using Nova.Core.Persistence;
using Nova.Core.Persistence.Protos;
namespace Nova.Tests.Persistence;

[TestClass]
public class CraftFileTests {

  /// <summary>
  /// Build a basic rocket: mk1pod → decoupler → fuelTank (T400) → LV-T30.
  /// Craft file: structure + state, no flight state.
  /// </summary>
  static Vessel BuildBasicRocket() {
    return new Vessel {
      Structure = new VesselStructure {
        Parts = {
          new PartStructure {
            Id = 0, PartName = "mk1pod_v2", ParentIndex = -1,
            TankVolume = new TankVolumeStructure {
              Volume = 40,
              Tanks = {
                new TankStructure { Resource = "Hydrazine", Capacity = 40 },
              },
            },
          },
          new PartStructure {
            Id = 1, PartName = "Decoupler_1", ParentIndex = 0,
          },
          new PartStructure {
            Id = 2, PartName = "fuelTank", ParentIndex = 1,
            TankVolume = new TankVolumeStructure {
              Volume = 2080,
              Tanks = {
                new TankStructure { Resource = "RP-1", Capacity = 1560 },
                new TankStructure { Resource = "Liquid Oxygen", Capacity = 520 },
              },
            },
          },
          new PartStructure {
            Id = 3, PartName = "liquidEngine_v2", ParentIndex = 2,
          },
        },
      },
      State = new VesselState {
        Parts = {
          new PartState { Id = 0, TankVolume = new TankVolumeState {
            Amounts = new double[] { 40 },
          }},
          new PartState { Id = 1 },
          new PartState { Id = 2, TankVolume = new TankVolumeState {
            Amounts = new double[] { 1560, 520 },
          }},
          new PartState { Id = 3 },
        },
      },
    };
  }

  [TestMethod]
  public void CraftFile_RoundTrip() {
    var vessel = BuildBasicRocket();

    byte[] bytes;
    using (var ms = new MemoryStream()) {
      Serializer.Serialize(ms, vessel);
      bytes = ms.ToArray();
    }

    Vessel loaded;
    using (var ms = new MemoryStream(bytes)) {
      loaded = Serializer.Deserialize<Vessel>(ms);
    }

    Assert.IsNull(loaded.State.Flight);
    Assert.IsNull(loaded.StructureHash);

    var parts = loaded.Structure.Parts;
    Assert.AreEqual(4, parts.Count);
    Assert.AreEqual("mk1pod_v2", parts[0].PartName);
    Assert.AreEqual(-1, parts[0].ParentIndex);
    Assert.AreEqual(40, parts[0].TankVolume.Volume);
    Assert.IsNull(parts[1].TankVolume); // decoupler: prefab-only
    Assert.AreEqual(2080, parts[2].TankVolume.Volume);
    Assert.AreEqual("RP-1", parts[2].TankVolume.Tanks[0].Resource);
    Assert.AreEqual(1560, parts[2].TankVolume.Tanks[0].Capacity);
    Assert.IsNull(parts[3].TankVolume); // engine: prefab-only

    // State
    Assert.AreEqual(1560, loaded.State.Parts[2].TankVolume.Amounts[0]);
    Assert.AreEqual(520, loaded.State.Parts[2].TankVolume.Amounts[1]);
  }

  [TestMethod]
  public void CraftFile_PartiallyFilledTanks() {
    var vessel = new Vessel {
      Structure = new VesselStructure {
        Parts = {
          new PartStructure {
            Id = 0, PartName = "tankPart", ParentIndex = -1,
            TankVolume = new TankVolumeStructure {
              Volume = 600,
              Tanks = {
                new TankStructure { Resource = "Liquid Oxygen", Capacity = 200 },
                new TankStructure { Resource = "Hydrazine", Capacity = 200 },
                new TankStructure { Resource = "RP-1", Capacity = 200 },
              },
            },
          },
        },
      },
      State = new VesselState {
        Parts = {
          new PartState { Id = 0, TankVolume = new TankVolumeState {
            Amounts = new double[] { 200, 0, 150 },  // full, empty, partial
          }},
        },
      },
    };

    byte[] bytes;
    using (var ms = new MemoryStream()) {
      Serializer.Serialize(ms, vessel);
      bytes = ms.ToArray();
    }

    Vessel loaded;
    using (var ms = new MemoryStream(bytes)) {
      loaded = Serializer.Deserialize<Vessel>(ms);
    }

    var amounts = loaded.State.Parts[0].TankVolume.Amounts;
    Assert.AreEqual(200, amounts[0]);
    Assert.AreEqual(0, amounts[1]);
    Assert.AreEqual(150, amounts[2]);
  }

  [TestMethod]
  public void SaveFile_WithFlightState() {
    var vessel = BuildBasicRocket();

    // Assign real persistent IDs
    vessel.Structure.Parts[0].Id = 1001;
    vessel.Structure.Parts[1].Id = 1002;
    vessel.Structure.Parts[2].Id = 1003;
    vessel.Structure.Parts[3].Id = 1004;
    vessel.State.Parts[0].Id = 1001;
    vessel.State.Parts[1].Id = 1002;
    vessel.State.Parts[2].Id = 1003;
    vessel.State.Parts[3].Id = 1004;

    // Drain the main tank mid-flight
    vessel.State.Parts[2].TankVolume.Amounts = new double[] { 800, 260 };

    // Vessel identity (structure) and state
    vessel.Structure.VesselId = "abc-123";
    vessel.Structure.PersistentId = 5000;
    vessel.State.Name = "My Rocket";
    vessel.State.Situation = 1;
    vessel.State.VesselType = 0;

    // Add flight state
    vessel.State.Flight = new FlightState {
      Orbit = new OrbitalState {
        SemiMajorAxis = 700000,
        Eccentricity = 0.01,
        Inclination = 0,
        BodyIndex = 1,
      },
      Position = new PositionState {
        Latitude = -0.09,
        Longitude = -74.5,
        Altitude = 100000,
      },
    };

    var save = new SaveFile {
      UniversalTime = 12345.67,
      ActiveVesselIndex = 0,
      Vessels = { vessel },
    };

    byte[] bytes;
    using (var ms = new MemoryStream()) {
      Serializer.Serialize(ms, save);
      bytes = ms.ToArray();
    }
    System.Console.WriteLine($"Serialized save size: {bytes.Length} bytes");

    SaveFile loaded;
    using (var ms = new MemoryStream(bytes)) {
      loaded = Serializer.Deserialize<SaveFile>(ms);
    }

    Assert.AreEqual(12345.67, loaded.UniversalTime);
    var v = loaded.Vessels[0];
    Assert.IsNotNull(v.State.Flight);
    Assert.AreEqual("My Rocket", v.State.Name);
    Assert.AreEqual("abc-123", v.Structure.VesselId);
    Assert.AreEqual(5000u, v.Structure.PersistentId);
    Assert.AreEqual(700000, v.State.Flight.Orbit.SemiMajorAxis);
    Assert.AreEqual(1003u, v.Structure.Parts[2].Id);

    // Drained tanks
    Assert.AreEqual(800, v.State.Parts[2].TankVolume.Amounts[0]);
    Assert.AreEqual(260, v.State.Parts[2].TankVolume.Amounts[1]);
  }

  [TestMethod]
  public void CraftFile_SerializedSize() {
    var vessel = BuildBasicRocket();

    using var ms = new MemoryStream();
    Serializer.Serialize(ms, vessel);

    System.Console.WriteLine($"Serialized craft size: {ms.Length} bytes");
    Assert.IsTrue(ms.Length < 300,
      $"Serialized size {ms.Length} bytes exceeds 300 byte budget");
  }
}
