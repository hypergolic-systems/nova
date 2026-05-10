using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEngine;
using Proto = Nova.Core.Persistence.Protos;

namespace Nova.Persistence;

/// <summary>
/// Build proto <see cref="Proto.VesselStructure"/> +
/// <see cref="Proto.VesselState"/> blobs from live KSP <see cref="Part"/>
/// objects. The previous version walked each part's
/// <c>NovaPartModule.Components</c> list and called
/// <c>cmp.SaveStructure(ps)</c> / <c>cmp.Save(state)</c>; that
/// machinery is gone with the C# simulator.
///
/// The proto-only rewrite reads each part's prefab MODULE config
/// directly. Phase-1 covers Battery — the only ported component
/// with editor-configurable structure (capacity). Command is
/// prefab-only and lives in <c>NovaPart</c> in the part database;
/// nothing per-instance to record here.
/// </summary>
public static class NovaVesselBuilder {

  public delegate uint PartIdSelector(Part part);

  public static (Proto.VesselStructure structure, Proto.VesselState state) BuildFromParts(
      IList<Part> parts, PartIdSelector idSelector = null) {
    idSelector ??= p => p.persistentId;
    var partIndex = new Dictionary<Part, int>();
    for (int i = 0; i < parts.Count; i++)
      partIndex[parts[i]] = i;

    var protoStructure = new List<Proto.PartStructure>();
    var protoState = new List<Proto.PartState>();

    var stageGroups = new SortedDictionary<int, List<uint>>();

    for (int i = 0; i < parts.Count; i++) {
      var part = parts[i];
      var parentIdx = part.parent != null && partIndex.ContainsKey(part.parent)
        ? partIndex[part.parent]
        : -1;

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

      if (part.parent != null) {
        ps.Attachment = BuildAttachment(part, partIndex);
      }

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

      if (part.inverseStage >= 0) {
        if (!stageGroups.ContainsKey(part.inverseStage))
          stageGroups[part.inverseStage] = new List<uint>();
        stageGroups[part.inverseStage].Add(id);
      }

      // Per-component structure/state. Driven by direct prefab-MODULE
      // reads; new ported components add their cases here.
      PopulateBattery(part, ps, partState);

      protoStructure.Add(ps);
      protoState.Add(partState);
    }

    var structure = new Proto.VesselStructure();
    structure.Parts.AddRange(protoStructure);

    var state = new Proto.VesselState();
    state.Parts.AddRange(protoState);

    foreach (var kvp in stageGroups.Reverse()) {
      state.Stages.Add(new Proto.Stage { PartIds = kvp.Value.ToArray() });
    }

    return (structure, state);
  }

  /// <summary>
  /// Read the prefab's <c>NovaBatteryModule</c> MODULE config for
  /// capacity + initial value, write to PartStructure.Battery and
  /// PartState.Battery. Live runtime contents (mid-flight save) will
  /// flow through the FFI mirror once the Rust-side save path lands.
  /// </summary>
  private static void PopulateBattery(Part part, Proto.PartStructure ps, Proto.PartState state) {
    var moduleNode = FindNovaModuleConfig(part, "NovaBatteryModule");
    if (moduleNode == null) return;
    var capStr = moduleNode.GetValue("capacity");
    if (capStr == null) return;
    var capacity = double.Parse(capStr);
    var valStr = moduleNode.GetValue("value") ?? capStr;
    var contents = double.Parse(valStr);
    ps.Battery = new Proto.BatteryStructure { Capacity = capacity };
    state.Battery = new Proto.BatteryState { Value = contents };
  }

  /// <summary>
  /// Find the <c>MODULE { name = ... }</c> config block on the
  /// part's prefab. Returns null when the part has no such module.
  /// </summary>
  private static ConfigNode FindNovaModuleConfig(Part part, string moduleName) {
    var prefab = part.partInfo?.partConfig;
    if (prefab == null) return null;
    foreach (var node in prefab.GetNodes("MODULE")) {
      if (node.GetValue("name") == moduleName) return node;
    }
    return null;
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
      attachment.ParentNodeIndex = -1;
      attachment.ChildNodeIndex = -1;
      if (part.srfAttachNode != null) {
        var pos = part.srfAttachNode.position;
        var ori = part.srfAttachNode.orientation;
        attachment.SrfAttachPos = new Proto.Vec3 { X = pos.x, Y = pos.y, Z = pos.z };
        attachment.SrfAttachNormal = new Proto.Vec3 { X = ori.x, Y = ori.y, Z = ori.z };
      }
    } else {
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

  /// <summary>
  /// Build the prefab-side <see cref="Proto.PartDatabase"/> from
  /// <c>PartLoader.LoadedPartsList</c>. One <c>NovaPart</c> per
  /// prefab that carries any Nova module. Sent to Rust at startup
  /// via <see cref="Nova.Ffi.NovaWorldAddon.SetPartDatabase"/>.
  /// </summary>
  public static Proto.PartDatabase BuildPartDatabase() {
    var db = new Proto.PartDatabase();
    var available = PartLoader.LoadedPartsList;
    if (available == null) return db;

    foreach (var info in available) {
      if (info?.partConfig == null) continue;
      var hasNova = false;
      var entry = new Proto.NovaPart {
        Name = info.name,
        DryMassKg = (info.partPrefab?.mass ?? 0) * 1000.0,
        DisplayTitle = info.title ?? info.name,
      };

      foreach (var moduleNode in info.partConfig.GetNodes("MODULE")) {
        var moduleName = moduleNode.GetValue("name");
        switch (moduleName) {
          case "NovaCommandModule": {
            var idle = moduleNode.GetValue("idleDraw");
            if (idle == null) break;
            entry.Command = new Proto.CommandPrefab { IdleDraw = double.Parse(idle) };
            hasNova = true;
            break;
          }
          case "NovaBatteryModule": {
            var rate = moduleNode.GetValue("maxRate");
            entry.Battery = new Proto.BatteryPrefab {
              MaxRate = rate != null ? double.Parse(rate) : 10.0,
            };
            hasNova = true;
            break;
          }
        }
      }

      if (hasNova) db.Parts.Add(entry);
    }

    return db;
  }
}
