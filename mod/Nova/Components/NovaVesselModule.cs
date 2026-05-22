using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Nova.Core;
using Nova.Communications;
using Nova.Core.Communications;
using Nova.Core.Components;
using Nova.Core.Components.Communications;
using Nova.Core.Components.Control;
using Nova.Core.Resources;
using Nova.Core.Flight;
using Nova.Core.Utils;
using Nova.Patches;
using Nova.Persistence;
using Nova.Science;
using Nova.Telemetry;
using Nova;
using Proto = Nova.Core.Persistence.Protos;

namespace Nova.Components;

public class NovaVesselModule : VesselModule {

  public VirtualVessel Virtual;

  /// <summary>
  /// Set by part modules (e.g. NovaRcsModule) when they discover data at
  /// OnStart that wasn't available during the initial solver build.
  /// Consumed once in the next FixedUpdate.
  /// </summary>
  public bool NeedsSolverRebuild;

  private bool solarDataDirty = true;

  /// <summary>
  /// Cached proto VesselStructure, built from live parts when loaded.
  /// Used by NovaSaveBuilder for unloaded vessel saves (no Part objects).
  /// Rebuilt on vessel modification (dock/undock).
  /// </summary>
  public Proto.VesselStructure CachedStructure;

  /// <summary>
  /// SHA256 hash of CachedStructure. Used by quickload to detect
  /// structural changes without full comparison.
  /// </summary>
  public byte[] CachedStructureHash;

  /// <summary>
  /// Lazily compute the structure hash. Tries cached hash first,
  /// then computes from CachedStructure, then rebuilds from parts.
  /// </summary>
  public byte[] GetOrComputeStructureHash() {
    if (CachedStructureHash != null) return CachedStructureHash;
    if (CachedStructure != null) {
      CachedStructureHash = NovaVesselBuilder.ComputeStructureHash(CachedStructure);
      return CachedStructureHash;
    }
    if (vessel?.parts != null && vessel.parts.Count > 0) {
      RebuildCachedStructure();
      return CachedStructureHash;
    }
    return null;
  }

  protected override void OnAwake() {
    base.OnAwake();
    // Must register in Awake (not Start) because Vessel.Initialize fires
    // onVesselPartCountChanged during the same frame as AddComponent<Vessel>,
    // before Unity calls Start on the next frame.
    GameEvents.onVesselPartCountChanged.Add(OnPartCountChanged);
  }

  protected override void OnStart() {
    base.OnStart();
    GameEvents.onVesselWasModified.Add(OnVesselWasModified);
    GameEvents.onPartDeCoupleNewVesselComplete.Add(OnVesselSplit);
    GameEvents.onVesselsUndocking.Add(OnVesselsUndocking);
    GameEvents.onVesselGoOnRails.Add(OnVesselGoOnRails);
    GameEvents.onVesselGoOffRails.Add(OnVesselGoOffRails);
  }

  private void OnDestroy() {
    GameEvents.onVesselPartCountChanged.Remove(OnPartCountChanged);
    GameEvents.onVesselWasModified.Remove(OnVesselWasModified);
    GameEvents.onPartDeCoupleNewVesselComplete.Remove(OnVesselSplit);
    GameEvents.onVesselsUndocking.Remove(OnVesselsUndocking);
    GameEvents.onVesselGoOnRails.Remove(OnVesselGoOnRails);
    GameEvents.onVesselGoOffRails.Remove(OnVesselGoOffRails);
    Virtual?.Systems?.Transmission?.CancelActive();
    CancelCommandLink();
    if (vessel != null)
      NovaCommunicationsAddon.Instance?.RemoveVesselEndpoint(vessel.id);
  }

  // Rails-state transitions change which Motion model the comms addon
  // should attach: on-rails Kepler vessels get analytical solver; off-
  // rails (active flight) gets numerical fallback. Refresh the
  // endpoint on each transition; the refresh drops + re-adds the
  // network endpoint, so the per-vessel transmission system needs
  // its endpoint reference rewired too.
  private void OnVesselGoOnRails(Vessel v) {
    if (v != vessel) return;
    NovaCommunicationsAddon.Instance?.RefreshVesselEndpoint(v);
    WireScienceTransmission();
  }
  private void OnVesselGoOffRails(Vessel v) {
    if (v != vessel) return;
    NovaCommunicationsAddon.Instance?.RefreshVesselEndpoint(v);
    WireScienceTransmission();
  }

  /// <summary>
  /// First-launch path: fires from Vessel.Initialize after findVesselParts
  /// populates vessel.parts (line 2018) but before InitializeModules calls
  /// PartModule.OnStart (line 2023). Builds VirtualVessel from live parts
  /// so it's ready before any NovaPartModule starts.
  ///
  /// For revert/load, Virtual already exists from Load_Postfix — this is a no-op.
  /// </summary>
  private void OnPartCountChanged(Vessel v) {
    if (v != vessel || Virtual != null) return;
    if (vessel.parts == null || vessel.parts.Count == 0) return;

    Virtual = BuildVirtualVesselFromParts(vessel.parts);
    ConfigureVirtualVessel(Virtual);
    RebuildCachedStructure();
  }

  /// <summary>
  /// Revert/save-load/background path: called by ProtoVesselPatches.Load_Postfix
  /// when a ProtoVessel is loaded from a captured ConfigNode.
  /// </summary>
  public void OnVesselFullLoad(ConfigNode node) {
    Virtual = BuildVirtualVesselFromConfig(node);
    ConfigureVirtualVessel(Virtual);

    // Cache structure when parts are available (may be deferred to FixedUpdate)
    if (vessel != null && vessel.loaded && vessel.parts != null && vessel.parts.Count > 0)
      RebuildCachedStructure();
  }

