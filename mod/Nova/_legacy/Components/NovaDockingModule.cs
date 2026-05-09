using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Nova;
using Nova.Core.Components;
using Nova.Core.Components.Structural;
using UnityEngine;

namespace Nova.Components;

public class NovaDockingModule : NovaPartModule, ITargetable, IStageSeparator {

  // --- Config fields (from MODULE in .cfg) ---

  [KSPField]
  public string nodeTransformName = "dockingNode";

  [KSPField]
  public string controlTransformName = "";

  [KSPField]
  public string referenceAttachNode = "";

  [KSPField]
  public string nodeType = "size1";

  [KSPField]
  public float acquireRange = 0.5f;

  [KSPField]
  public float acquireMinFwdDot = 0.7f;

  [KSPField]
  public float acquireForce = 2f;

  [KSPField]
  public float acquireTorque = 2f;

  [KSPField]
  public float acquireTorqueRoll = 0f;

  [KSPField(advancedTweakable = true, isPersistant = true, guiActive = true, guiActiveEditor = true,
    guiName = "Acquire Force %", guiUnits = "%")]
  [UI_FloatRange(scene = UI_Scene.All, stepIncrement = 5f, maxValue = 200f, minValue = 0f)]
  public float acquireForceTweak = 100f;

  [KSPField]
  public float captureRange = 0.06f;

  [KSPField]
  public float captureMinFwdDot = 0.998f;

  [KSPField]
  public float acquireMinRollDot = float.MinValue;

  [KSPField]
  public float captureMinRollDot = float.MinValue;

  [KSPField]
  public float captureMaxRvel = 0.3f;

  [KSPField]
  public float undockEjectionForce = 10f;

  [KSPField]
  public float minDistanceToReEngage = 1f;

  [KSPField]
  public bool snapRotation;

  [KSPField]
  public float snapOffset = 90f;

  [KSPField]
  public bool gendered;

  [KSPField]
  public bool genderFemale = true;

  // --- Persisted runtime state ---

  [KSPField(isPersistant = true)]
  public string state = "Ready";

  [KSPField(isPersistant = true)]
  public uint dockedPartUId;

  [KSPField(isPersistant = true)]
  public int dockingNodeModuleIndex;

  // --- Runtime state ---

  public NovaDockingModule otherNode;
  public DockedVesselInfo vesselInfo;
  public Transform nodeTransform;
  public Transform controlTransform;
  public AttachNode referenceNode;
  public HashSet<string> nodeTypes = new();

  private float disengageTimer;
  private bool wasPreAttached;
  private Part recentlyUndockedFrom;

  // --- Lifecycle ---

  public override void OnStart(StartState st) {
    base.OnStart(st);

    nodeTransform = part.FindModelTransform(nodeTransformName);
    if (nodeTransform == null) {
      NovaLog.Log($"[NovaDockingModule] No node transform '{nodeTransformName}' on {part.partInfo.name}");
      return;
    }

    if (controlTransformName == "")
      controlTransform = part.transform;
    else {
      controlTransform = part.FindModelTransform(controlTransformName);
      if (controlTransform == null)
        controlTransform = part.transform;
    }

    if (referenceAttachNode != "")
      referenceNode = part.FindAttachNode(referenceAttachNode);

    // Parse nodeType into set (supports comma-separated)
    foreach (var nt in nodeType.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries))
      nodeTypes.Add(nt);

    // Part must be physically significant for docking joints
    if (part.physicalSignificance != Part.PhysicalSignificance.FULL)
      part.physicalSignificance = Part.PhysicalSignificance.FULL;

