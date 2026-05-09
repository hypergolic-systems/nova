using System.Collections.Generic;
using UnityEngine;
using Proto = Nova.Core.Persistence.Protos;

namespace Nova.Persistence;

/// <summary>
/// Instantiates KSP <see cref="Part"/> GameObjects from a
/// <see cref="Proto.VesselStructure"/> + <see cref="Proto.VesselState"/>
/// pair. Used by <see cref="NovaCraftLoader"/> (.nvc) and
/// <see cref="NovaSaveLoader"/> (.nvs); the proto is the single source
/// of truth, and BOTH this method and <c>nova_vessel_new</c> on the
/// Rust side fork from the same bytes.
///
/// The previous version reached into <c>NovaPartModule.Components</c>
/// to call <c>cmp.LoadStructure(ps)</c> + <c>cmp.Load(partState)</c>
/// on every C# VirtualComponent. That entire block is gone — the
/// dynamic state in <c>PartStructure</c> / <c>PartState</c> (battery
/// capacity/contents, tank loadout/amounts, etc.) is consumed by the
/// Rust simulator directly when the same proto bytes are forwarded
/// via <c>nova_vessel_new</c>. C# never re-deserialises into a
/// per-component object graph.
/// </summary>
public static class NovaPartInstantiator {

  public delegate uint AssignPersistentId(Proto.PartStructure ps);

  public static List<Part> Instantiate(
      Proto.VesselStructure structure,
      Proto.VesselState state,
      AssignPersistentId assignId) {

    var partInverseStage = new Dictionary<uint, int>();
    if (state?.Stages != null) {
      for (int i = 0; i < state.Stages.Count; i++) {
        int inverseStage = state.Stages.Count - 1 - i;
        foreach (var partId in state.Stages[i].PartIds)
          partInverseStage[partId] = inverseStage;
      }
    }

    // Instantiate from prefab
    var parts = new List<Part>();
    foreach (var ps in structure.Parts) {
      var info = PartLoader.getPartInfoByName(ps.PartName);
      if (info == null) {
        NovaLog.Log($"Part prefab not found: {ps.PartName}");
        foreach (var p in parts) Object.Destroy(p.gameObject);
        return null;
      }

      var part = Object.Instantiate(info.partPrefab);
      part.gameObject.SetActive(true);
      part.partInfo = info;
      part.name = info.name;
      part.craftID = ps.Id;
      part.persistentId = assignId(ps);
      part.inverseStage = partInverseStage.TryGetValue(ps.Id, out var istg) ? istg : -1;
      parts.Add(part);
    }

    // Build Id → Part lookup for symmetry references
    var partById = new Dictionary<uint, Part>();
    for (int i = 0; i < parts.Count; i++)
      partById[structure.Parts[i].Id] = parts[i];

    // Link parent/children and set up attachments + symmetry
    for (int i = 0; i < structure.Parts.Count; i++) {
      var ps = structure.Parts[i];
      var part = parts[i];

      if (ps.ParentIndex >= 0 && ps.ParentIndex < parts.Count) {
        var parent = parts[ps.ParentIndex];
        part.setParent(parent);
        part.transform.parent = parent.transform;
      }

      if (ps.Attachment != null)
        SetupAttachment(part, ps);

      if (ps.Symmetry != null) {
        part.symMethod = ps.Symmetry.Mirror != null ? SymmetryMethod.Mirror : SymmetryMethod.Radial;
        if (ps.Symmetry.Mirror != null)
          part.mirrorVector = new Vector3(ps.Symmetry.Mirror.X, ps.Symmetry.Mirror.Y, ps.Symmetry.Mirror.Z);
        foreach (var partnerId in ps.Symmetry.Partners)
          if (partById.TryGetValue(partnerId, out var partner))
            part.symmetryCounterparts.Add(partner);
      }
    }

    // No per-component C# loading here. The same `structure` + `state`
    // bytes that drove this method are also forwarded to Rust via
    // `nova_vessel_new`; that's where battery capacity / tank loadout
    // / etc. land.

    // Wire attach node owners
    foreach (var part in parts) {
      foreach (var node in part.attachNodes) {
        node.owner = part;
        node.FindAttachedPart(parts);
      }
      if (part.srfAttachNode != null) {
        part.srfAttachNode.owner = part;
        part.srfAttachNode.FindAttachedPart(parts);
      }
    }

    // Set root-relative positions from proto
    for (int i = 0; i < structure.Parts.Count; i++) {
      var ps = structure.Parts[i];
      var part = parts[i];

      if (ps.RelativePos != null)
        part.orgPos = new Vector3(ps.RelativePos.X, ps.RelativePos.Y, ps.RelativePos.Z);
      if (ps.RelativeRot != null)
        part.orgRot = new Quaternion(ps.RelativeRot.X, ps.RelativeRot.Y, ps.RelativeRot.Z, ps.RelativeRot.W);

      part.partTransform = part.transform;
      part.packed = true;
    }

    // Set initial world transforms from root (root at origin)
    PositionPartsFromRoot(parts);

    return parts;
  }

  /// <summary>
  /// Set part world transforms from orgPos/orgRot relative to root (parts[0]).
  /// </summary>
  public static void PositionPartsFromRoot(List<Part> parts) {
    var root = parts[0];
    foreach (var part in parts) {
      part.transform.position = root.transform.TransformPoint(part.orgPos);
      part.transform.rotation = root.transform.rotation * part.orgRot;
    }
  }

  static void SetupAttachment(Part part, Proto.PartStructure ps) {
    var attach = ps.Attachment;
    var parent = part.parent;

    if (attach.SrfAttachPos != null) {
      part.attachMode = AttachModes.SRF_ATTACH;
      if (part.srfAttachNode != null) {
        part.srfAttachNode.position = new Vector3(attach.SrfAttachPos.X, attach.SrfAttachPos.Y, attach.SrfAttachPos.Z);
        if (attach.SrfAttachNormal != null)
          part.srfAttachNode.orientation = new Vector3(attach.SrfAttachNormal.X, attach.SrfAttachNormal.Y, attach.SrfAttachNormal.Z);
        part.srfAttachNode.attachedPartId = parent.craftID;
      }
    } else {
      part.attachMode = AttachModes.STACK;
      if (attach.ParentNodeIndex >= 0 && attach.ParentNodeIndex < parent.attachNodes.Count)
        parent.attachNodes[attach.ParentNodeIndex].attachedPartId = part.craftID;
      if (attach.ChildNodeIndex >= 0 && attach.ChildNodeIndex < part.attachNodes.Count)
        part.attachNodes[attach.ChildNodeIndex].attachedPartId = parent.craftID;
    }
  }
}