  /// <summary>
  /// Configure a newly built VirtualVessel with logging and time.
  /// </summary>
  private void ConfigureVirtualVessel(VirtualVessel vv) {
    vv.Log = NovaLog.Log;
    RegisterCommsEndpoint();
  }

  // Push this vessel into the comms network. Called whenever Virtual
  // is built or modified — antennas come from Virtual.AllComponents
  // (already populated at this point, even before PartModule.OnStart).
  // Idempotent: AddVesselEndpoint short-circuits if already present.
  // Also called from NovaCommunicationsAddon.Awake to catch up
  // vessels whose VesselModule loaded before the addon was online —
  // a normal Flight-scene transition where Load_Postfix fires
  // sometime in the load sequence and KSPAddon Awake happens after.
  public void RegisterCommsEndpoint() {
    var addon = NovaCommunicationsAddon.Instance;
    if (addon == null || Virtual == null) return;
    var antennas = Virtual.AllComponents().OfType<Antenna>().ToList();
    if (antennas.Count == 0) return;
    addon.AddVesselEndpoint(vessel, antennas);
    WireScienceTransmission();
    WireCommandLink();
  }

  // Re-register: drop the existing endpoint and re-add. Used when the
  // antenna set changes (parts added/removed, dock/undock).
  private void RebuildCommsEndpoint() {
    CancelCommandLink();
    NovaCommunicationsAddon.Instance?.RemoveVesselEndpoint(vessel.id);
    RegisterCommsEndpoint();
  }

  // Hand the per-vessel transmission system its network handles. Pulled
  // out of RegisterCommsEndpoint so we can rewire on dock/undock without
  // duplicating the antenna-registration body.
  private void WireScienceTransmission() {
    var addon = NovaCommunicationsAddon.Instance;
    if (addon == null || Virtual?.Systems == null) return;
    var ep = addon.GetVesselEndpoint(vessel.id);
    if (ep == null || addon.KscEndpoint == null) return;
    Virtual.Systems.Transmission.SetCommNetwork(
        addon.Network, ep, addon.KscEndpoint,
        NovaScienceArchive.Instance,
        vessel.persistentId, vessel.vesselName ?? "");
  }

  // ── StoredCommands link ──────────────────────────────────────────
  // Per-vessel Receive<string>("StoredCommands", probe.CommandReceiveRateBps).
  // Submitted alongside the antenna endpoint; cancelled on
  // dock/undock/destroy. The job's AllocatedRateBps is rolled into
  // the primary Probe's lerp each FixedUpdate.
  private Receive<string> commandReceiveJob;

  private void WireCommandLink() {
    var addon = NovaCommunicationsAddon.Instance;
    if (addon == null) return;
    var ep = addon.GetVesselEndpoint(vessel.id);
    if (ep == null) return;
    var probe = PrimaryProbe;
    if (probe == null) return;
    if (probe.CommandReceiveRateBps <= 0) return;

    // RegisterCommsEndpoint can fire more than once for the same vessel
    // — once from ConfigureVirtualVessel during load and once from the
    // addon's Awake catch-up loop, in either order depending on which
    // KSPAddon instance comes online first. Without cancelling first,
    // both submissions stay Active and the allocator water-fills them
    // by splitting the KSC→vessel link between the duplicates, so each
    // receive sees half the achievable rate. Cancel-then-submit makes
    // this idempotent: the field always points at the only Active job.
    CancelCommandLink();

    commandReceiveJob = new Receive<string>(ep, "StoredCommands", probe.CommandReceiveRateBps);
    addon.Network.Submit(commandReceiveJob);
  }

  private void CancelCommandLink() {
    if (commandReceiveJob == null) return;
    NovaCommunicationsAddon.Instance?.Network?.Cancel(commandReceiveJob);
    commandReceiveJob = null;
  }

  // ── Primary Probe ────────────────────────────────────────────────
  // The vessel's authoritative command store: largest-capacity Probe
  // on the vessel. Multi-probe summation is a follow-up. Cached and
  // invalidated by the same `rcsModulesDirty` flag the topology hooks
  // already maintain — it flips on every onVesselWasModified and
  // RebuildTopology, which is exactly when the Probe roster could
  // change too.
  private Probe cachedPrimaryProbe;
  private bool primaryProbeDirty = true;

  public Probe PrimaryProbe {
    get {
      if (primaryProbeDirty) {
        cachedPrimaryProbe = Virtual?.AllComponents().OfType<Probe>()
            .OrderByDescending(p => p.CommandCapacityBytes).FirstOrDefault();
        primaryProbeDirty = false;
      }
      return cachedPrimaryProbe;
    }
  }

  // ── Control authorization ────────────────────────────────────────
  // Recomputed once per FixedUpdate against the primary Probe's
  // StoredCommands ledger. False = the ledger couldn't pay for this
  // tick's input, so attitude inputs zero out and engines see throttle
  // 0. Crewed vessels and probe-less debris are unconditionally true
  // (kerbals fly by hand; debris has no input either way).
  private bool controlAuthorizedThisTick = true;
  public bool ControlAuthorizedThisTick => controlAuthorizedThisTick;

  // Roll the comms allocator's current AllocatedRateBps for this
  // vessel's StoredCommands receive job into the primary Probe's
  // ledger. Cheap when the rate hasn't changed (no rebaseline).
  private void PullCommandRefill() {
    var probe = PrimaryProbe;
    if (probe == null || commandReceiveJob == null) return;
    var rate = commandReceiveJob.AllocatedRateBps;
    if (rate != probe.CommandRefillBps) probe.SetCommandRefillRate(rate);
  }

