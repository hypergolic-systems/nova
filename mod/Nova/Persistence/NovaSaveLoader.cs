using System;
using Nova.Core.Persistence;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Nova.Core;
using Nova.Core.Components;
using Nova.Components;
using Nova.Science;
using Nova;
using UnityEngine;
using Proto = Nova.Core.Persistence.Protos;

namespace Nova.Persistence;

/// <summary>
/// Loads game state from .hgs files.
///
/// Two load paths:
/// - Quickload (TryQuickload): running scene exists, diff against it
/// - Scene load (LoadScene): nothing exists, create everything from scratch
/// </summary>
public static class NovaSaveLoader {

  /// <summary>
  /// Proto deserialized by LoadGamePatches.LoadGame_Prefix, consumed by
  /// LoadGamePatches.Load_Prefix to create vessels during Game.Load.
  /// </summary>
  public static Proto.SaveFile PendingProto;

  /// <summary>
  /// The Game instance built from PendingProto. Used to validate that
  /// Game.Load is being called on the right Game (not a stale reference
  /// from e.g. LoadGameDialog metadata preview).
  /// </summary>
  public static Game PendingGame;

  /// <summary>
  /// Quickload from .hgs. Returns true if handled (caller should
  /// skip stock quickload), false if no .hgs file exists.
  /// </summary>
  public static bool TryQuickload(string filename, string folder) {
    var dir = Path.Combine(KSPUtil.ApplicationRootPath, "saves", folder);
    var hgsPath = Path.Combine(dir, filename + ".hgs");
    if (!File.Exists(hgsPath)) return false;

    Proto.SaveFile save;
    using (var stream = File.OpenRead(hgsPath)) {
      var (type, version) = NovaFileFormat.ReadPrefix(stream);
      save = ProtoBuf.Serializer.Deserialize<Proto.SaveFile>(stream);
    }

    ApplyQuickload(save);
    return true;
  }

