using System.Collections.Generic;
using System.IO;
using Nova.Ffi;
using UnityEngine;
using Proto = Nova.Core.Persistence.Protos;

namespace Nova.Persistence;

/// <summary>
/// Load a <see cref="Proto.CraftFile"/> when the player clicks Launch.
/// Rust-first spawn order:
///
/// 1. Generate persistent IDs for the ship + parts up front.
/// 2. Remap the .nvc proto into the new persistent-ID space.
/// 3. Register the vessel with the Rust simulator
///    (<see cref="NovaWorldAddon.RegisterVessel"/>) — Rust is the
///    source of truth, so the canonical bytes land there *before*
///    KSP creates anything.
/// 4. Instantiate KSP <see cref="Part"/> GameObjects (via
///    <see cref="NovaPartInstantiator"/>) using the same IDs.
/// 5. KSP's <c>ShipConstruction.AssembleForLaunch</c> downstream
///    creates a <see cref="Vessel"/> whose <c>persistentId</c> equals
///    <see cref="ShipConstruct.persistentId"/> (per stock
///    <c>ShipConstruction.cs:470</c>); <see cref="Components.NovaVesselModule"/>'s
///    <c>OnLoadVessel</c> looks up the existing handle by that id.
///
/// No PendingVessel handoff: the Rust vessel is alive before any
/// KSP-side <see cref="Part"/> exists, and the per-vessel id is the
/// only piece of state that needs to cross the gap.
/// </summary>
public static class NovaCraftLoader {

  public static ShipConstruct Load(Proto.CraftFile craft) {
    var addon = NovaWorldAddon.Instance;
    if (addon == null) {
      NovaLog.LogError("NovaCraftLoader.Load: NovaWorldAddon not available");
      return null;
    }

    var protoVessel = craft.Vessel;
    var structure = protoVessel.Structure;
    var state = protoVessel.State;

    var meta = craft.Metadata;
    var ship = new ShipConstruct {
      shipName = meta?.Name ?? "",
      shipDescription = meta?.Description ?? "",
      shipFacility = (EditorFacility)(meta?.Facility ?? 0),
      persistentId = FlightGlobals.GetUniquepersistentId(),
      parts = new List<Part>(),
    };
    if (craft.Rotation != null)
      ship.rotation = new Quaternion(craft.Rotation.X, craft.Rotation.Y, craft.Rotation.Z, craft.Rotation.W);

    // Pre-generate part persistent IDs and remap the proto into that
    // ID space. The Rust simulator and KSP both use these as the
    // primary part identifier, and they need to match — Rust is keyed
    // on these from the moment of `nova_vessel_new`.
    var craftIdToPid = new Dictionary<uint, uint>();
    foreach (var ps in structure.Parts)
      craftIdToPid[ps.Id] = FlightGlobals.GetUniquepersistentId();

    var remappedStructure = RemapStructure(structure, craftIdToPid);
    var remappedState = RemapState(state, craftIdToPid);
    remappedStructure.PersistentId = ship.persistentId;
    if (remappedState != null && string.IsNullOrEmpty(remappedState.Name))
      remappedState.Name = ship.shipName;

    // Register with Rust *first*. The .nvc has no orbit (launchpad
    // is KSP's call); Rust gets the vessel as `Situation::Abstract`,
    // and `NovaVesselModule.MaybePushOrbit` upgrades it to `Orbit`
    // once `vessel.orbitDriver` is wired up.
    var ut = HighLogic.LoadedSceneIsFlight ? Planetarium.GetUniversalTime() : 0.0;
    var handle = addon.RegisterVessel(
        ship.persistentId,
        Serialize(remappedStructure),
        Serialize(remappedState),
        ut);
    if (handle == null) {
      NovaLog.LogError($"NovaCraftLoader.Load: Rust registration failed for ship {ship.shipName}");
      return null;
    }

    // Now instantiate the KSP-side parts using the IDs we already
    // committed to Rust. Each PartStructure.Id maps to its allocated
    // persistent id via craftIdToPid.
    var parts = NovaPartInstantiator.Instantiate(structure, state, ps => craftIdToPid[ps.Id]);
    if (parts == null) {
      addon.UnregisterVessel(ship.persistentId);
      return null;
    }

    // Restore editor position — PositionPartsFromRoot placed root at
    // origin, but the editor expects it at the saved VAB/SPH position.
    if (craft.EditorPosition != null) {
      var ep = craft.EditorPosition;
      parts[0].transform.position = new Vector3(ep.X, ep.Y, ep.Z);
      NovaPartInstantiator.PositionPartsFromRoot(parts);
    }

    foreach (var part in parts)
      part.ship = ship;

    ship.parts = parts;
    return ship;
  }

  static Proto.VesselStructure RemapStructure(Proto.VesselStructure src, Dictionary<uint, uint> map) {
    var copy = Clone(src);
    foreach (var ps in copy.Parts) {
      if (map.TryGetValue(ps.Id, out var newId)) ps.Id = newId;
      if (ps.Symmetry?.Partners != null) {
        for (int i = 0; i < ps.Symmetry.Partners.Length; i++)
          if (map.TryGetValue(ps.Symmetry.Partners[i], out var newPartner))
            ps.Symmetry.Partners[i] = newPartner;
      }
    }
    return copy;
  }

  static Proto.VesselState RemapState(Proto.VesselState src, Dictionary<uint, uint> map) {
    if (src == null) return new Proto.VesselState();
    var copy = Clone(src);
    foreach (var ps in copy.Parts) {
      if (map.TryGetValue(ps.Id, out var newId)) ps.Id = newId;
    }
    foreach (var stage in copy.Stages) {
      if (stage.PartIds == null) continue;
      for (int i = 0; i < stage.PartIds.Length; i++)
        if (map.TryGetValue(stage.PartIds[i], out var newId))
          stage.PartIds[i] = newId;
    }
    return copy;
  }

  static T Clone<T>(T src) {
    using var ms = new MemoryStream();
    ProtoBuf.Serializer.Serialize(ms, src);
    ms.Position = 0;
    return ProtoBuf.Serializer.Deserialize<T>(ms);
  }

  static byte[] Serialize<T>(T proto) {
    using var ms = new MemoryStream();
    ProtoBuf.Serializer.Serialize(ms, proto);
    return ms.ToArray();
  }
}