  // Decide whether this FixedUpdate's input passes the StoredCommands
  // gate. Vessels with crew (kerbal pilot) or no probe at all are
  // unconditionally authorized — gating is the probe-only sanction
  // for an idle KSC link.
  //
  // Cost model — only RAW user input counts; SAS torque is free.
  //   Attitude: continuous spend while the user holds the stick.
  //     rate = |pitch,yaw,roll,X,Y,Z| · InputCostBps  (B/s)
  //   Throttle: discrete spend on adjustment, not on holding.
  //     pulse = |Δthrottle| · InputCostBps            (bytes, this tick)
  // Read from FlightInputHandler.state — that's the pre-SAS axis read
  // (stock ModuleReactionWheel uses the same source). vessel.ctrlState
  // is the SAS-modified output and would charge the player for the
  // autopilot's torque commands.
  //
  // Display vs deduction:
  //   - Real deduction this tick = `spendThisTick` bytes via
  //     TrySpendCommands. That's what actually moves the ledger.
  //   - Display rate (probe.CommandConsumeBps) = an EMA of
  //     RecentSpendBytes divided by CommandSmoothingWindowSec. A
  //     50-byte throttle Δ would otherwise show as 2500 B/s for one
  //     fixed step (50 ÷ 0.02) and never be sampled by the 10 Hz
  //     telemetry; smoothed it reads ~50 B/s decaying over ~1s, which
  //     integrates back to the same 50 bytes. Continuous attitude
  //     spend converges to the literal attitudeBps in steady state.
  //
  // The EMA decays every tick regardless of active-vessel state so a
  // vessel switch can't strand a frozen rate on the panel.
  private const double CommandSmoothingWindowSec = 1.0;
  private double lastUserThrottle;
  private bool   lastUserThrottleReady;

  private void AuthorizeControlForThisTick() {
    controlAuthorizedThisTick = true;
    var probe = PrimaryProbe;
    var dt = Time.fixedDeltaTime;

    if (probe == null) return;

    // EMA decay first — applies whether or not we add new spend below.
    var decay = System.Math.Max(0.0, 1.0 - dt / CommandSmoothingWindowSec);
    probe.RecentSpendBytes *= decay;

    double spendThisTick = ComputeSpendThisTick(probe, dt);
    if (spendThisTick > 0) {
      probe.RecentSpendBytes += spendThisTick;
      if (!probe.TrySpendCommands(spendThisTick))
        controlAuthorizedThisTick = false;
    }

    probe.CommandConsumeBps = probe.RecentSpendBytes / CommandSmoothingWindowSec;
  }

  private double ComputeSpendThisTick(Probe probe, double dt) {
    if (vessel == null) return 0;
    if (vessel.GetCrewCount() > 0) return 0;
    if (probe.InputCostBps <= 0) return 0;

    // FlightInputHandler.state is static — it reflects whatever the
    // player is doing to the *active* vessel. Background NovaVesselModule
    // FixedUpdates would otherwise charge their probes for input the
    // player aimed at someone else. Background vessels generate no user
    // input, full stop.
    if (vessel != FlightGlobals.ActiveVessel) {
      lastUserThrottleReady = false;
      return 0;
    }

    var raw = FlightInputHandler.state;
    if (raw == null) return 0;

    double attitudeMag = System.Math.Sqrt(
        raw.pitch * raw.pitch + raw.yaw * raw.yaw + raw.roll * raw.roll
      + raw.X     * raw.X     + raw.Y   * raw.Y   + raw.Z    * raw.Z);

    double throttleDelta = lastUserThrottleReady
        ? System.Math.Abs(raw.mainThrottle - lastUserThrottle)
        : 0;
    lastUserThrottle      = raw.mainThrottle;
    lastUserThrottleReady = true;

    // Attitude: continuous bytes/tick. Throttle: one-tick byte pulse on
    // |Δthrottle|. Both feed the same accumulator; the smoothing wire
    // makes the pulse readable on the panel.
    double attitudeBytes = attitudeMag * probe.InputCostBps * dt;
    double throttleBytes = throttleDelta * probe.InputCostBps;
    return attitudeBytes + throttleBytes;
  }

  public void InvalidateRcsCache() {
    rcsModulesDirty = true;
    rcsSolver = null;
    primaryProbeDirty = true;
  }

  public void InvalidateSolarData() {
    solarDataDirty = true;
  }

  private void OnVesselWasModified(Vessel v) {
    if (v != vessel || Virtual == null) return;
    RebuildTopology();
    InvalidateRcsCache();
    InvalidateSolarData();
    RebuildCommsEndpoint();
  }

  private void OnVesselSplit(Vessel oldVessel, Vessel newVessel) {
    HandleVesselSplit(oldVessel, newVessel);
  }

  private void OnVesselsUndocking(Vessel oldVessel, Vessel newVessel) {
    HandleVesselSplit(oldVessel, newVessel);
  }