  static bool ApplyQuickload(Proto.SaveFile save) {
    NovaScienceArchive.Reset();
    NovaScienceArchive.Instance.HydrateFrom(save.ScienceArchive);

    // Build lookup of saved vessels by PersistentId
    var savedByPid = new Dictionary<uint, Proto.Vessel>();
    foreach (var sv in save.Vessels)
      savedByPid[sv.Structure.PersistentId] = sv;

    // Build lookup of in-flight vessels by persistentId
    var flightByPid = new Dictionary<uint, Vessel>();
    foreach (var v in FlightGlobals.Vessels) {
      if (v.state == Vessel.State.DEAD) continue;
      flightByPid[v.persistentId] = v;
    }

    // Classify vessels into match / create / destroy
    var toMatch = new List<(Vessel flight, Proto.Vessel saved)>();
    var toCreate = new List<Proto.Vessel>();
    var toDestroy = new List<Vessel>();

    foreach (var kvp in savedByPid) {
      var pid = kvp.Key;
      var saved = kvp.Value;

      if (flightByPid.TryGetValue(pid, out var flight)) {
        flightByPid.Remove(pid);
        var mod = flight.FindVesselModuleImplementing<NovaVesselModule>();
        var flightHash = mod?.GetOrComputeStructureHash();

        if (flightHash != null && saved.StructureHash != null
            && HashesMatch(flightHash, saved.StructureHash)) {
          toMatch.Add((flight, saved));
        } else if (flightHash == null && saved.StructureHash != null) {
          // No hash available on in-flight side (e.g. unloaded non-Nova vessel).
          // Match by PersistentId alone — structure can't change while unloaded.
          toMatch.Add((flight, saved));
        } else {
          // Structure changed — destroy old, create new
          var reason = flightHash == null ? "no flight hash"
                     : saved.StructureHash == null ? "no saved hash"
                     : "hash mismatch";
          var fh = flightHash != null ? BitConverter.ToString(flightHash).Replace("-","").Substring(0,16) : "null";
          var sh = saved.StructureHash != null ? BitConverter.ToString(saved.StructureHash).Replace("-","").Substring(0,16) : "null";
          NovaLog.Log($"[Quickload] Structure mismatch for {flight.vesselName} (pid={pid}): {reason} flight={fh} saved={sh}");
          toDestroy.Add(flight);
          toCreate.Add(saved);
        }
      } else {
        // Vessel in save but not in flight — create
        toCreate.Add(saved);
      }
    }

    // Remaining in-flight vessels not in save — destroy
    foreach (var v in flightByPid.Values)
      toDestroy.Add(v);

    NovaLog.Log($"[Quickload] {toMatch.Count} matched, {toCreate.Count} to create, {toDestroy.Count} to destroy");

    // Detach FlightCamera from the active vessel before destruction.
    // DestroyImmediate on the vessel GO leaves the camera with a null target.
    if (FlightCamera.fetch != null)
      FlightCamera.fetch.SetTarget((Transform)null);

    // Destroy old vessels synchronously. Skip Die() — it uses deferred
    // Object.Destroy which leaves ghost parts. FULL parts are detached from
    // the vessel hierarchy (Part.Start calls SetParent(null)), so we must
    // destroy each part GO individually.
    foreach (var v in toDestroy) {
      NovaLog.Log($"[Quickload] Destroying vessel: {v.vesselName} (pid={v.persistentId})");
      var vesselGO = v.gameObject;
      FlightGlobals.RemoveVessel(v);
      // Destroy non-root part GOs first (FULL parts are detached from hierarchy).
      // Root part GO = vessel GO — destroy last.
      if (v.parts != null)
        foreach (var p in v.parts)
          if (p != null && p.gameObject != null && p.gameObject != vesselGO)
            UnityEngine.Object.DestroyImmediate(p.gameObject);
      if (vesselGO != null)
        UnityEngine.Object.DestroyImmediate(vesselGO);
    }

    // Create new/mismatched vessels (includes synchronous physics init)
    foreach (var saved in toCreate) {
      var v = CreateVessel(saved);
      if (v == null)
        throw new InvalidOperationException($"Failed to create vessel pid={saved.Structure.PersistentId} name={saved.State.Name}");
      NovaLog.Log($"[Quickload] Created vessel: {v.vesselName} (pid={v.persistentId})");
    }

    // Restore universal time BEFORE applying state — orbital position
    // computation needs the correct UT.
    Planetarium.SetUniversalTime(save.UniversalTime);

    // Apply state to matched vessels
    foreach (var (flight, saved) in toMatch) {
      NovaLog.Log($"[Quickload] Matched vessel: {flight.vesselName} (pid={flight.persistentId})");
      ApplyVesselState(flight, saved);
    }

    // Restore crew assignments
    if (save.Crews != null)
      RestoreCrew(save.Crews);

    // Suppress collision handling during unpack (stock API, used for docking)
    var ignoreField = HarmonyLib.AccessTools.Field(typeof(Vessel), "ignoreCollisionsFrames");
    foreach (var v in FlightGlobals.Vessels)
      if (v != null && v.state != Vessel.State.DEAD)
        ignoreField.SetValue(v, 60);

    // Set active vessel — triggers GoOffRails → Unpack.
    // Joints and collision ignores are already in place from CreateVessel.
    if (save.ActiveVesselIndex >= 0 && save.ActiveVesselIndex < save.Vessels.Count) {
      var activePid = save.Vessels[save.ActiveVesselIndex].Structure.PersistentId;
      var activeVessel = FlightGlobals.Vessels.FirstOrDefault(v => v.persistentId == activePid);
      if (activeVessel != null) {
        if (activeVessel != FlightGlobals.ActiveVessel)
          FlightGlobals.ForceSetActiveVessel(activeVessel);
        // Re-target camera — ForceSetActiveVessel skips if already active,
        // and the camera pivot may have been disrupted by quickload.
        FlightCamera.fetch.SetTarget(activeVessel.transform);
      }
    }

    FlightDriver.SetPause(false);

    NovaLog.Log($"[Quickload] Complete — UT={save.UniversalTime:F1}");
    return true;
  }

