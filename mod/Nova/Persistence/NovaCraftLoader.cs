using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Proto = Nova.Core.Persistence.Protos;

namespace Nova.Persistence;

/// <summary>
/// Load a <see cref="Proto.CraftFile"/> into a KSP
/// <see cref="ShipConstruct"/> AND stash a remapped
/// <see cref="Proto.Vessel"/> for the Rust simulator to pick up
/// once KSP spawns the live <see cref="Vessel"/>.
///
/// The proto is canonical: every craft launched from the editor
/// goes through this method, so the .nvc bytes flow into both
/// the KSP-side <see cref="Part"/> graph (via
/// <see cref="NovaPartInstantiator"/>) and the Rust-side
/// <c>nova-sim</c> vessel (via <see cref="PendingVessel"/> →
/// <c>NovaVesselModule.OnPartCountChanged</c> →
/// <c>RegisterFromProto</c>).
///
/// Why the stash: KSP creates the live <see cref="Vessel"/> some
/// frames after <see cref="Load"/> returns, and the
/// <see cref="GameEvents.onVesselPartCountChanged"/> hook is the
/// earliest reliable point to register with Rust. We can't pass
/// the proto through KSP's spawn pipeline directly, so we hand
/// it off via this static.
/// </summary>
public static class NovaCraftLoader {

  /// <summary>
  /// Set by <see cref="Load"/> after a craft is loaded via
  /// <c>ShipConstruction.LoadShip</c>. Consumed by
  /// <c>NovaVesselModule.OnPartCountChanged</c> when the spawned
  /// vessel's part-id set matches; cleared after use.
  /// </summary>
  public static Proto.Vessel PendingVessel;

  public static ShipConstruct Load(Proto.CraftFile craft) {
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

    // Capture craft-ID → persistent-ID mapping during instantiation
    // so we can rewrite the proto into persistent-ID space afterwards.
    // The Rust side keys per-component arena slots by the persistent
    // ID that the C# side will report at lookup time, so the proto
    // must use the same id space.
    var craftIdToPid = new Dictionary<uint, uint>();
    var parts = NovaPartInstantiator.Instantiate(structure, state, ps => {
      var newId = FlightGlobals.GetUniquepersistentId();
      craftIdToPid[ps.Id] = newId;
      return newId;
    });
    if (parts == null) return null;

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

    // Clone + remap the proto into persistent-ID space and stash
    // for the upcoming OnPartCountChanged → RegisterFromProto handoff.
    PendingVessel = new Proto.Vessel {
      Structure = RemapStructure(structure, craftIdToPid),
      State = RemapState(state, craftIdToPid),
    };

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
}