  private void HandleVesselSplit(Vessel oldVessel, Vessel newVessel) {
    if (oldVessel != vessel) return;
    if (Virtual == null) return;

    var newMod = newVessel.FindVesselModuleImplementing<NovaVesselModule>();
    if (newMod == null) return;

    var newPartIds = new HashSet<uint>(newVessel.parts.Select(p => p.persistentId));
    var extracted = Virtual.ExtractParts(newPartIds);

    if (extracted.Count > 0) {
      var partNames = new Dictionary<uint, string>();
      var partDryMasses = new Dictionary<uint, double>();
      foreach (var p in newVessel.parts) {
        partNames[p.persistentId] = p.partInfo?.name ?? "";
        partDryMasses[p.persistentId] = p.prefabMass * 1000;
      }

      newMod.Virtual = VirtualVessel.FromExistingParts(
        extracted, BuildParentMap(newVessel), partNames, partDryMasses,
        Planetarium.GetUniversalTime());
      ConfigureVirtualVessel(newMod.Virtual);
    }

    RebuildTopology();
  }

  private void RebuildTopology() {
    // InitializeSolver throws away the old VesselSystems. Cancel any
    // in-flight science transmit first so its Packet doesn't orphan
    // on the network with a stale source endpoint.
    Virtual?.Systems?.Transmission?.CancelActive();
    Virtual.UpdatePartTree(BuildParentMap(vessel));
    Virtual.InitializeSolver(Planetarium.GetUniversalTime());
    RebuildCachedStructure();
  }

  private void RebuildCachedStructure() {
    if (vessel == null || vessel.parts == null || vessel.parts.Count == 0) return;
    var (structure, _) = NovaVesselBuilder.BuildFromParts(vessel.parts, p => p.persistentId);
    structure.VesselId = vessel.id.ToString();
    structure.PersistentId = vessel.persistentId;
    CachedStructure = structure;
    CachedStructureHash = NovaVesselBuilder.ComputeStructureHash(structure);
    NovaVesselStructureTopic.MarkVesselDirty(vessel.id.ToString("D"));
  }

  private static Dictionary<uint, uint?> BuildParentMap(Vessel v) {
    var map = new Dictionary<uint, uint?>();
    foreach (var p in v.parts)
      map[p.persistentId] = p.parent?.persistentId;
    return map;
  }

  /// <summary>
  /// Resolve the prefab dry mass for a part (in kg).
  /// </summary>
  private static double ResolvePrefabMass(string partName) {
    var partInfo = PartLoader.getPartInfoByName(partName);
    return partInfo != null ? partInfo.partPrefab.mass * 1000 : 0;
  }

  /// <summary>
  /// Build a VirtualVessel from live Part objects + prefab configs.
  /// Used by the first-launch path (OnPartCountChanged).
  /// </summary>
  private VirtualVessel BuildVirtualVesselFromParts(List<Part> parts) {
    var vv = new VirtualVessel();

    var parentMap = BuildParentMap(vessel);

    foreach (var part in parts) {
      var partName = part.partInfo?.name;
      if (partName == null) continue;

      var partInfo = PartLoader.getPartInfoByName(partName);
      if (partInfo == null) continue;

      // Prefer live module.Components when populated — that path carries
      // proto-loaded state from NovaPartInstantiator (incl. editor-time
      // reconfigurations like Set Tank Config). Aggregate across every
      // NovaPartModule on the part (each module owns one component pre-
      // OnStart; post-OnStart they all share the full list, so dedupe
      // by reference). Falls back to the prefab for brand-new in-flight
      // construction where neither NPI nor OnStart has run.
      var partModules = part.Modules.OfType<NovaPartModule>().ToList();
      var components = partModules
        .Where(m => m.Components != null)
        .SelectMany(m => m.Components)
        .Distinct()
        .ToList();
      if (components.Count == 0)
        components = CreateComponentsFromPrefab(partInfo);
      var dryMassKg = partInfo.partPrefab.mass * 1000;

      vv.AddPart(part.persistentId, partName, dryMassKg, components);
    }

    vv.UpdatePartTree(parentMap);
    vv.InitializeSolver(Planetarium.GetUniversalTime());
    return vv;
  }

  /// <summary>
  /// Build a VirtualVessel from a saved vessel ConfigNode (PART tree).
  /// Used by the revert/load path (OnVesselFullLoad).
  /// </summary>
  private static VirtualVessel BuildVirtualVesselFromConfig(ConfigNode vesselNode) {
    var vv = new VirtualVessel();
    var partNodes = vesselNode.GetNodes("PART");
    if (partNodes.Length == 0) return vv;

    // Determine format and build parent map
    var isSaveFormat = partNodes[0].GetValue("name") != null
                    && partNodes[0].GetValue("part") == null;

    var parentMap = new Dictionary<uint, uint?>();
    if (isSaveFormat) {
      var ids = new List<uint>();
      foreach (var partNode in partNodes) {
        var persistentId = uint.Parse(partNode.GetValue("persistentId"));
        ids.Add(persistentId);
        parentMap[persistentId] = null;
      }
      for (int i = 0; i < partNodes.Length; i++) {
        var parentIdx = int.Parse(partNodes[i].GetValue("parent")
          ?? throw new Exception("PART node missing 'parent' field"));
        if (parentIdx != 0 || i != 0)
          parentMap[ids[i]] = ids[parentIdx];
      }
    } else {
      var partFieldToId = new Dictionary<string, uint>();
      foreach (var partNode in partNodes) {
        var partField = partNode.GetValue("part")
          ?? throw new Exception("PART node missing 'part' field");
        var persistentId = uint.Parse(partNode.GetValue("persistentId"));
        partFieldToId[partField] = persistentId;
        parentMap[persistentId] = null;
      }
      foreach (var partNode in partNodes) {
        var parentId = partFieldToId[partNode.GetValue("part")];
        foreach (var link in partNode.GetValues("link")) {
          if (partFieldToId.TryGetValue(link, out var childId))
            parentMap[childId] = parentId;
        }
      }
    }

    // Load components from prefab MODULE configs
    foreach (var partNode in partNodes) {
      var id = uint.Parse(partNode.GetValue("persistentId"));
      var partName = partNode.GetValue("name")
                  ?? partNode.GetValue("part")?.Split('_')[0];
      if (partName == null) continue;

      var partInfo = PartLoader.getPartInfoByName(partName);
      if (partInfo == null) continue;

      var components = CreateComponentsFromPrefab(partInfo);
      var dryMassKg = partInfo.partPrefab.mass * 1000;

      vv.AddPart(id, partName, dryMassKg, components);
    }

    vv.UpdatePartTree(parentMap);
    double time;
    try { time = Planetarium.GetUniversalTime(); }
    catch { time = 0; }
    vv.InitializeSolver(time);
    return vv;
  }

