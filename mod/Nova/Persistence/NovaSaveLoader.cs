using System;
using Nova.Core.Persistence;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Nova.Components;
using Nova.Ffi;
using UnityEngine;
using Proto = Nova.Core.Persistence.Protos;

namespace Nova.Persistence;

/// <summary>
/// Loads game state from .nvs files. Both KSP-side parts and the
/// Rust simulator are forks of the same proto pair; nothing is
/// reconstructed from per-component C# objects (those don't exist
/// any more — see <c>Nova.Core/_legacy/</c>).
///
/// Two load paths:
///  - <see cref="TryQuickload"/>: a flight scene already exists;
///    diff vs the proto and match / create / destroy as needed.
///  - <see cref="LoadScene"/>: nothing exists; create everything.
///
/// Per-vessel `Proto.Vessel` (Structure + State) is handed to:
///  - <see cref="NovaPartInstantiator"/> to materialise live KSP
///    parts;
///  - <see cref="NovaWorldAddon.RegisterVessel"/> to materialise
///    the nova-sim vessel.
/// </summary>
public static class NovaSaveLoader {

  public static Proto.SaveFile PendingProto;
  public static Game PendingGame;

  /// <summary>
  /// Quickload from .nvs. Returns true if handled (caller should
  /// skip stock quickload), false if no .nvs file exists.
  /// </summary>
  public static bool TryQuickload(string filename, string folder) {
    var dir = Path.Combine(KSPUtil.ApplicationRootPath, "saves", folder);
    var nvsPath = Path.Combine(dir, filename + ".nvs");
    if (!File.Exists(nvsPath)) return false;

    Proto.SaveFile save;
    using (var stream = File.OpenRead(nvsPath)) {
      var (type, version) = NovaFileFormat.ReadPrefix(stream);
      save = ProtoBuf.Serializer.Deserialize<Proto.SaveFile>(stream);
    }

    ApplyQuickload(save);
    return true;
  }

  static bool ApplyQuickload(Proto.SaveFile save) {
    // Phase-1: always destroy + create. Stock-style structural-diff
    // matching is gone with `ComputeCurrentStructureHash` and will be
    // re-added once the Rust simulator can answer "structure hash for
    // vessel X" itself (the source of truth has moved).
    var savedByPid = new Dictionary<uint, Proto.Vessel>();
    foreach (var sv in save.Vessels)
      savedByPid[sv.Structure.PersistentId] = sv;

    var toDestroy = new List<Vessel>();
    foreach (var v in FlightGlobals.Vessels) {
      if (v.state == Vessel.State.DEAD) continue;
      toDestroy.Add(v);
    }

    var toCreate = new List<Proto.Vessel>(savedByPid.Values);

    NovaLog.Log($"[Quickload] {toCreate.Count} to create, {toDestroy.Count} to destroy");

    if (FlightCamera.fetch != null)
      FlightCamera.fetch.SetTarget((Transform)null);

    foreach (var v in toDestroy) {
      NovaLog.Log($"[Quickload] Destroying vessel: {v.vesselName} (pid={v.persistentId})");
      var vesselGO = v.gameObject;
      FlightGlobals.RemoveVessel(v);
      if (v.parts != null)
        foreach (var p in v.parts)
          if (p != null && p.gameObject != null && p.gameObject != vesselGO)
            UnityEngine.Object.DestroyImmediate(p.gameObject);
      if (vesselGO != null)
        UnityEngine.Object.DestroyImmediate(vesselGO);
    }

    foreach (var saved in toCreate) {
      var v = CreateVessel(saved);
      if (v == null)
        throw new InvalidOperationException(
            $"Failed to create vessel pid={saved.Structure.PersistentId} name={saved.State.Name}");
      NovaLog.Log($"[Quickload] Created vessel: {v.vesselName} (pid={v.persistentId})");
    }

    Planetarium.SetUniversalTime(save.UniversalTime);

    if (save.Crews != null)
      RestoreCrew(save.Crews);

    var ignoreField = HarmonyLib.AccessTools.Field(typeof(Vessel), "ignoreCollisionsFrames");
    foreach (var v in FlightGlobals.Vessels)
      if (v != null && v.state != Vessel.State.DEAD)
        ignoreField.SetValue(v, 60);

    if (save.ActiveVesselIndex >= 0 && save.ActiveVesselIndex < save.Vessels.Count) {
      var activePid = save.Vessels[save.ActiveVesselIndex].Structure.PersistentId;
      var activeVessel = FlightGlobals.Vessels.FirstOrDefault(v => v.persistentId == activePid);
      if (activeVessel != null) {
        if (activeVessel != FlightGlobals.ActiveVessel)
          FlightGlobals.ForceSetActiveVessel(activeVessel);
        FlightCamera.fetch.SetTarget(activeVessel.transform);
      }
    }

    FlightDriver.SetPause(false);

    NovaLog.Log($"[Quickload] Complete — UT={save.UniversalTime:F1}");
    return true;
  }

