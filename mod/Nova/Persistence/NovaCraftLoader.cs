using System.Collections.Generic;
using UnityEngine;
using Proto = Nova.Core.Persistence.Protos;

namespace Nova.Persistence;

public static class NovaCraftLoader {

  public static ShipConstruct Load(Proto.CraftFile craft) {
    var vessel = craft.Vessel;
    var structure = vessel.Structure;
    var state = vessel.State;

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

    // Craft files assign new persistent IDs (not loaded from save)
    var parts = NovaPartInstantiator.Instantiate(structure, state,
      ps => FlightGlobals.GetUniquepersistentId());
    if (parts == null) return null;

    // Restore editor position — PositionPartsFromRoot placed root at origin,
    // but the editor expects it at the saved VAB/SPH position.
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
}