    if (HighLogic.LoadedSceneIsFlight)
      StartCoroutine(LateStart());
  }

  private IEnumerator LateStart() {
    yield return null;

    // Restore docked partner reference
    if (state.Contains("Docked")) {
      otherNode = FindOtherNode();
      if (otherNode == null) {
        NovaLog.Log($"[NovaDocking] {part.partInfo.name}: docked partner {dockedPartUId} not found, resetting to Ready");
        state = "Ready";
      } else {
        NovaLog.Log($"[NovaDocking] {part.partInfo.name}: restored docked state with {otherNode.part.partInfo.name}");
      }
    }

    // Detect pre-attached ports (editor-placed docking connections)
    if (state == "Ready" && referenceNode?.attachedPart != null) {
      var attached = referenceNode.attachedPart;
      foreach (var mod in attached.Modules.OfType<NovaDockingModule>()) {
        if (mod.referenceNode?.attachedPart == part) {
          otherNode = mod;
          mod.otherNode = this;
          state = "PreAttached";
          mod.state = "PreAttached";
          NovaLog.Log($"[NovaDocking] {part.partInfo.name}: detected pre-attached partner {mod.part.partInfo.name}");
          break;
        }
      }
    }

    UpdateEventVisibility();
  }

  private NovaDockingModule FindOtherNode() {
    var p = FlightGlobals.FindPartByID(dockedPartUId);
    if (p == null) return null;
    if (dockingNodeModuleIndex >= 0 && dockingNodeModuleIndex < p.Modules.Count)
      return p.Modules[dockingNodeModuleIndex] as NovaDockingModule;
    return p.Modules.OfType<NovaDockingModule>().FirstOrDefault();
  }

  public override void OnLoad(ConfigNode node) {
    base.OnLoad(node);
    if (node.HasValue("state"))
      state = node.GetValue("state");
    if (node.HasValue("dockUId"))
      dockedPartUId = uint.Parse(node.GetValue("dockUId"));
    if (node.HasValue("dockNodeIdx"))
      dockingNodeModuleIndex = int.Parse(node.GetValue("dockNodeIdx"));
    if (node.HasNode("DOCKEDVESSEL")) {
      vesselInfo = new DockedVesselInfo();
      vesselInfo.Load(node.GetNode("DOCKEDVESSEL"));
    }
  }

  public override void OnSave(ConfigNode node) {
    base.OnSave(node);
    node.SetValue("state", state);
    node.SetValue("dockUId", dockedPartUId.ToString());
    node.SetValue("dockNodeIdx", dockingNodeModuleIndex.ToString());
    if (vesselInfo != null)
      vesselInfo.Save(node.AddNode("DOCKEDVESSEL"));
  }

  // --- FixedUpdate state machine ---

  public void FixedUpdate() {
    if (!HighLogic.LoadedSceneIsFlight || nodeTransform == null) return;

    switch (state) {
      case "Ready":
        FixedUpdate_Ready();
        break;
      case "Acquire":
        FixedUpdate_Acquire();
        break;
      case "Disengage":
        FixedUpdate_Disengage();
        break;
      case "PreAttached":
        FixedUpdate_PreAttached();
        break;
      // Docked states: no per-frame work
    }
  }

  private void FixedUpdate_Ready() {
    if (part.packed) return;
    var found = FindNodeApproaches();
    if (found != null) {
      otherNode = found;
      dockedPartUId = found.part.flightID;
      dockingNodeModuleIndex = found.part.Modules.IndexOf(found);
      found.otherNode = this;
      found.dockedPartUId = part.flightID;
      found.dockingNodeModuleIndex = part.Modules.IndexOf(this);

      if (otherNode.vessel == vessel) {
        // Same vessel docking — deferred, skip for now
        return;
      }

      NovaLog.Log($"[NovaDocking] Acquire: {part.partInfo.name} ↔ {found.part.partInfo.name}");
      state = "Acquire";
      found.state = "Acquire";
      UpdateEventVisibility();
      found.UpdateEventVisibility();
    }
  }

  private void FixedUpdate_Acquire() {
    if (part.packed || otherNode == null) {
      state = "Ready";
      UpdateEventVisibility();
      return;
    }

    // Check if port moved too far away
    var dist = (nodeTransform.position - otherNode.nodeTransform.position).sqrMagnitude;
    if (dist > acquireRange * acquireRange * 4f) {
      otherNode.state = "Ready";
      otherNode.otherNode = null;
      otherNode.UpdateEventVisibility();
      otherNode = null;
      state = "Ready";
      UpdateEventVisibility();
      return;
    }

    // Apply magnetic forces
    var delta = otherNode.nodeTransform.position - nodeTransform.position;
    var torqueFwd = Vector3.Cross(nodeTransform.forward, -otherNode.nodeTransform.forward);
    float invDistSq = 1f / Mathf.Max(delta.sqrMagnitude, 0.05f);
    float tweakFactor = acquireForceTweak * 0.01f;

    part.AddForceAtPosition(delta * invDistSq * acquireForce * 0.5f * tweakFactor, nodeTransform.position);
    part.AddTorque(torqueFwd * invDistSq * acquireTorque * 0.5f * tweakFactor);

    float otherTweak = otherNode.acquireForceTweak * 0.01f;
    otherNode.part.AddForceAtPosition(-delta * invDistSq * otherNode.acquireForce * 0.5f * otherTweak, otherNode.nodeTransform.position);
    otherNode.part.AddTorque(-torqueFwd * invDistSq * otherNode.acquireTorque * 0.5f * otherTweak);

    // Check capture
    if (CheckDockContact(this, otherNode, captureRange, captureMinFwdDot, captureMinRollDot)) {
      if (otherNode.vessel != vessel) {
        float relVelSq = (part.rb.velocity - otherNode.part.rb.velocity).sqrMagnitude;
        if (relVelSq <= captureMaxRvel * captureMaxRvel) {
          // Determine docker/dockee by vessel dominance
          if (Vessel.GetDominantVessel(vessel, otherNode.vessel) == vessel) {
            otherNode.DockToVessel(this);
          } else {
            DockToVessel(otherNode);
          }
        }
      }
    }
  }

  private void FixedUpdate_Disengage() {
    disengageTimer -= TimeWarp.fixedDeltaTime;

    bool farEnough = recentlyUndockedFrom == null
      || recentlyUndockedFrom.vessel == null
      || (nodeTransform.position - recentlyUndockedFrom.transform.position).sqrMagnitude
          > minDistanceToReEngage * minDistanceToReEngage;

    if (disengageTimer <= 0 && farEnough) {
      recentlyUndockedFrom = null;
      state = "Ready";
      UpdateEventVisibility();
    }
  }

  private IEnumerator ApplyEjectionForce(int frameDelay, Part a, Vector3 forceA, Part b, Vector3 forceB) {
    for (int i = 0; i < frameDelay; i++)
      yield return null;
    if (a != null) a.AddForce(forceA);
    if (b != null) b.AddForce(forceB);
  }

  private void FixedUpdate_PreAttached() {
    // Pre-attached ports transition to docked on first FixedUpdate
    if (otherNode != null) {
      state = "Docked (docker)";
      otherNode.state = "Docked (dockee)";
      wasPreAttached = true;
      otherNode.wasPreAttached = true;

      dockedPartUId = otherNode.part.flightID;
      dockingNodeModuleIndex = otherNode.part.Modules.IndexOf(otherNode);
      otherNode.dockedPartUId = part.flightID;
      otherNode.dockingNodeModuleIndex = part.Modules.IndexOf(this);

      part.fuelLookupTargets.Add(otherNode.part);
      otherNode.part.fuelLookupTargets.Add(part);

      UpdateEventVisibility();
      otherNode.UpdateEventVisibility();
    } else {
      state = "Ready";
      UpdateEventVisibility();
    }
  }

  // --- Docking ---

  public void DockToVessel(NovaDockingModule other) {
    // Save vessel info on both sides
    vesselInfo = new DockedVesselInfo {
      name = vessel.vesselName,
      vesselType = vessel.vesselType,
      rootPartUId = vessel.rootPart.flightID,
    };
    other.vesselInfo = new DockedVesselInfo {
      name = other.vessel.vesselName,
      vesselType = other.vessel.vesselType,
      rootPartUId = other.vessel.rootPart.flightID,
    };

    NovaLog.Log($"[NovaDocking] DockToVessel: {vessel.vesselName} docking to {other.vessel.vesselName}");

    // Capture other vessel's Virtual parts BEFORE Part.Couple destroys it
    var otherVessel = other.vessel;
    var otherMod = otherVessel.FindVesselModuleImplementing<NovaVesselModule>();
    Dictionary<uint, List<VirtualComponent>> capturedParts = null;
    Dictionary<uint, string> capturedNames = null;
    Dictionary<uint, double> capturedMasses = null;
    if (otherMod?.Virtual != null) {
      var otherPartIds = new HashSet<uint>(otherVessel.parts.Select(p => p.persistentId));
      capturedParts = otherMod.Virtual.ExtractParts(otherPartIds);
      capturedNames = new Dictionary<uint, string>();
      capturedMasses = new Dictionary<uint, double>();
      foreach (var p in otherVessel.parts) {
        capturedNames[p.persistentId] = p.partInfo?.name ?? "";
        capturedMasses[p.persistentId] = p.prefabMass * 1000;
      }
    }

    var activeVessel = FlightGlobals.ActiveVessel;
    uint oldId = vessel.persistentId;
    uint otherId = otherVessel.persistentId;

    GameEvents.onVesselDocking.Fire(oldId, otherId);
    GameEvents.onActiveJointNeedUpdate.Fire(otherVessel);
    GameEvents.onActiveJointNeedUpdate.Fire(vessel);

    // Align vessels
    otherVessel.SetRotation(otherVessel.transform.rotation);
    vessel.SetRotation(
      Quaternion.FromToRotation(nodeTransform.forward, -other.nodeTransform.forward) * vessel.transform.rotation);
    vessel.SetPosition(
      vessel.transform.position - (nodeTransform.position - other.nodeTransform.position), usePristineCoords: true);
    vessel.IgnoreGForces(10);

    // Merge vessels
    part.Couple(other.part);

    // Inject captured parts into surviving vessel's Virtual
    if (capturedParts != null && capturedParts.Count > 0) {
      var survivingMod = vessel.FindVesselModuleImplementing<NovaVesselModule>();
      if (survivingMod?.Virtual != null)
        survivingMod.Virtual.MergeParts(capturedParts, capturedNames, capturedMasses);
    }

    GameEvents.onVesselPersistentIdChanged.Fire(oldId, otherId);

    if (activeVessel == otherVessel) {
      FlightGlobals.ForceSetActiveVessel(vessel);
      FlightInputHandler.SetNeutralControls();
    } else if (vessel == FlightGlobals.ActiveVessel) {
      vessel.MakeActive();
      FlightInputHandler.SetNeutralControls();
    }

    // Set states
    state = "Docked (docker)";
    other.state = "Docked (dockee)";

    dockedPartUId = other.part.flightID;
    dockingNodeModuleIndex = other.part.Modules.IndexOf(other);
    other.dockedPartUId = part.flightID;
    other.dockingNodeModuleIndex = part.Modules.IndexOf(this);

    part.fuelLookupTargets.Add(other.part);
    other.part.fuelLookupTargets.Add(part);

    UpdateEventVisibility();
    other.UpdateEventVisibility();

    // Register parts in persistent lookup tables
    foreach (var p in otherVessel.parts) {
      FlightGlobals.PersistentLoadedPartIds.Add(p.persistentId, p);
      if (p.protoPartSnapshot != null)
        FlightGlobals.PersistentUnloadedPartIds.Add(p.protoPartSnapshot.persistentId, p.protoPartSnapshot);
    }

    GameEvents.onVesselWasModified.Fire(vessel);
    GameEvents.onDockingComplete.Fire(new GameEvents.FromToAction<Part, Part>(part, other.part));
  }

  // --- Undocking ---

  public void Undock() {
    if (otherNode == null) return;

    var parent = part.parent;
    if (parent != otherNode.part) {
      // We're not the docker — delegate to the other side
      otherNode.Undock();
      return;
    }

    part.fuelLookupTargets.Remove(otherNode.part);
    otherNode.part.fuelLookupTargets.Remove(part);

    if (wasPreAttached) {
      // Pre-attached ports were never separate vessels — decouple like a decoupler
      part.decouple();
    } else {
      part.Undock(vesselInfo);
    }

    var otherPart = otherNode.part;
    NovaLog.Log($"[NovaDocking] Undock: {part.partInfo.name} from {otherPart.partInfo.name}");

    StartCoroutine(ApplyEjectionForce(2, part, nodeTransform.forward * (-undockEjectionForce * 0.5f),
      otherPart, nodeTransform.forward * (undockEjectionForce * 0.5f)));

    state = "Disengage";
    disengageTimer = 3f;
    recentlyUndockedFrom = otherPart;

    otherNode.state = "Disengage";
    otherNode.disengageTimer = 3f;
    otherNode.recentlyUndockedFrom = part;

    otherNode.otherNode = null;
    otherNode.vesselInfo = null;
    otherNode.dockedPartUId = 0;
    otherNode.wasPreAttached = false;
    otherNode.UpdateEventVisibility();

    otherNode = null;
    vesselInfo = null;
    dockedPartUId = 0;
    wasPreAttached = false;
    UpdateEventVisibility();
  }

  [KSPAction("Undock", activeEditor = false)]
  public void UndockAction(KSPActionParam param) {
    if (state == "Docked (docker)" || state == "Docked (dockee)") Undock();
  }

  // --- Targeting ---

  [KSPEvent(guiActiveUnfocused = true, guiActive = false, unfocusedRange = 200f, guiName = "Set as Target")]
  public void SetAsTarget() {
    FlightGlobals.fetch.SetVesselTarget(this);
  }

  [KSPEvent(guiActiveUnfocused = true, guiActive = false, unfocusedRange = 200f, guiName = "Unset Target")]
  public void UnsetTarget() {
    FlightGlobals.fetch.SetVesselTarget(null);
  }

  // --- Control from here ---

  [KSPEvent(guiActive = true, guiName = "Control from here")]
  public void MakeReferenceTransform() {
    part.SetReferenceTransform(controlTransform);
    vessel.SetReferenceTransform(part);
  }

  [KSPAction("Control from here")]
  public void MakeReferenceToggle(KSPActionParam act) { MakeReferenceTransform(); }

  // --- Proximity detection ---

  private NovaDockingModule FindNodeApproaches() {
    if (part.packed) return null;

    foreach (var v in FlightGlobals.VesselsLoaded) {
      if (v.packed) continue;

      foreach (var p in v.parts) {
        if (p == part || p.State == PartStates.DEAD) continue;

        foreach (var mod in p.Modules.OfType<NovaDockingModule>()) {
          if (mod.state != "Ready") continue;
          if (!IsCompatible(mod)) continue;
          if (CheckDockContact(this, mod, acquireRange, acquireMinFwdDot, acquireMinRollDot))
            return mod;
        }
      }
    }
    return null;
  }

  private bool IsCompatible(NovaDockingModule other) {
    // Node types must overlap
    bool typeMatch = false;
    foreach (var nt in nodeTypes) {
      if (other.nodeTypes.Contains(nt)) { typeMatch = true; break; }
    }
    if (!typeMatch) return false;

    // Gendered ports must be opposite genders
    if (gendered != other.gendered) return false;
    if (gendered && genderFemale == other.genderFemale) return false;

    // Snap rotation must match
    if (snapRotation != other.snapRotation) return false;
    if (snapRotation && snapOffset != other.snapOffset) return false;

    return true;
  }

  private static bool CheckDockContact(NovaDockingModule m1, NovaDockingModule m2,
      float minDist, float minFwdDot, float minRollDot) {
    if ((m1.nodeTransform.position - m2.nodeTransform.position).sqrMagnitude >= minDist * minDist)
      return false;
    if (Vector3.Dot(m1.nodeTransform.forward, -m2.nodeTransform.forward) <= minFwdDot)
      return false;

    float upDot = Vector3.Dot(m1.nodeTransform.up, m2.nodeTransform.up);
    if (m1.snapRotation) {
      double angle = Math.Acos(Mathf.Clamp(upDot, -1f, 1f));
      if (Vector3.Dot(m1.nodeTransform.up, m2.nodeTransform.right) > 0f)
        angle = Math.PI * 2.0 - angle;
      double snapRad = UtilMath.DegreesToRadians(m1.snapOffset);
      if (snapRad > 0) {
        while (angle > snapRad) angle -= snapRad;
      }
      upDot = (float)Math.Cos(angle);
    }

    return upDot > minRollDot;
  }

  private bool NodeIsTooFar() {
    if (otherNode?.nodeTransform == null) return true;
    return (nodeTransform.position - otherNode.nodeTransform.position).sqrMagnitude
      > minDistanceToReEngage * minDistanceToReEngage;
  }

  // --- Event visibility ---

  private void UpdateEventVisibility() {
    bool isReady = state == "Ready";
    bool isOtherVessel = vessel != FlightGlobals.ActiveVessel;
    Events["SetAsTarget"].active = isReady && isOtherVessel;
    Events["UnsetTarget"].active = false; // Updated by targeting system
  }

  // --- IStageSeparator ---

  public override bool IsStageable() => false;

  public int GetStageIndex(int fallback) {
    if (!stagingEnabled && part.parent != null)
      return part.parent.inverseStage;
    return part.inverseStage;
  }

  // --- ITargetable ---

  public Transform GetTransform() => nodeTransform;
  public Vector3 GetObtVelocity() => vessel.obt_velocity;
  public Vector3 GetSrfVelocity() => vessel.srf_velocity;
  public Vector3 GetFwdVector() => nodeTransform ? nodeTransform.forward : vessel.transform.forward;
  public Vessel GetVessel() => vessel;
  public string GetName() => vessel.vesselName + " - " + part.partInfo.title;
  public string GetDisplayName() => GetName();
  public Orbit GetOrbit() => vessel.orbit;
  public OrbitDriver GetOrbitDriver() => vessel.orbitDriver;
  public VesselTargetModes GetTargetingMode() => VesselTargetModes.DirectionAndVelocity;
  public bool GetActiveTargetable() => false;

  // --- Info ---

  public override string GetInfo() {
    return $"Capture range: {captureRange:0.0###}m\nUndock ejection force: {undockEjectionForce:0.0###}";
  }
}
