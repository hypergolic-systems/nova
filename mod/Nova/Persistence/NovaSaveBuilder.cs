using System.Collections.Generic;
using System.Linq;
using Nova.Components;
using Nova.Ffi;
using Nova.Ffi.Generated;
using UnityEngine;
using Proto = Nova.Core.Persistence.Protos;

namespace Nova.Persistence;

/// <summary>
/// Build a proto <see cref="Proto.SaveFile"/> from live game state.
///
/// The previous version pulled per-component state from the C# C
/// <c>VirtualVessel</c> (via <c>mod.Virtual.SavePartState</c>); that
/// path is gone. The Rust simulator is now the source of truth for
/// component state, so the Battery/Command/etc. fields on each
/// <c>PartState</c> are populated by reading the per-vessel FFI
/// arena via <c>NovaVesselModule.Handle</c>.
///
/// Phase-1 scope: loaded vessels only. Unloaded (on-rails) vessels
/// land when background-sim ports — the Rust simulator will own
/// their state by then and we can read directly from the world's
/// vessel set.
/// </summary>
public static class NovaSaveBuilder {

  public static Proto.SaveFile Build() {
    var game = HighLogic.CurrentGame;
    var save = new Proto.SaveFile {
      UniversalTime = Planetarium.GetUniversalTime(),
      Game = BuildGameMetadata(game),
    };
    save.Crews.AddRange(BuildCrewRoster(game.CrewRoster));

    var vessels = FlightGlobals.Vessels;
    for (int i = 0; i < vessels.Count; i++) {
      var vessel = vessels[i];
      if (vessel.state == Vessel.State.DEAD) continue;
      var proto = BuildVessel(vessel);
      if (proto != null) {
        save.Vessels.Add(proto);
        if (vessel == FlightGlobals.ActiveVessel)
          save.ActiveVesselIndex = save.Vessels.Count - 1;
      }
    }

    return save;
  }

  static Proto.GameMetadata BuildGameMetadata(Game game) {
    return new Proto.GameMetadata {
      Title = game.Title,
      Mode = (int)game.Mode,
      Seed = game.Seed,
      Flag = game.flagURL,
      LaunchId = (int)game.launchID,
      Scene = (int)HighLogic.LoadedScene,
    };
  }

  static List<Proto.Kerbal> BuildCrewRoster(KerbalRoster roster) {
    var crew = new List<Proto.Kerbal>();
    foreach (var pcm in roster.Crew.Concat(roster.Tourist).Concat(roster.Unowned)) {
      crew.Add(BuildKerbal(pcm));
    }
    return crew;
  }

  static Proto.Kerbal BuildKerbal(ProtoCrewMember pcm) {
    var kerbal = new Proto.Kerbal {
      Name = pcm.name,
      Gender = (int)pcm.gender,
      Trait = pcm.trait,
      State = (int)pcm.rosterStatus,
      Courage = pcm.courage,
      Stupidity = pcm.stupidity,
      Veteran = pcm.veteran,
    };

    if (pcm.rosterStatus == ProtoCrewMember.RosterStatus.Assigned) {
      foreach (var vessel in FlightGlobals.Vessels) {
        if (!vessel.loaded) continue;
        foreach (var part in vessel.parts) {
          int seatIdx = part.protoModuleCrew.IndexOf(pcm);
          if (seatIdx >= 0) {
            kerbal.AssignedVesselId = vessel.persistentId;
            kerbal.AssignedPartId = part.persistentId;
            kerbal.SeatIndex = seatIdx;
            return kerbal;
          }
        }
      }
    }

    return kerbal;
  }