  static void ApplyVesselState(Vessel vessel, Proto.Vessel saved) {
    var state = saved.State;
    var flight = state.Flight;

    // Vessel metadata
    vessel.vesselName = state.Name;
    vessel.vesselType = (VesselType)state.VesselType;
    vessel.situation = (Vessel.Situations)state.Situation;
    vessel.missionTime = state.MissionTime;
    vessel.launchTime = state.LaunchTime;

    // Orbit
    if (flight?.Orbit != null)
      ApplyOrbit(vessel, flight.Orbit);

    // Position and rotation — pack the vessel, reposition from orbit, unpack.
    if (flight != null) {
      bool wasPacked = vessel.packed;
      if (!wasPacked) vessel.GoOnRails();

      // Position from orbit
      if (flight.Orbit != null) {
        vessel.orbitDriver.updateMode = OrbitDriver.UpdateMode.UPDATE;
        vessel.orbitDriver.orbit.Init();
        vessel.orbitDriver.updateFromParameters();
      }

      // Rotation
      if (flight.Position != null) {
        var pos = flight.Position;
        vessel.latitude = pos.Latitude;
        vessel.longitude = pos.Longitude;
        vessel.altitude = pos.Altitude;
        vessel.heightFromTerrain = (float)pos.HeightAboveTerrain;
        if (pos.Rotation != null) {
          vessel.srfRelRotation = new Quaternion(pos.Rotation.X, pos.Rotation.Y, pos.Rotation.Z, pos.Rotation.W);
          var body = vessel.orbitDriver.orbit.referenceBody;
          vessel.transform.rotation = body.bodyTransform.rotation * vessel.srfRelRotation;
        }
      }

      if (!wasPacked) {
        vessel.GoOffRails();
        // GoOffRails reinitializes modules which can reset autopilot state.
        // Re-register SAS skill callbacks that may have been lost.
        vessel.Autopilot.SetupModules();
      }

      // Action groups AFTER unpack
      ApplyActionGroups(vessel, flight.ActionGroups);
    }

    // Nova component state
    var mod = vessel.FindVesselModuleImplementing<NovaVesselModule>();
    if (mod?.Virtual != null && state.Parts != null) {
      foreach (var partState in state.Parts)
        mod.Virtual.LoadPartState(partState.Id, partState);
      mod.Virtual.Invalidate();
    }
  }

  static void RestoreCrew(List<Proto.Kerbal> crew) {
    var roster = HighLogic.CurrentGame.CrewRoster;
    int seated = 0;

    NovaLog.Log($"[Quickload] Crew: restoring {crew.Count} kerbals");

    foreach (var k in crew) {
      if (k.AssignedVesselId == 0) {
        NovaLog.Log($"[Quickload] Crew: {k.Name} unassigned, skipping");
        continue;
      }

      var pcm = roster[k.Name];
      if (pcm == null) {
        NovaLog.Log($"[Quickload] Crew: {k.Name} not found in roster");
        continue;
      }

      // Find the vessel and part
      var vessel = FlightGlobals.Vessels.FirstOrDefault(v => v.persistentId == k.AssignedVesselId);
      if (vessel == null) {
        NovaLog.Log($"[Quickload] Crew: {k.Name} vessel pid={k.AssignedVesselId} not found");
        continue;
      }

      var part = vessel.parts?.FirstOrDefault(p => p.persistentId == k.AssignedPartId);
      if (part == null) {
        NovaLog.Log($"[Quickload] Crew: {k.Name} part pid={k.AssignedPartId} not found on {vessel.vesselName}");
        continue;
      }

      // Skip if already in the right seat (matched vessel, crew unchanged)
      if (part.protoModuleCrew.Contains(pcm)) {
        seated++;
        continue;
      }

      // Remove from current assignment if any
      if (pcm.rosterStatus == ProtoCrewMember.RosterStatus.Assigned) {
        foreach (var v in FlightGlobals.Vessels)
          foreach (var p in v.parts ?? new List<Part>())
            if (p.protoModuleCrew.Contains(pcm))
              p.RemoveCrewmember(pcm);
      }

      // Seat at saved index, register experience traits, refresh part skill values
      part.AddCrewmemberAt(pcm, k.SeatIndex);
      pcm.RegisterExperienceTraits(part);
      part.PartValues.Update();
      seated++;
    }

    if (seated > 0)
      NovaLog.Log($"[Quickload] Crew: seated {seated} kerbals");
  }

