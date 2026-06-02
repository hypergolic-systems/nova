using System;
using System.Collections.Generic;
using System.IO;
using Nova.Core.Components;
using Nova.Core.Persistence;
using Nova.Core.Utils;
using Nova.Sim.Components;
using Nova.Sim.Config;
using ProtoBuf;
using Proto = Nova.Core.Persistence.Protos;

namespace Nova.Sim.Persistence;

// Hydrate a Nova .nvc / .nvs proto file into a VirtualVessel, sourcing
// component prefab data from the patched part database.
//
// .nvc → single craft: the file's `Vessel` becomes the simulator's
// active vessel. Persistent IDs are minted fresh from the part index
// (no clash because the simulator hosts a single vessel today).
//
// .nvs → save: the first vessel (or the file's active_vessel_index)
// becomes the simulator's active vessel. The other vessels are
// ignored for v1 — multi-vessel support arrives when the sim grows
// background-tick coverage past the headlining one.
//
// Per part:
//   1. Resolve `part_name` via PartDatabase.Get. Unknown → loud error.
//   2. Read dry mass from the patched ConfigNode's top-level `mass`
//      value (KSP convention: tons). Stored on the VirtualVessel in kg.
//   3. Walk the part's MODULE children, invoke SimComponentFactory on
//      each Nova module name. Non-Nova modules (ModuleEngines, etc.)
//      are skipped.
//   4. Call cmp.LoadStructure(ps) for components that override the
//      prefab (TankVolume, Battery, DataStorage), then cmp.Load(state)
//      on every component if a matching PartState is present.
//   5. Track parent index to feed into VirtualVessel.FromExistingParts.
public static class SimVesselLoader {
  public sealed class LoadResult {
    public VirtualVessel Vessel;
    public string VesselName;
    public string VesselGuid;
    public double UniversalTime; // 0 from .nvc; saved UT from .nvs
    public double MissionTime;
    public double LaunchTime;
    // Kerbals from `SaveFile.crew` whose `assigned_vessel_id` matches
    // this vessel's persistent id. Empty for .nvc loads (craft files
    // have no save-level roster).
    public IReadOnlyList<Proto.Kerbal> Crew = Array.Empty<Proto.Kerbal>();
  }

  public static LoadResult LoadCraft(string path, PartDatabase db, double simTime = 0) {
    using (var fs = File.OpenRead(path)) {
      var prefix = NovaFileFormat.ReadPrefix(fs);
      if (prefix.type != 'C')
        throw new InvalidDataException("expected .nvc craft file, got HGS type '" + prefix.type + "'");
      var craft = Serializer.Deserialize<Proto.CraftFile>(fs);
      // Fallback chain: craft.Metadata.Name → filename stem. In-game,
      // KSP's craft loader picks up the ship name from the .craft node;
      // the sim's .nvc proto doesn't always carry it (Metadata.Name is
      // optional), so derive from the filename when missing — that
      // matches what the player sees in the VAB save dialog.
      var metadataName = craft.Metadata?.Name;
      var fallbackName = !string.IsNullOrEmpty(metadataName)
          ? metadataName
          : Path.GetFileNameWithoutExtension(path);
      return BuildFromVessel(craft.Vessel, fallbackName, db, simTime, missionTime: 0, launchTime: simTime);
    }
  }

  public static LoadResult LoadSave(string path, PartDatabase db) {
    using (var fs = File.OpenRead(path)) {
      var prefix = NovaFileFormat.ReadPrefix(fs);
      if (prefix.type != 'S')
        throw new InvalidDataException("expected .nvs save file, got HGS type '" + prefix.type + "'");
      var save = Serializer.Deserialize<Proto.SaveFile>(fs);
      if (save.Vessels == null || save.Vessels.Count == 0)
        throw new InvalidDataException("save file contains no vessels");
      int idx = save.ActiveVesselIndex;
      if (idx < 0 || idx >= save.Vessels.Count) idx = 0;
      var v = save.Vessels[idx];
      var state = v.State;
      var result = BuildFromVessel(v, state?.Name, db,
          simTime: save.UniversalTime,
          missionTime: state?.MissionTime ?? 0,
          launchTime: state?.LaunchTime ?? 0);
      // Filter the save-level Kerbal roster down to the active vessel.
      // `Kerbal.assigned_vessel_id` is the proto's vessel persistent_id —
      // match against VesselStructure.PersistentId, not the GUID string.
      var vesselPid = v.Structure?.PersistentId ?? 0u;
      if (vesselPid != 0u && save.Crews != null && save.Crews.Count > 0) {
        var crew = new List<Proto.Kerbal>();
        for (int i = 0; i < save.Crews.Count; i++) {
          var k = save.Crews[i];
          if (k != null && k.AssignedVesselId == vesselPid) crew.Add(k);
        }
        result.Crew = crew;
      }
      return result;
    }
  }

