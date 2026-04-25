using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Nova.Components;
using UnityEngine;
using Proto = Nova.Core.Persistence.Protos;

namespace Nova.Persistence;

/// <summary>
/// Builds proto Vessel structure and state from live Part objects.
/// Shared by craft file saves and game saves.
/// </summary>
public static class NovaVesselBuilder {

  public delegate uint PartIdSelector(Part part);

  /// <summary>
  /// Build proto structure and state from live parts.
  /// idSelector chooses which ID to store: craftID for craft files,
  /// persistentId for save files.
  /// </summary>
  public static (Proto.VesselStructure structure, Proto.VesselState state) BuildFromParts(
      IList<Part> parts, PartIdSelector idSelector = null) {
    idSelector ??= p => p.craftID; // default: craft file behavior
    var partIndex = new Dictionary<Part, int>();
    for (int i = 0; i < parts.Count; i++)
      partIndex[parts[i]] = i;

    var protoStructure = new List<Proto.PartStructure>();
    var protoState = new List<Proto.PartState>();

    // Track staging: group parts by inverseStage
    var stageGroups = new SortedDictionary<int, List<uint>>();

    for (int i = 0; i < parts.Count; i++) {
      var part = parts[i];
      var parentIdx = part.parent != null && partIndex.ContainsKey(part.parent)
        ? partIndex[part.parent]
        : -1;

      // Save root-relative position/rotation. Compute from transforms since
      // orgPos/orgRot may be stale or zero for editor-built parts.
      var root = parts[0];
      var relPos = root.transform.InverseTransformPoint(part.transform.position);
      var relRot = Quaternion.Inverse(root.transform.rotation) * part.transform.rotation;
      var id = idSelector(part);
      var ps = new Proto.PartStructure {
        Id = id,
        PartName = part.partInfo.name,
        ParentIndex = parentIdx,
        RelativePos = new Proto.Vec3 { X = relPos.x, Y = relPos.y, Z = relPos.z },
        RelativeRot = new Proto.Quat { X = relRot.x, Y = relRot.y, Z = relRot.z, W = relRot.w },
      };
      var partState = new Proto.PartState {
        Id = id,
        Activated = part.State == PartStates.ACTIVE,
      };

      // Attachment (null for root)
      if (part.parent != null) {
        ps.Attachment = BuildAttachment(part, partIndex);
      }

      // Symmetry
      if (part.symmetryCounterparts != null && part.symmetryCounterparts.Count > 0) {
        var sym = new Proto.Symmetry {
          Partners = part.symmetryCounterparts.Select(p => idSelector(p)).ToArray(),
        };
        var mir = part.mirrorVector;
        if (mir.x < 0 || mir.y < 0 || mir.z < 0) {
          sym.Mirror = new Proto.Vec3 { X = mir.x, Y = mir.y, Z = mir.z };
        }
        ps.Symmetry = sym;
      }

      // Staging
      if (part.inverseStage >= 0) {
        if (!stageGroups.ContainsKey(part.inverseStage))
          stageGroups[part.inverseStage] = new List<uint>();
        stageGroups[part.inverseStage].Add(id);
      }

      // Nova components
      foreach (var module in part.Modules.OfType<NovaPartModule>()) {
        if (module.Components == null) continue;
        foreach (var cmp in module.Components) {
          cmp.SaveStructure(ps);
          cmp.Save(partState);
        }
      }

      protoStructure.Add(ps);
      protoState.Add(partState);
    }

    var structure = new Proto.VesselStructure();
    structure.Parts.AddRange(protoStructure);

    var state = new Proto.VesselState();
    state.Parts.AddRange(protoState);

    // Build stage list in firing order (highest inverseStage first)
    foreach (var kvp in stageGroups.Reverse()) {
      state.Stages.Add(new Proto.Stage { PartIds = kvp.Value.ToArray() });
    }

    return (structure, state);
  }

  public static byte[] ComputeStructureHash(Proto.VesselStructure structure) {
    using var ms = new MemoryStream();
    ProtoBuf.Serializer.Serialize(ms, structure);
    using var sha = SHA256.Create();
    return sha.ComputeHash(ms.ToArray());
  }

  static Proto.Attachment BuildAttachment(Part part, Dictionary<Part, int> partIndex) {
    var attachment = new Proto.Attachment();

    if (part.attachMode == AttachModes.SRF_ATTACH) {
      // Surface attached
      attachment.ParentNodeIndex = -1;
      attachment.ChildNodeIndex = -1;
      if (part.srfAttachNode != null) {
        var pos = part.srfAttachNode.position;
        var ori = part.srfAttachNode.orientation;
        attachment.SrfAttachPos = new Proto.Vec3 { X = pos.x, Y = pos.y, Z = pos.z };
        attachment.SrfAttachNormal = new Proto.Vec3 { X = ori.x, Y = ori.y, Z = ori.z };
      }
    } else {
      // Stack attached — find node indices
      var parentNode = part.parent.FindAttachNodeByPart(part);
      var childNode = part.FindAttachNodeByPart(part.parent);

      attachment.ParentNodeIndex = parentNode != null
        ? part.parent.attachNodes.IndexOf(parentNode)
        : -1;
      attachment.ChildNodeIndex = childNode != null
        ? part.attachNodes.IndexOf(childNode)
        : -1;
    }

    return attachment;
  }
}