  static void ApplyOrbit(Vessel vessel, Proto.OrbitalState orbState) {
    var body = FlightGlobals.Bodies[orbState.BodyIndex];
    var orbit = vessel.orbitDriver.orbit;
    orbit.inclination = orbState.Inclination;
    orbit.eccentricity = orbState.Eccentricity;
    orbit.semiMajorAxis = orbState.SemiMajorAxis;
    orbit.LAN = orbState.Lan;
    orbit.argumentOfPeriapsis = orbState.ArgumentOfPeriapsis;
    orbit.meanAnomalyAtEpoch = orbState.MeanAnomalyAtEpoch;
    orbit.epoch = orbState.Epoch;
    orbit.referenceBody = body;
    orbit.Init();
    vessel.orbitDriver.updateFromParameters();
  }

  static void ApplyActionGroups(Vessel vessel, uint bits) {
    for (int i = 0; i < 32; i++) {
      var group = (KSPActionGroup)(1 << i);
      bool on = (bits & (uint)(1 << i)) != 0;
      vessel.ActionGroups.SetGroup(group, on);
    }
  }

  /// <summary>
  /// Create a new vessel from proto data. Sets up state so that stock's
  /// Part.Start lifecycle handles rigidbodies, joints, collision, and
  /// module initialization on the next frame.
  /// </summary>
  static Vessel CreateVessel(Proto.Vessel saved) {
    var structure = saved.Structure;
    var state = saved.State;
    var flight = state.Flight;
    var pid = structure.PersistentId;

    NovaLog.Log($"[Quickload] CreateVessel: pid={pid} name={state.Name} parts={structure.Parts.Count}");

    // 1. Instantiate parts from prefab (sets parent/children, attach nodes, attachMode, orgPos)
    var parts = NovaPartInstantiator.Instantiate(structure, state, ps => ps.Id);
    if (parts == null) {
      NovaLog.Log($"[Quickload] CreateVessel: part instantiation failed for pid={pid}");
      return null;
    }
    NovaLog.Log($"[Quickload] CreateVessel: instantiated {parts.Count} parts");

    // 2. Root part GO = vessel GO. Vessel.Awake sets vesselTransform, framesAtStartup.
    var rootGO = parts[0].gameObject;
    var vessel = rootGO.AddComponent<Vessel>();

    // 3. Vessel identity and state
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

    // 4. Orbit
    var orbitDriver = rootGO.AddComponent<OrbitDriver>();
    if (flight?.Orbit != null) {
      NovaLog.Log($"[Quickload] CreateVessel: orbit bodyIndex={flight.Orbit.BodyIndex} sma={flight.Orbit.SemiMajorAxis:F0}");
      orbitDriver.orbit = LoadOrbit(flight.Orbit);
    }

    // 5. World position/rotation from save
    // Saved world position is floating-origin-dependent and stale across
    // sessions. Compute world position from geographic coordinates instead.
    if (flight?.Position != null) {
      var pos = flight.Position;
      vessel.latitude = pos.Latitude;
      vessel.longitude = pos.Longitude;
      vessel.altitude = pos.Altitude;
      vessel.heightFromTerrain = (float)pos.HeightAboveTerrain;

      // Correct altitude for landed vessels — ensure we're never below terrain.
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

    // 6. Position parts from orgPos. NovaPartInstantiator already set up the
    // correct transform hierarchy (each part parented to its parent part).
    // Don't flatten under root — physicsless parts rely on transform parenting.
    NovaPartInstantiator.PositionPartsFromRoot(parts);

    // 7. Part state needed by Initialize
    foreach (var part in parts) {
      part.packed = true;
      part.flightID = ShipConstruction.GetUniqueFlightID(HighLogic.CurrentGame.flightState);
    }

    // 8. Initialize(preCreate=true) — gets us findVesselParts, orbitRenderer,
    // PQS levels, referenceBody, SetLoaded, isAttached, etc. preCreate skips
    // AddVessel and InitializeModules (we handle those separately).
    bool orbiting = !vessel.LandedOrSplashed;
    var initMethod = HarmonyLib.AccessTools.Method(typeof(Vessel), "Initialize",
      new[] { typeof(bool), typeof(bool), typeof(bool), typeof(bool) });
    // fromShipAssembly=true so Initialize takes the branch that sets
    // packed=true + situation=PRELAUNCH (which we overwrite below),
    // instead of the else branch that sets packed=false + updateSituation → FLYING.
    initMethod.Invoke(vessel, new object[] { true, true, orbiting, false });
    NovaLog.Log($"[Quickload] CreateVessel: Initialize found {vessel.parts.Count} parts");

    // 9. Fix values that Initialize overwrites
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

    // Restore geographic position — Initialize(fromShipAssembly) recalculates
    // lat/lon/alt from the world position, which may introduce floating-point drift.
    if (flight?.Position != null) {
      var pos = flight.Position;
      vessel.latitude = pos.Latitude;
      vessel.longitude = pos.Longitude;
      vessel.altitude = pos.Altitude;
      vessel.heightFromTerrain = (float)pos.HeightAboveTerrain;

      // Recompute world position from geographic coordinates
      var body = vessel.orbitDriver.orbit.referenceBody;
      if (body != null) {
        var worldPos = body.GetWorldSurfacePosition(
          pos.Latitude, pos.Longitude, vessel.altitude);
        vessel.SetPosition(worldPos);
        if (pos.Rotation != null)
          vessel.transform.rotation = body.bodyTransform.rotation * vessel.srfRelRotation;
      }
    }

    // Reset PQS levels so GoOffRails triggers CheckGroundCollision
    // (terrain may not be fully loaded yet). Initialize sets these to
    // match the current controller, which would skip the ground check.
    vessel.PQSminLevel = 0;
    vessel.PQSmaxLevel = 0;

    // 10. Action groups
    if (flight != null)
      ApplyActionGroups(vessel, flight.ActionGroups);

    // 11. Register with FlightGlobals — creates VesselModules
    FlightGlobals.AddVessel(vessel);

    // 14. Build VirtualVessel (must exist before OnStartFlight)
    var hgsMod = vessel.FindVesselModuleImplementing<NovaVesselModule>();
    if (hgsMod != null)
      hgsMod.Virtual = BuildVirtualVessel(structure, state, parts);

    // 15. Synchronous physics init — create rigidbodies and joints NOW
    // instead of waiting for Part.Start (which runs next frame).
    // Part.Start is idempotent — it checks rb == null before creating.
    // Also set started=true — Part.Start only sets this in FLIGHT scene,
    // but we create vessels in SPACECENTER. FlightIntegrator.Setup() bails
    // if !partRef.started, blocking all force integration (gravity, thrust).
    foreach (var part in vessel.parts) {
      bool isFull = part.PhysicsSignificance != 1;  // 1 = NONE, 0/-1 = FULL
      if (isFull) {
        // Detach from transform hierarchy — FULL parts are connected by
        // physics joints only. Without this, the hierarchy fights the joints.
        part.transform.parent = null;

        var rb = part.gameObject.GetComponent<Rigidbody>();
        if (rb == null)
          rb = part.gameObject.AddComponent<Rigidbody>();
        rb.useGravity = false;
        rb.drag = 0f;
        rb.isKinematic = true;  // packed
        rb.maxAngularVelocity = PhysicsGlobals.MaxAngularVelocity;
        part.rb = rb;
        part.CreateAttachJoint(part.attachMode);

        // Stock Part.Start adds these — expected by FlightIntegrator
        if (part.gameObject.GetComponent<CollisionEnhancer>() == null)
          part.gameObject.AddComponent<CollisionEnhancer>();
        var buoyancy = part.gameObject.GetComponent<PartBuoyancy>();
        if (buoyancy == null)
          buoyancy = part.gameObject.AddComponent<PartBuoyancy>();
        part.partBuoyancy = buoyancy;
        buoyancy.enabled = !part.packed;
      }
      // Part.Start sets collider.isTrigger = false — without this, part
      // colliders are triggers and pass through terrain instead of colliding.
      if (part.collider != null)
        part.collider.isTrigger = false;
      part.SetCollisionIgnores();
      part.started = true;
    }

    // Force CollisionManager to apply Physics.IgnoreCollision synchronously
    var updateMethod = HarmonyLib.AccessTools.Method(
      typeof(CollisionManager), "UpdatePartCollisionIgnores");
    GameEvents.OnCollisionIgnoreUpdate.Fire();
    updateMethod.Invoke(CollisionManager.Instance, null);

    // 16. Restore part activation state (e.g. staged engines)
    var stateById = new Dictionary<uint, Proto.PartState>();
    if (state?.Parts != null)
      foreach (var ps in state.Parts)
        stateById[ps.Id] = ps;
    foreach (var part in vessel.parts) {
      if (stateById.TryGetValue(part.persistentId, out var ps) && ps.Activated)
        part.force_activate(false);
    }

    // 17. Fire creation event
    GameEvents.onVesselCreate.Fire(vessel);

    return vessel;
  }

  /// <summary>
  /// Build a VirtualVessel from proto data + prefab configs.
  /// </summary>
  static VirtualVessel BuildVirtualVessel(
      Proto.VesselStructure structure, Proto.VesselState state, List<Part> parts) {
    var vv = new VirtualVessel();

    var stateById = new Dictionary<uint, Proto.PartState>();
    if (state?.Parts != null)
      foreach (var ps in state.Parts)
        stateById[ps.Id] = ps;

    var parentMap = new Dictionary<uint, uint?>();
    foreach (var p in parts)
      parentMap[p.persistentId] = p.parent?.persistentId;

    for (int i = 0; i < structure.Parts.Count && i < parts.Count; i++) {
      var protoPS = structure.Parts[i];
      var part = parts[i];
      var partName = protoPS.PartName;

      var partInfo = PartLoader.getPartInfoByName(partName);
      if (partInfo == null) continue;

      var components = new List<Nova.Core.Components.VirtualComponent>();
      foreach (var moduleNode in partInfo.partConfig.GetNodes("MODULE")) {
        var moduleName = moduleNode.GetValue("name");
        if (moduleName == null || !ComponentFactory.IsHgsModule(moduleName)) continue;

        var cmp = ComponentFactory.Create(moduleNode);
        cmp.LoadStructure(protoPS);
        components.Add(cmp);
      }

      // Use prefab mass from the part config — part.prefabMass is 0 on freshly
      // instantiated parts (it's set during Part.OnLoad which we skip).
      var dryMassKg = partInfo.partPrefab.mass * 1000;

      if (components.Count > 0) {
        vv.AddPart(part.persistentId, partName, dryMassKg, components);
        if (stateById.TryGetValue(protoPS.Id, out var partState))
          vv.LoadPartState(part.persistentId, partState);
      }

      vv.SetPartDryMass(part.persistentId, dryMassKg);
    }

    vv.UpdatePartTree(parentMap);
    vv.InitializeSolver(Planetarium.GetUniversalTime());
    return vv;
  }

  static Orbit LoadOrbit(Proto.OrbitalState o) {
    var body = FlightGlobals.Bodies[o.BodyIndex];
    return new Orbit(o.Inclination, o.Eccentricity, o.SemiMajorAxis,
      o.Lan, o.ArgumentOfPeriapsis, o.MeanAnomalyAtEpoch, o.Epoch, body);
  }

  static bool HashesMatch(byte[] a, byte[] b) {
    if (a.Length != b.Length) return false;
    for (int i = 0; i < a.Length; i++)
      if (a[i] != b[i]) return false;
    return true;
  }

  // ── Scene load (from scratch) ──────────────────────────────────────────

  /// <summary>
  /// Build a minimal Game object from proto. Used by LoadGamePatches to
  /// return a Game without ever reading the .sfs file.
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
    // Default to SPACECENTER — callers override for quickload
    game.startScene = GameScenes.SPACECENTER;

    // Crew roster
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

    // Empty flight state — vessels are created by LoadScene, not ProtoVessels
    game.flightState = new FlightState {
      universalTime = save.UniversalTime,
      activeVesselIdx = save.ActiveVesselIndex,
    };

    // Provide a config node so other code doesn't NRE on HighLogic.CurrentGame.config
    var configField = HarmonyLib.AccessTools.Field(typeof(Game), "config");
    configField.SetValue(game, new ConfigNode("GAME"));

    return game;
  }

