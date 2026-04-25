using System.Collections.Generic;
using System.Linq;
using Nova.Components;
using Nova;
using UnityEngine;
using Proto = Nova.Core.Persistence.Protos;

namespace Nova.Persistence;

/// <summary>
/// Builds a proto SaveFile from live game state.
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

    // Track assignment: find which vessel/part this kerbal is in
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
    Proto.VesselStructure structure;
    Proto.VesselState state;

    if (vessel.loaded) {
      (structure, state) = NovaVesselBuilder.BuildFromParts(vessel.parts, p => p.persistentId);
    } else {
      // Unloaded: use cached structure from NovaVesselModule if available
      var mod = vessel.FindVesselModuleImplementing<NovaVesselModule>();
      structure = mod?.CachedStructure;
      if (structure != null) {
        state = BuildUnloadedState(mod);
      } else {
        // No cached structure (e.g. asteroids, comets, non-Nova vessels).
        // Build minimal structure from protoPartSnapshots so the vessel
        // is included in the save (our save is authoritative).
        structure = BuildStructureFromProto(vessel.protoVessel);
        state = new Proto.VesselState();
      }
    }

    // Vessel identity → structure
    structure.VesselId = vessel.id.ToString();
    structure.PersistentId = vessel.persistentId;

    // Structure hash (includes identity — vessel ID change = structural change)
    var structureHash = NovaVesselBuilder.ComputeStructureHash(structure);

    // Vessel state fields
    state.Name = vessel.vesselName;
    state.VesselType = (int)vessel.vesselType;
    state.Situation = (int)vessel.situation;
    state.MissionTime = vessel.missionTime;
    state.LaunchTime = vessel.launchTime;

    // Flight state
    state.Flight = BuildFlightState(vessel);

    return new Proto.Vessel { Structure = structure, State = state, StructureHash = structureHash };
  }

  static Proto.VesselState BuildUnloadedState(NovaVesselModule mod) {
    var state = new Proto.VesselState();

    // Nova component state from VirtualVessel
    if (mod.Virtual != null && mod.CachedStructure != null) {
      foreach (var ps in mod.CachedStructure.Parts) {
        var partState = new Proto.PartState { Id = ps.Id };
        mod.Virtual.SavePartState(ps.Id, partState);
        state.Parts.Add(partState);
      }
    }

    // TODO: stages for unloaded vessels

    return state;
  }

  static Proto.FlightState BuildFlightState(Vessel vessel) {
    var flight = new Proto.FlightState {
      Orbit = BuildOrbit(vessel.orbit),
      Position = BuildPosition(vessel),
      ActionGroups = BuildActionGroups(vessel),
    };
    return flight;
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
    // KSPActionGroup is a flags enum: Stage=1, Gear=2, Light=4, RCS=8, SAS=16, Brakes=32, Abort=64, Custom01=128...
    for (int i = 0; i < 32; i++) {
      var group = (KSPActionGroup)(1 << i);
      if (groups[group])
        bits |= (uint)(1 << i);
    }
    return bits;
  }

  /// <summary>
  /// Build a minimal VesselStructure from ProtoVessel data for non-Nova
  /// vessels (asteroids, comets, etc.) so they're included in the save.
  /// </summary>
  static Proto.VesselStructure BuildStructureFromProto(ProtoVessel pv) {
    var structure = new Proto.VesselStructure();
    if (pv?.protoPartSnapshots == null) return structure;

    for (int i = 0; i < pv.protoPartSnapshots.Count; i++) {
      var snap = pv.protoPartSnapshots[i];
      var ps = new Proto.PartStructure {
        Id = snap.persistentId,
        PartName = snap.partName,
        ParentIndex = i == pv.rootIndex ? -1 : snap.parentIdx,
      };
      structure.Parts.Add(ps);
    }
    return structure;
  }
}