  /// <summary>
  /// Create VirtualComponents from a part's prefab MODULE configs.
  /// </summary>
  private static List<VirtualComponent> CreateComponentsFromPrefab(AvailablePart partInfo) {
    var components = new List<VirtualComponent>();
    foreach (var moduleNode in partInfo.partConfig.GetNodes("MODULE")) {
      var moduleName = moduleNode.GetValue("name");
      if (moduleName == null || !ComponentFactory.IsHgsModule(moduleName)) continue;
      components.Add(ComponentFactory.Create(moduleNode));
    }
    return components;
  }

  public void FixedUpdate() {
    if (Virtual == null) EnsureVirtual();
    if (Virtual == null) return;
    if (solarDataDirty) {
      solarDataDirty = false;
      Virtual.ComputeSolarRates();
      Virtual.Invalidate();
    }
    if (NeedsSolverRebuild) {
      NeedsSolverRebuild = false;
      Virtual.InitializeSolver(Planetarium.GetUniversalTime());
    }
    if (Virtual.Context == null) Virtual.Context = new KspVesselContext(vessel);
    PullCommandRefill();
    AuthorizeControlForThisTick();
    SolveAttitude();
    Virtual.Tick(Planetarium.GetUniversalTime());

    // Telemetry: post-tick part state is fresh — mark dirty so the
    // 10 Hz broadcaster picks up new rates / SOC / file fidelity.
    // Cheap O(parts × topics) walk; topics only emit on dirty, so
    // unsubscribed parts are no-ops.
    if (vessel != null && vessel.parts != null) {
      for (int i = 0; i < vessel.parts.Count; i++) {
        var p = vessel.parts[i];
        if (p == null) continue;
        NovaPartTopic.MarkPartDirty(p.persistentId);
        NovaScienceTopic.MarkPartDirty(p.persistentId);
        NovaStorageTopic.MarkPartDirty(p.persistentId);
      }
    }
  }

  /// <summary>
  /// Lazy VirtualVessel creation for save-load paths where Load_Postfix
  /// fires before vesselRef or VesselModules are available.
  /// </summary>
  private void EnsureVirtual() {
    var node = ProtoVesselPatches.GetCapturedNode(vessel.protoVessel);
    if (node == null) return;
    OnVesselFullLoad(node);
  }


  private List<NovaRcsModule> cachedRcsModules;
  private List<NovaReactionWheelModule> cachedWheelModules;
  private List<NovaEngineModule> cachedGimbalEngines;
  private bool rcsModulesDirty = true;
  private int rcsLogCounter;
  private RcsSolver rcsSolver;

  // Solver array layout: [RCS nozzles][wheel slots][gimbal slots].
  // We track each block's start index so the per-tick MaxThrottle
  // update for gimbals knows where to write, and the post-Solve
  // distribution loops know where to read.
  private int rcsSlotCount;
  private int wheelSlotCount;
  private int gimbalSlotStart;

  // Per-gimbal-engine vessel-local geometry, captured at solver-build
  // time. Engine pivot, gimbal axes, nominal thrust direction, and
  // sideMag are rigid-vessel invariants — they don't move during a
  // burn and so don't need re-reading from Unity transforms each tick.
  // Lever (engine pivot − CoM) is the only piece that drifts; we
  // refresh slot torques from these cached parts plus live CoM
  // whenever CoM has moved past `GimbalCoMThreshold` since the last
  // refresh (or on the first tick after a fresh build, where the
  // build-time CoM can be transient — staging fires events before
  // VesselPrecalculate has redone its mass walk for the new part set).
  private struct GimbalSlotGeometry {
    public Vec3d EnginePos;
    public Vec3d PitchSide;
    public Vec3d YawSide;
    public double SideMag;
  }
  private GimbalSlotGeometry[] gimbalSlotGeom;
  private Vec3d lastGimbalRefreshCoM;
  private bool gimbalRefreshPending;
  private const double GimbalCoMThreshold = 0.1;

  // RCS input cached from Update() to match stock ModuleRCS timing.
  private Vec3d cachedInputLin;
  private Vec3d cachedInputRot;
  private bool inputReady;

  /// <summary>
  /// Read control input in Update (same timing as stock ModuleRCS) so we
  /// see post-FlightInputHandler sign conventions.
  /// </summary>
  public void Update() {
    if (Virtual == null || vessel == null) return;

    var cs = vessel.ctrlState;
    cachedInputRot = new Vec3d(-cs.pitch, -cs.roll, -cs.yaw);
    cachedInputLin = new Vec3d(-cs.X, -cs.Z, cs.Y);
    inputReady = true;
  }