  /// <summary>
  /// Create entire flight state from proto — no diffing, no matching.
  /// Called during Game.Load when entering flight from main menu or space center.
  /// </summary>
  public static void LoadScene(Proto.SaveFile save) {
    NovaLog.Log($"[SceneLoad] Creating {save.Vessels.Count} vessels from proto");

    NovaScienceArchive.Reset();
    NovaScienceArchive.Instance.HydrateFrom(save.ScienceArchive);

    // Destroy any existing persistent vessels from a previous session
    // (e.g., exit to main menu then load again).
    if (FlightGlobals.fetch != null && FlightGlobals.Vessels != null) {
      for (int i = FlightGlobals.Vessels.Count - 1; i >= 0; i--) {
        var v = FlightGlobals.Vessels[i];
        if (v != null) {
          FlightGlobals.RemoveVessel(v);
          UnityEngine.Object.Destroy(v.gameObject);
        }
      }
      NovaLog.Log($"[SceneLoad] Cleared {FlightGlobals.Vessels.Count} stale vessels");
    }

    Planetarium.SetUniversalTime(save.UniversalTime);

    // Create all vessels
    for (int i = 0; i < save.Vessels.Count; i++) {
      var saved = save.Vessels[i];
      var v = CreateVessel(saved);
      if (v == null)
        throw new InvalidOperationException(
          $"Failed to create vessel pid={saved.Structure.PersistentId} name={saved.State.Name}");
      NovaLog.Log($"[SceneLoad] Created vessel: {v.vesselName} (pid={v.persistentId})");
    }

    // Restore crew assignments and spawn IVA internals for portraits
    if (save.Crews != null)
      RestoreCrew(save.Crews);
    foreach (var v in FlightGlobals.Vessels)
      if (v != null) v.SpawnCrew();

    // Suppress collision handling during initial physics setup
    var ignoreField = HarmonyLib.AccessTools.Field(typeof(Vessel), "ignoreCollisionsFrames");
    foreach (var v in FlightGlobals.Vessels)
      if (v != null && v.state != Vessel.State.DEAD)
        ignoreField.SetValue(v, 60);

    NovaLog.Log($"[SceneLoad] Complete — {FlightGlobals.Vessels.Count} vessels, UT={save.UniversalTime:F1}");
  }
}