  static Proto.Vessel BuildVessel(Vessel vessel) {
    if (!vessel.loaded) {
      // Phase-1 punt: unloaded vessels need a different path (read
      // from Rust world state by id, not from KSP parts). Until that
      // lands, skip — saves only persist active/loaded vessels.
      return null;
    }

    var (structure, state) = NovaVesselBuilder.BuildFromParts(vessel.parts, p => p.persistentId);

    structure.VesselId = vessel.id.ToString();
    structure.PersistentId = vessel.persistentId;
    if (vessel.orbit != null)
      structure.Orbit = BuildOrbit(vessel.orbit);

    state.Name = vessel.vesselName;
    state.VesselType = (int)vessel.vesselType;
    state.Situation = (int)vessel.situation;
    state.MissionTime = vessel.missionTime;
    state.LaunchTime = vessel.launchTime;
    state.Flight = BuildFlightState(vessel);

    // Overwrite per-component state fields with live values from the
    // Rust FFI handle. Without this, mid-flight saves would record
    // the prefab default contents (NovaVesselBuilder seeds from cfg)
    // and lose any in-flight drain.
    OverlayLiveStateFromHandle(vessel, state);

    var structureHash = NovaVesselBuilder.ComputeStructureHash(structure);
    return new Proto.Vessel {
      Structure = structure,
      State = state,
      StructureHash = structureHash,
    };
  }

  /// <summary>
  /// Walk the vessel's parts and, for components with FFI mirrors
  /// (Battery today; more as ports land), pull the live state from
  /// the arena and overwrite the corresponding field in
  /// <see cref="Proto.PartState"/>.
  /// </summary>
  static unsafe void OverlayLiveStateFromHandle(Vessel vessel, Proto.VesselState state) {
    var mod = vessel.FindVesselModuleImplementing<NovaVesselModule>();
    var handle = mod?.Handle;
    if (handle == null) return;

    // Index the proto's parts for O(1) lookup by id.
    var psById = new Dictionary<uint, Proto.PartState>();
    foreach (var ps in state.Parts) psById[ps.Id] = ps;

    foreach (var part in vessel.parts) {
      if (!psById.TryGetValue(part.persistentId, out var ps)) continue;

      if (part.FindModuleImplementing<NovaBatteryModule>() != null
          && handle.HasState<BatteryState>(part.persistentId)) {
        var bs = handle.GetState<BatteryState>(part.persistentId);
        if (ps.Battery == null) ps.Battery = new Proto.BatteryState();
        ps.Battery.Value = bs.Contents;
      }
      // Future: TankVolume, FuelCell, etc. — same pattern.
    }
  }

  static Proto.FlightState BuildFlightState(Vessel vessel) {
    return new Proto.FlightState {
      Position = BuildPosition(vessel),
      ActionGroups = BuildActionGroups(vessel),
    };
  }

  static Proto.OrbitalState BuildOrbit(Orbit orbit) {
    return new Proto.OrbitalState {
      Inclination = orbit.inclination,
      Eccentricity = orbit.eccentricity,
      SemiMajorAxis = orbit.semiMajorAxis,
      Lan = orbit.LAN,
      ArgumentOfPeriapsis = orbit.argumentOfPeriapsis,
      MeanAnomalyAtEpoch = orbit.meanAnomalyAtEpoch,
      Epoch = orbit.epoch,
      BodyIndex = orbit.referenceBody.flightGlobalsIndex,
    };
  }

  static Proto.PositionState BuildPosition(Vessel vessel) {
    var rot = vessel.srfRelRotation;
    var vel = vessel.srf_velocity;
    return new Proto.PositionState {
      Latitude = vessel.latitude,
      Longitude = vessel.longitude,
      Altitude = vessel.altitude,
      Rotation = new Proto.Quat { X = rot.x, Y = rot.y, Z = rot.z, W = rot.w },
      Velocity = new Proto.Vec3 { X = (float)vel.x, Y = (float)vel.y, Z = (float)vel.z },
      HeightAboveTerrain = vessel.heightFromTerrain,
    };
  }

  static uint BuildActionGroups(Vessel vessel) {
    uint bits = 0;
    var groups = vessel.ActionGroups;
    for (int i = 0; i < 32; i++) {
      var group = (KSPActionGroup)(1 << i);
      if (groups[group]) bits |= (uint)(1 << i);
    }
    return bits;
  }
}