  static void RestoreCrew(List<Proto.Kerbal> crew) {
    var roster = HighLogic.CurrentGame.CrewRoster;
    int seated = 0;

    NovaLog.Log($"[Quickload] Crew: restoring {crew.Count} kerbals");

    foreach (var k in crew) {
      if (k.AssignedVesselId == 0) continue;
      var pcm = roster[k.Name];
      if (pcm == null) continue;
      var vessel = FlightGlobals.Vessels.FirstOrDefault(v => v.persistentId == k.AssignedVesselId);
      if (vessel == null) continue;
      var part = vessel.parts?.FirstOrDefault(p => p.persistentId == k.AssignedPartId);
      if (part == null) continue;
      if (part.protoModuleCrew.Contains(pcm)) {
        seated++;
        continue;
      }
      if (pcm.rosterStatus == ProtoCrewMember.RosterStatus.Assigned) {
        foreach (var v in FlightGlobals.Vessels)
          foreach (var p in v.parts ?? new List<Part>())
            if (p.protoModuleCrew.Contains(pcm))
              p.RemoveCrewmember(pcm);
      }
      part.AddCrewmemberAt(pcm, k.SeatIndex);
      pcm.RegisterExperienceTraits(part);
      part.PartValues.Update();
      seated++;
    }

    if (seated > 0)
      NovaLog.Log($"[Quickload] Crew: seated {seated} kerbals");
  }

  static void ApplyActionGroups(Vessel vessel, uint bits) {
    for (int i = 0; i < 32; i++) {
      var group = (KSPActionGroup)(1 << i);
      bool on = (bits & (uint)(1 << i)) != 0;
      vessel.ActionGroups.SetGroup(group, on);
    }
  }