  private static LoadResult BuildFromVessel(Proto.Vessel v, string fallbackName,
      PartDatabase db, double simTime, double missionTime, double launchTime) {
    if (v?.Structure == null || v.Structure.Parts == null || v.Structure.Parts.Count == 0)
      throw new InvalidDataException("vessel has no parts");

    var structure = v.Structure;
    var state = v.State;

    var stateById = new Dictionary<uint, Proto.PartState>();
    if (state?.Parts != null) {
      foreach (var ps in state.Parts) stateById[ps.Id] = ps;
    }

    // Component dispatch by part id. Index used here also serves as
    // the synthetic persistent id when we don't have one from save.
    var components = new Dictionary<uint, List<VirtualComponent>>();
    var parents    = new Dictionary<uint, uint?>();
    var names      = new Dictionary<uint, string>();
    var masses     = new Dictionary<uint, double>();

    for (int i = 0; i < structure.Parts.Count; i++) {
      var p = structure.Parts[i];
      uint partId = p.Id != 0 ? p.Id : (uint)(i + 1); // mint id if zero (.nvc convention)

      var partConfig = db.Get(p.PartName);
      if (partConfig == null)
        throw new InvalidDataException(
            "part '" + p.PartName + "' (index " + i + ") not found in part database");

      names[partId] = p.PartName;
      masses[partId] = ReadDryMassKg(partConfig);

      var partComponents = new List<VirtualComponent>();
      foreach (var moduleNode in partConfig.GetNodes("MODULE")) {
        var moduleName = moduleNode.GetValue("name");
        if (!SimComponentFactory.IsNovaModule(moduleName)) continue;
        var cmp = SimComponentFactory.Create(moduleNode);
        if (cmp == null) continue;
        partComponents.Add(cmp);
      }

      stateById.TryGetValue(p.Id, out var partState);
      foreach (var cmp in partComponents) {
        cmp.LoadStructure(p);
        if (partState != null) cmp.Load(partState);
      }
      components[partId] = partComponents;

      if (p.ParentIndex >= 0 && p.ParentIndex < structure.Parts.Count) {
        var parent = structure.Parts[p.ParentIndex];
        parents[partId] = parent.Id != 0 ? parent.Id : (uint)(p.ParentIndex + 1);
      } else {
        parents[partId] = null;
      }
    }

    var vessel = VirtualVessel.FromExistingParts(components, parents, names, masses, simTime);

    return new LoadResult {
      Vessel = vessel,
      // `??` would let an empty string through; use IsNullOrEmpty so a
      // proto with `Name = ""` falls through to the filename fallback.
      VesselName = !string.IsNullOrEmpty(state?.Name) ? state.Name
                 : !string.IsNullOrEmpty(fallbackName) ? fallbackName
                 : "Vessel",
      VesselGuid = !string.IsNullOrEmpty(structure.VesselId) ? structure.VesselId : Guid.NewGuid().ToString("D"),
      UniversalTime = simTime,
      MissionTime = missionTime,
      LaunchTime = launchTime,
    };
  }

  // KSP convention: `mass = X` on the part = dry mass in tons. Nova
  // stores dry mass in kg, so multiply by 1000. Missing/unparseable
  // entries → 0 (the part contributes no dry mass).
  private static double ReadDryMassKg(ConfigNode partConfig) {
    var raw = partConfig.GetValue("mass");
    if (raw == null) return 0;
    if (!double.TryParse(raw, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var tons))
      return 0;
    return tons * 1000.0;
  }
}