  private void CollectModules() {
    if (!rcsModulesDirty && cachedRcsModules != null) return;

    cachedRcsModules = new List<NovaRcsModule>();
    cachedWheelModules = new List<NovaReactionWheelModule>();
    cachedGimbalEngines = new List<NovaEngineModule>();

    foreach (var part in vessel.parts) {
      var rcs = part.FindModuleImplementing<NovaRcsModule>();
      if (rcs != null && rcs.ThrusterCount > 0)
        cachedRcsModules.Add(rcs);

      var wheel = part.FindModuleImplementing<NovaReactionWheelModule>();
      if (wheel?.Wheel != null)
        cachedWheelModules.Add(wheel);

      var engine = part.FindModuleImplementing<NovaEngineModule>();
      if (engine?.Engine != null
          && engine.Engine.GimbalRangeRad > 0
          && engine.GimbalTransform != null)
        cachedGimbalEngines.Add(engine);
    }
    rcsModulesDirty = false;
  }

  private void SolveAttitude() {
    CollectModules();

    bool rcsOn = vessel.ActionGroups[KSPActionGroup.RCS];
    var rcsModules = rcsOn ? cachedRcsModules : new List<NovaRcsModule>();
    var wheelModules = cachedWheelModules;
    var gimbalEngines = cachedGimbalEngines;

    if (rcsModules.Count == 0 && wheelModules.Count == 0 && gimbalEngines.Count == 0) return;

    // When RCS is off, clear all thruster throttles.
    if (!rcsOn) {
      foreach (var mod in cachedRcsModules) mod.ClearThrottles();
    }

    if (!inputReady) return;
    var inputRot = cachedInputRot;
    var inputLin = cachedInputLin;

    // StoredCommands gate — when the ledger couldn't pay for this
    // tick's input (see AuthorizeControlForThisTick), drive the rest
    // of the solver as if the player let go of the stick.
    if (!controlAuthorizedThisTick) {
      inputRot = Vec3d.Zero;
      inputLin = Vec3d.Zero;
    }

    bool hasInput = inputRot.SqrMagnitude > 1e-6 || inputLin.SqrMagnitude > 1e-6;
    if (!hasInput) {
      foreach (var mod in rcsModules) mod.ClearThrottles();
      foreach (var mod in wheelModules) {
        var w = mod.Wheel;
        // Intensity changes don't invalidate the LP — the wheel's
        // off-LP buffer absorbs them; the LP only re-solves when the
        // buffer hysteresis flips. See ReactionWheel.cs.
        w.ThrottlePitch = 0;
        w.ThrottleRoll = 0;
        w.ThrottleYaw = 0;
      }
      // Zero gimbal deflections too — the apply-rotation code in
      // NovaEngineModule.FixedUpdate reads these every tick, so a
      // hands-off frame must drive the bell back to centred.
      foreach (var mod in gimbalEngines) {
        var e = mod.Engine;
        e.GimbalPitchDeflection = 0;
        e.GimbalYawDeflection = 0;
      }
      return;
    }

    // Count actuators: RCS nozzles + 6 wheel slots + 4 gimbal slots.
    int nRcs = 0;
    foreach (var mod in rcsModules) nRcs += mod.ThrusterCount;
    int nWheelSlots = wheelModules.Count * 6;
    int nGimbalSlots = gimbalEngines.Count * 4;
    int totalActuators = nRcs + nWheelSlots + nGimbalSlots;
    rcsSlotCount = nRcs;
    wheelSlotCount = nWheelSlots;
    gimbalSlotStart = nRcs + nWheelSlots;

    // Build or rebuild the solver.
    if (rcsSolver == null || rcsSolver.ThrusterCount != totalActuators) {
      rcsSolver = new RcsSolver(totalActuators);
      rcsSolver.Log = NovaLog.Log;

      var thrusters = new RcsSolver.Thruster[totalActuators];
      var refXform = vessel.ReferenceTransform;
      // Build-time CoM for gimbal lever-arm calc. The QP's BuildQ
      // already re-derives RCS torques against the live CoM via
      // `positions[]`, but gimbal slots are pure-torque (the slot's
      // Torque vector IS the lever × side-force product), so we bake
      // the CoM in here. CoMThreshold-driven solver rebuilds will
      // recompute when the lever drift starts to matter.
      var buildCoMV = refXform.InverseTransformPoint(vessel.CoM);
      var buildCoM = new Vec3d(buildCoMV.x, buildCoMV.y, buildCoMV.z);
      int ti = 0;

      // RCS thrusters.
      foreach (var mod in rcsModules) {
        foreach (var t in mod.ThrusterTransforms) {
          var dir = mod.UseZaxis ? t.forward : t.up;
          var localPos = refXform.InverseTransformPoint(t.position);
          var localDir = refXform.InverseTransformDirection(-dir);
          thrusters[ti++] = new RcsSolver.Thruster {
            Position = new Vec3d(localPos.x, localPos.y, localPos.z),
            Direction = new Vec3d(localDir.x, localDir.y, localDir.z).Normalized,
            MaxPower = mod.ThrusterPower,
          };
        }
      }

      // Reaction wheel virtual thrusters: 6 per wheel (±pitch, ±roll, ±yaw).
      foreach (var mod in wheelModules) {
        var w = mod.Wheel;
        double pitch = w.PitchTorque;
        double roll = w.RollTorque;
        double yaw = w.YawTorque;
        thrusters[ti++] = new RcsSolver.Thruster { Torque = new Vec3d(+pitch, 0, 0) };
        thrusters[ti++] = new RcsSolver.Thruster { Torque = new Vec3d(-pitch, 0, 0) };
        thrusters[ti++] = new RcsSolver.Thruster { Torque = new Vec3d(0, +roll, 0) };
        thrusters[ti++] = new RcsSolver.Thruster { Torque = new Vec3d(0, -roll, 0) };
        thrusters[ti++] = new RcsSolver.Thruster { Torque = new Vec3d(0, 0, +yaw) };
        thrusters[ti++] = new RcsSolver.Thruster { Torque = new Vec3d(0, 0, -yaw) };
      }

      // Engine gimbal slots: 4 per engine (+pitch, -pitch, +yaw, -yaw).
      // Cache rigid-vessel geometry per engine — engine pivot, gimbal
      // axes, nominal thrust direction, and sideMag don't move during
      // a burn. Slot torques (lever × F_lat) drift with CoM, so we
      // seed them with build-time CoM here and refresh below whenever
      // CoM moves past `GimbalCoMThreshold`.
      gimbalSlotGeom = new GimbalSlotGeometry[gimbalEngines.Count];
      for (int gi = 0; gi < gimbalEngines.Count; gi++) {
        var mod = gimbalEngines[gi];
        var e = mod.Engine;
        var gt = mod.GimbalTransform;
        var localPosV = refXform.InverseTransformPoint(gt.position);
        var pitchAxisV = refXform.InverseTransformDirection(gt.right).normalized;
        var yawAxisV = refXform.InverseTransformDirection(gt.up).normalized;
        // Nominal thrust force direction (zero deflection), captured
        // by NovaEngineModule at OnStart from the same `-thrustTransform.
        // forward` expression FixedUpdate uses to apply force — keeps
        // the QP slot-torque signs in lockstep with the physics.
        // Using `gt.forward` would be model-dependent (some engines
        // have gt pointing "up", some "down").
        var thrustDirV = refXform.InverseTransformDirection(mod.NominalThrustDirectionWorld).normalized;

        var pos = new Vec3d(localPosV.x, localPosV.y, localPosV.z);
        var pitchAxis = new Vec3d(pitchAxisV.x, pitchAxisV.y, pitchAxisV.z);
        var yawAxis = new Vec3d(yawAxisV.x, yawAxisV.y, yawAxisV.z);
        var thrustDir = new Vec3d(thrustDirV.x, thrustDirV.y, thrustDirV.z);
        var sideMag = e.Thrust * Math.Sin(e.GimbalRangeRad);

        var pitchSide = Vec3d.Cross(pitchAxis, thrustDir);
        if (pitchSide.SqrMagnitude > 1e-12) pitchSide = pitchSide.Normalized;
        var yawSide = Vec3d.Cross(yawAxis, thrustDir);
        if (yawSide.SqrMagnitude > 1e-12) yawSide = yawSide.Normalized;

        gimbalSlotGeom[gi] = new GimbalSlotGeometry {
          EnginePos = pos,
          PitchSide = pitchSide,
          YawSide = yawSide,
          SideMag = sideMag,
        };

        var lever = pos - buildCoM;
        var pitchTorque = Vec3d.Cross(lever, pitchSide * sideMag);
        var yawTorque = Vec3d.Cross(lever, yawSide * sideMag);

        thrusters[ti++] = new RcsSolver.Thruster { Torque = pitchTorque, IsGimbal = true };
        thrusters[ti++] = new RcsSolver.Thruster { Torque = -1.0 * pitchTorque, IsGimbal = true };
        thrusters[ti++] = new RcsSolver.Thruster { Torque = yawTorque, IsGimbal = true };
        thrusters[ti++] = new RcsSolver.Thruster { Torque = -1.0 * yawTorque, IsGimbal = true };
      }
      // Force a torque refresh on the first tick after a fresh build:
      // `buildCoM` here can be transient at staging (events fire from
      // mid-Update before the next FixedUpdate's VesselPrecalculate
      // has rerun the mass walk for the post-stage parts list).
      gimbalRefreshPending = true;

      rcsSolver.SetThrusters(thrusters);

      NovaLog.Log($"[Attitude] {rcsModules.Count} RCS modules ({nRcs} nozzles), " +
                  $"{wheelModules.Count} reaction wheels ({nWheelSlots} virtual slots), " +
                  $"{gimbalEngines.Count} gimbal engines ({nGimbalSlots} virtual slots)");
    }

    // Per-tick gimbal slot updates.
    //
    //   MaxThrottle = engine's current `NormalizedOutput`. A
    //   30 %-throttle engine exposes 30 % of full-deflection torque;
    //   an idle engine zero. Updates every tick — cheap, doesn't
    //   invalidate Q.
    //
    //   Slot torque = `lever × F_lat` where `lever = engine_pivot −
    //   vessel.CoM`. The pivot is rigid relative to the vessel; the
    //   CoM drifts as fuel burns and jumps at staging. Once the CoM
    //   crosses the pivot's vessel-local height the slot's torque
    //   sign flips, and if we keep serving the QP the old value, it
    //   fires the +pitch slot for a +X demand while the deflected
    //   bell physically produces −X — runaway spin. We refresh on
    //   CoM drift past `GimbalCoMThreshold` (matches the solver's
    //   own Q-rebuild threshold) plus once unconditionally on the
    //   first tick after a solver rebuild, since `buildCoM` can be
    //   transient at staging.
    var refXformLive = vessel.ReferenceTransform;
    var liveCoMV = refXformLive.InverseTransformPoint(vessel.CoM);
    var liveCoM = new Vec3d(liveCoMV.x, liveCoMV.y, liveCoMV.z);
    bool refreshGimbalTorques = gimbalRefreshPending
        || (liveCoM - lastGimbalRefreshCoM).SqrMagnitude
            > GimbalCoMThreshold * GimbalCoMThreshold;

    for (int gi = 0; gi < gimbalEngines.Count; gi++) {
      var e = gimbalEngines[gi].Engine;
      double maxT = e.NormalizedOutput;
      int slotBase = gimbalSlotStart + gi * 4;
      rcsSolver.SetSlotMaxThrottle(slotBase + 0, maxT);
      rcsSolver.SetSlotMaxThrottle(slotBase + 1, maxT);
      rcsSolver.SetSlotMaxThrottle(slotBase + 2, maxT);
      rcsSolver.SetSlotMaxThrottle(slotBase + 3, maxT);

      if (refreshGimbalTorques) {
        var geom = gimbalSlotGeom[gi];
        var lever = geom.EnginePos - liveCoM;
        var pitchTorque = Vec3d.Cross(lever, geom.PitchSide * geom.SideMag);
        var yawTorque = Vec3d.Cross(lever, geom.YawSide * geom.SideMag);
        rcsSolver.SetSlotTorque(slotBase + 0, pitchTorque);
        rcsSolver.SetSlotTorque(slotBase + 1, -1.0 * pitchTorque);
        rcsSolver.SetSlotTorque(slotBase + 2, yawTorque);
        rcsSolver.SetSlotTorque(slotBase + 3, -1.0 * yawTorque);
      }
    }
    if (refreshGimbalTorques) {
      lastGimbalRefreshCoM = liveCoM;
      gimbalRefreshPending = false;
    }

    // CoM in vessel-local space.
    var localCoM = vessel.ReferenceTransform.InverseTransformPoint(vessel.CoM);

    var input = new RcsSolver.Input {
      CoM = new Vec3d(localCoM.x, localCoM.y, localCoM.z),
      DesiredForce = inputLin,
      DesiredTorque = inputRot,
    };

    var solved = rcsSolver.Solve(input);

    // Log periodically.
    if (rcsLogCounter++ % 50 == 0) {
      var cs = vessel.ctrlState;
      NovaLog.Log($"[Attitude] ctrlState: pitch={cs.pitch:F3} yaw={cs.yaw:F3} roll={cs.roll:F3} X={cs.X:F3} Y={cs.Y:F3} Z={cs.Z:F3}");
      NovaLog.Log($"[Attitude] input: force={input.DesiredForce} torque={input.DesiredTorque} CoM={input.CoM}");
    }

    // Distribute RCS throttles.
    int idx = 0;
    foreach (var mod in rcsModules) {
      int count = mod.ThrusterCount;
      var slice = new double[count];
      for (int i = 0; i < count; i++)
        slice[i] = solved[idx++];
      mod.ApplySolvedThrottles(slice);
    }

    // Distribute reaction wheel torques.
    foreach (var mod in wheelModules) {
      // 6 virtual thrusters per wheel: +pitch, -pitch, +roll, -roll, +yaw, -yaw
      double pPlus = solved[idx++], pMinus = solved[idx++];
      double rPlus = solved[idx++], rMinus = solved[idx++];
      double yPlus = solved[idx++], yMinus = solved[idx++];

      var w = mod.Wheel;
      // Intensity changes don't invalidate the LP — the wheel's off-LP
      // buffer absorbs them; the LP only re-solves when the buffer
      // hysteresis flips. See ReactionWheel.cs.
      w.ThrottlePitch = pPlus - pMinus;
      w.ThrottleRoll = rPlus - rMinus;
      w.ThrottleYaw = yPlus - yMinus;

      // Net torque in vessel-local space, scaled by electricity satisfaction.
      var sat = w.Satisfaction;
      var localTorque = new Vector3(
        (float)(w.ThrottlePitch * w.PitchTorque * sat),
        (float)(w.ThrottleRoll * w.RollTorque * sat),
        (float)(w.ThrottleYaw * w.YawTorque * sat));

      mod.ApplyTorque(localTorque);

      if (rcsLogCounter % 50 == 1) {
        NovaLog.Log($"[Attitude] wheel {mod.part.partInfo.name}: " +
          $"pitch={w.ThrottlePitch:F3} roll={w.ThrottleRoll:F3} yaw={w.ThrottleYaw:F3} " +
          $"localTorque={localTorque}");
      }
    }

    // Distribute gimbal deflections. Slot throttles are capped at
    // `MaxThrottle = NormalizedOutput` (so the QP saturates at the
    // current effective torque, matching what we report to SAS via
    // ITorqueProvider). The PHYSICAL deflection still has to span the
    // full `gimbalRange` at that saturation point, otherwise the
    // engine fires at half-range and produces only `NormalizedOutput²`
    // of full torque — the very mismatch that fed back as oscillation.
    // Divide by `NormalizedOutput` so a saturated slot pair maps to
    // `±1` deflection (which `NovaEngineModule` then multiplies by the
    // full `gimbalRange`).
    foreach (var mod in gimbalEngines) {
      double pPlus = solved[idx++], pMinus = solved[idx++];
      double yPlus = solved[idx++], yMinus = solved[idx++];
      var e = mod.Engine;
      double scale = e.NormalizedOutput > 1e-6 ? 1.0 / e.NormalizedOutput : 0;
      e.GimbalPitchDeflection = (pPlus - pMinus) * scale;
      e.GimbalYawDeflection = (yPlus - yMinus) * scale;

      if (rcsLogCounter % 50 == 1) {
        NovaLog.Log($"[Attitude] gimbal {mod.part.partInfo.name}: " +
          $"pitch={e.GimbalPitchDeflection:F3} yaw={e.GimbalYawDeflection:F3}");
      }
    }
  }
}