  /// <summary>
  /// Create a new live vessel from a saved <c>Proto.Vessel</c>. Both
  /// KSP-side <c>Part</c>s and the Rust-side <c>nova-sim</c> vessel
  /// are forked from the SAME proto pair: the same bytes drive
  /// <see cref="NovaPartInstantiator"/> here and
  /// <c>nova_vessel_new</c> over the FFI.
  /// </summary>
  static Vessel CreateVessel(Proto.Vessel saved) {
    var structure = saved.Structure;
    var state = saved.State;
    var flight = state.Flight;
    var pid = structure.PersistentId;

    NovaLog.Log($"[Quickload] CreateVessel: pid={pid} name={state.Name} parts={structure.Parts.Count}");

    // Rust-first: register the vessel with the simulator from the
    // saved proto bytes BEFORE we materialise any KSP-side state.
    // NovaVesselModule.OnLoadVessel will look this handle up by
    // persistent id when KSP brings the vessel up.
    var addon = NovaWorldAddon.Instance;
    if (addon == null) {
      NovaLog.LogError($"CreateVessel: NovaWorldAddon not available for pid={pid}");
      return null;
    }
    var ut = HighLogic.LoadedSceneIsFlight ? Planetarium.GetUniversalTime() : 0.0;
    var handle = addon.RegisterVessel(pid, Serialize(structure), Serialize(state), ut);
    if (handle == null) {
      NovaLog.LogError($"CreateVessel: Rust registration failed for pid={pid}");
      return null;
    }

    var parts = NovaPartInstantiator.Instantiate(structure, state, ps => ps.Id);
    if (parts == null) {
      NovaLog.Log($"[Quickload] CreateVessel: part instantiation failed for pid={pid}");
      addon.UnregisterVessel(pid);
      return null;
    }
    NovaLog.Log($"[Quickload] CreateVessel: instantiated {parts.Count} parts");

    var rootGO = parts[0].gameObject;
    var vessel = rootGO.AddComponent<Vessel>();

    vessel.id = Guid.Parse(structure.VesselId);
    vessel.persistentId = structure.PersistentId;
    vessel.vesselName = state.Name ?? "";
    vessel.vesselType = (VesselType)state.VesselType;
    vessel.situation = (Vessel.Situations)state.Situation;
    vessel.missionTime = state.MissionTime;
    vessel.launchTime = state.LaunchTime;
    vessel.packed = true;
    vessel.Landed = vessel.situation == Vessel.Situations.LANDED
                 || vessel.situation == Vessel.Situations.PRELAUNCH;
    vessel.Splashed = vessel.situation == Vessel.Situations.SPLASHED;
    vessel.IgnoreGForces(10);

    var orbitDriver = rootGO.AddComponent<OrbitDriver>();
    if (structure?.Orbit != null) {
      orbitDriver.orbit = LoadOrbit(structure.Orbit);
    }

    if (flight?.Position != null) {
      var pos = flight.Position;
      vessel.latitude = pos.Latitude;
      vessel.longitude = pos.Longitude;
      vessel.altitude = pos.Altitude;
      vessel.heightFromTerrain = (float)pos.HeightAboveTerrain;

      if (vessel.LandedOrSplashed) {
        var body = orbitDriver.orbit.referenceBody;
        var pqs = body?.pqsController;
        if (pqs != null) {
          var surfaceNormal = body.GetRelSurfaceNVector(pos.Latitude, pos.Longitude);
          var surfaceHeight = pqs.GetSurfaceHeight(surfaceNormal) - body.Radius;
          vessel.altitude = Math.Max(vessel.altitude, surfaceHeight);
        }
      }
      if (pos.Rotation != null)
        vessel.srfRelRotation = new Quaternion(pos.Rotation.X, pos.Rotation.Y, pos.Rotation.Z, pos.Rotation.W);

      var refBody = orbitDriver.orbit.referenceBody;
      if (refBody != null) {
        rootGO.transform.position = refBody.GetWorldSurfacePosition(
            vessel.latitude, vessel.longitude, vessel.altitude);
        if (pos.Rotation != null)
          rootGO.transform.rotation = refBody.bodyTransform.rotation * vessel.srfRelRotation;
      }
    }

    NovaPartInstantiator.PositionPartsFromRoot(parts);

    foreach (var part in parts) {
      part.packed = true;
      part.flightID = ShipConstruction.GetUniqueFlightID(HighLogic.CurrentGame.flightState);
    }

    bool orbiting = !vessel.LandedOrSplashed;
    var initMethod = HarmonyLib.AccessTools.Method(typeof(Vessel), "Initialize",
        new[] { typeof(bool), typeof(bool), typeof(bool), typeof(bool) });
    initMethod.Invoke(vessel, new object[] { true, true, orbiting, false });

    vessel.vesselType = (VesselType)state.VesselType;
    vessel.situation = (Vessel.Situations)state.Situation;
    vessel.Landed = vessel.situation == Vessel.Situations.LANDED
                 || vessel.situation == Vessel.Situations.PRELAUNCH;
    vessel.Splashed = vessel.situation == Vessel.Situations.SPLASHED;
    vessel.launchTime = state.LaunchTime;
    vessel.missionTime = state.MissionTime;
    vessel.packed = true;
    vessel.orbitDriver.updateMode = vessel.LandedOrSplashed
        ? OrbitDriver.UpdateMode.IDLE
        : OrbitDriver.UpdateMode.UPDATE;

    if (flight?.Position != null) {
      var pos = flight.Position;
      vessel.latitude = pos.Latitude;
      vessel.longitude = pos.Longitude;
      vessel.altitude = pos.Altitude;
      vessel.heightFromTerrain = (float)pos.HeightAboveTerrain;

      var body = vessel.orbitDriver.orbit.referenceBody;
      if (body != null) {
        var worldPos = body.GetWorldSurfacePosition(
            pos.Latitude, pos.Longitude, vessel.altitude);
        vessel.SetPosition(worldPos);
        if (pos.Rotation != null)
          vessel.transform.rotation = body.bodyTransform.rotation * vessel.srfRelRotation;
      }
    }

    vessel.PQSminLevel = 0;
    vessel.PQSmaxLevel = 0;

    if (flight != null)
      ApplyActionGroups(vessel, flight.ActionGroups);

    FlightGlobals.AddVessel(vessel);

    // Rust handle already exists (registered at the top of
    // CreateVessel); NovaVesselModule.OnLoadVessel attaches to it.

    foreach (var part in vessel.parts) {
      bool isFull = part.PhysicsSignificance != 1;
      if (isFull) {
        part.transform.parent = null;

        var rb = part.gameObject.GetComponent<Rigidbody>();
        if (rb == null)
          rb = part.gameObject.AddComponent<Rigidbody>();
        rb.useGravity = false;
        rb.drag = 0f;
        rb.isKinematic = true;
        rb.maxAngularVelocity = PhysicsGlobals.MaxAngularVelocity;
        part.rb = rb;
        part.CreateAttachJoint(part.attachMode);

        if (part.gameObject.GetComponent<CollisionEnhancer>() == null)
          part.gameObject.AddComponent<CollisionEnhancer>();
        var buoyancy = part.gameObject.GetComponent<PartBuoyancy>();
        if (buoyancy == null)
          buoyancy = part.gameObject.AddComponent<PartBuoyancy>();
        part.partBuoyancy = buoyancy;
        buoyancy.enabled = !part.packed;
      }
      if (part.collider != null)
        part.collider.isTrigger = false;
      part.SetCollisionIgnores();
      part.started = true;
    }

    var updateMethod = HarmonyLib.AccessTools.Method(
        typeof(CollisionManager), "UpdatePartCollisionIgnores");
    GameEvents.OnCollisionIgnoreUpdate.Fire();
    updateMethod.Invoke(CollisionManager.Instance, null);

    var stateById = new Dictionary<uint, Proto.PartState>();
    if (state?.Parts != null)
      foreach (var ps in state.Parts)
        stateById[ps.Id] = ps;
    foreach (var part in vessel.parts) {
      if (stateById.TryGetValue(part.persistentId, out var ps) && ps.Activated)
        part.force_activate(false);
    }

    GameEvents.onVesselCreate.Fire(vessel);

    return vessel;
  }

