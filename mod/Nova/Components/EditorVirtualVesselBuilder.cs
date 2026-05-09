using System.Collections.Generic;
using System.Linq;
using Nova.Core.Components;

namespace Nova.Components;

// Build a transient VirtualVessel from a ShipConstruct (editor scene).
// Used by NovaStageTopic to run DeltaVSimulation for the editor's
// current craft — the live VirtualVessel only exists during flight,
// where NovaVesselModule owns it. In the editor each part's components
// live on its NovaPartModule.Components list (populated by
// NovaPartModule.OnStartEditor); we clone those into a fresh
// VirtualVessel so the simulation's mutations (Throttle, Jettisoned,
// Buffer drains during the burn) don't bleed into the live modules.
internal static class EditorVirtualVesselBuilder {

  public static VirtualVessel Build(ShipConstruct ship, double time) {
    var parts   = new Dictionary<uint, List<VirtualComponent>>();
    var parents = new Dictionary<uint, uint?>();
    var names   = new Dictionary<uint, string>();
    var masses  = new Dictionary<uint, double>();

    if (ship == null || ship.parts == null) {
      return VirtualVessel.FromExistingParts(parts, parents, names, masses, time);
    }

    for (int i = 0; i < ship.parts.Count; i++) {
      var p = ship.parts[i];
      if (p == null) continue;

      // Clone every component so the simulation can mutate freely.
      // VirtualComponent.Clone is a MemberwiseClone by default; deeper
      // members override as needed (e.g. Engine resets Throttle).
      var components = new List<VirtualComponent>();
      foreach (var module in p.Modules.OfType<NovaPartModule>()) {
        if (module.Components == null) continue;
        foreach (var c in module.Components)
          components.Add(c.Clone());
      }

      parts[p.persistentId]   = components;
      parents[p.persistentId] = p.parent != null ? (uint?)p.parent.persistentId : null;
      names[p.persistentId]   = p.partInfo?.name ?? "";
      // Dry mass: read the prefab and convert tonnes → kg, matching
      // NovaVesselModule.cs:304/372 and NovaSaveLoader.cs:578. `p.mass`
      // and `p.prefabMass` are both unreliable in the editor (zero on
      // freshly-instantiated parts); the prefab's static `mass` field
      // is authoritative.
      var prefabMassTonnes = p.partInfo?.partPrefab != null ? p.partInfo.partPrefab.mass : 0f;
      masses[p.persistentId]  = prefabMassTonnes * 1000.0;
    }

    return VirtualVessel.FromExistingParts(parts, parents, names, masses, time);
  }
}