  static Orbit LoadOrbit(Proto.OrbitalState o) {
    var body = FlightGlobals.Bodies[o.BodyIndex];
    return new Orbit(o.Inclination, o.Eccentricity, o.SemiMajorAxis,
        o.Lan, o.ArgumentOfPeriapsis, o.MeanAnomalyAtEpoch, o.Epoch, body);
  }

  // ── Scene load (from scratch) ──────────────────────────────────────

  /// <summary>
  /// Build a minimal Game object from proto. Used by LoadGamePatches
  /// to return a Game without ever reading the .sfs file.
  /// </summary>
  public static Game BuildGameFromProto(Proto.SaveFile save) {
    var game = new Game();
    var meta = save.Game;
    if (meta != null) {
      game.Title = meta.Title ?? "";
      game.Mode = (Game.Modes)meta.Mode;
      game.Seed = meta.Seed;
      game.flagURL = meta.Flag ?? "";
      game.launchID = (uint)meta.LaunchId;
    }
    game.startScene = GameScenes.SPACECENTER;

    game.CrewRoster = new KerbalRoster(game.Mode);
    if (save.Crews != null) {
      foreach (var k in save.Crews) {
        var pcm = new ProtoCrewMember(
            ProtoCrewMember.KerbalType.Crew,
            k.Name);
        pcm.gender = (ProtoCrewMember.Gender)k.Gender;
        pcm.trait = k.Trait;
        KerbalRoster.SetExperienceTrait(pcm);
        pcm.courage = k.Courage;
        pcm.stupidity = k.Stupidity;
        pcm.veteran = k.Veteran;
        pcm.rosterStatus = (ProtoCrewMember.RosterStatus)k.State;
        game.CrewRoster.AddCrewMember(pcm);
      }
    }

    game.flightState = new FlightState {
      universalTime = save.UniversalTime,
      activeVesselIdx = save.ActiveVesselIndex,
    };

    var configField = HarmonyLib.AccessTools.Field(typeof(Game), "config");
    configField.SetValue(game, new ConfigNode("GAME"));

    return game;
  }

  /// <summary>
  /// Create entire flight state from proto — no diffing, no matching.
  /// Called during Game.Load when entering flight from main menu.
  /// </summary>
  public static void LoadScene(Proto.SaveFile save) {
    NovaLog.Log($"[SceneLoad] Creating {save.Vessels.Count} vessels from proto");

    if (FlightGlobals.fetch != null && FlightGlobals.Vessels != null) {
      for (int i = FlightGlobals.Vessels.Count - 1; i >= 0; i--) {
        var v = FlightGlobals.Vessels[i];
        if (v != null) {
          FlightGlobals.RemoveVessel(v);
          UnityEngine.Object.Destroy(v.gameObject);
        }
      }
    }

    Planetarium.SetUniversalTime(save.UniversalTime);

    for (int i = 0; i < save.Vessels.Count; i++) {
      var saved = save.Vessels[i];
      var v = CreateVessel(saved);
      if (v == null)
        throw new InvalidOperationException(
            $"Failed to create vessel pid={saved.Structure.PersistentId} name={saved.State.Name}");
      NovaLog.Log($"[SceneLoad] Created vessel: {v.vesselName} (pid={v.persistentId})");
    }

    if (save.Crews != null)
      RestoreCrew(save.Crews);
    foreach (var v in FlightGlobals.Vessels)
      if (v != null) v.SpawnCrew();

    var ignoreField = HarmonyLib.AccessTools.Field(typeof(Vessel), "ignoreCollisionsFrames");
    foreach (var v in FlightGlobals.Vessels)
      if (v != null && v.state != Vessel.State.DEAD)
        ignoreField.SetValue(v, 60);

    NovaLog.Log($"[SceneLoad] Complete — {FlightGlobals.Vessels.Count} vessels, UT={save.UniversalTime:F1}");
  }

  static byte[] Serialize<T>(T proto) {
    using var ms = new MemoryStream();
    ProtoBuf.Serializer.Serialize(ms, proto);
    return ms.ToArray();
  }
}
