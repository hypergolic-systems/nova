using System;
using UnityEngine;

namespace Nova.Telemetry;

/// <summary>
/// Registers each <c>nova/*</c> topic family with
/// <see cref="NovaTopicProxy"/> at startup. The only C# code that
/// knows about specific Nova topic name shapes — wire-name shape
/// (prefix + key type) lives somewhere by necessity, and this is
/// it.
///
/// Adding a new topic family is one new <c>RegisterFamily</c> call
/// here plus a matching prefix branch in
/// <c>nova-telemetry::topics::serialize</c> and a concrete
/// <see cref="NovaProxiedTopicBase"/> subclass for the key type
/// (Unity rejects generic MonoBehaviours, so each TKey gets its
/// own non-generic class).
///
/// The <see cref="KSPAddonAttribute"/> on
/// <see cref="NovaTopicFamiliesAddon"/> ensures registration runs
/// at <c>Startup.Instantly</c>, before
/// <see cref="NovaTelemetryInstaller"/>'s MainMenu attach wakes
/// the proxy that consumes the registry.
/// </summary>
public static class NovaTopicFamilies {
  /// <c>nova/part/{persistentId}</c> — per-part snapshot, key uint.
  public const string PartPrefix = "nova/part/";

  /// <c>nova/vessel-structure/{guid}</c> — per-vessel part graph, key Guid.
  public const string VesselStructurePrefix = "nova/vessel-structure/";

  internal static void RegisterAll() {
    NovaTopicProxy.RegisterFamily(PartPrefix, AttachPart);
    NovaTopicProxy.RegisterFamily(VesselStructurePrefix, AttachVesselStructure);
    NovaLog.Log("[Nova/Telemetry] families registered: nova/part/, nova/vessel-structure/");
  }

  private static NovaProxiedTopicBase AttachPart(GameObject host, string topicName) {
    var rest = topicName.Substring(PartPrefix.Length);
    if (!uint.TryParse(rest, out uint id)) {
      Debug.LogWarning("[Nova/Telemetry] bad part id in topic: " + topicName);
      return null;
    }
    NovaPartProxiedTopic.PendingPrefix = PartPrefix;
    NovaPartProxiedTopic.PendingKey = id;
    var t = host.AddComponent<NovaPartProxiedTopic>();
    NovaPartProxiedTopic.PendingPrefix = null;
    NovaPartProxiedTopic.PendingKey = default;
    return t;
  }

  private static NovaProxiedTopicBase AttachVesselStructure(GameObject host, string topicName) {
    var rest = topicName.Substring(VesselStructurePrefix.Length);
    if (!Guid.TryParse(rest, out Guid g)) {
      Debug.LogWarning("[Nova/Telemetry] bad vessel guid in topic: " + topicName);
      return null;
    }
    NovaVesselStructureProxiedTopic.PendingPrefix = VesselStructurePrefix;
    NovaVesselStructureProxiedTopic.PendingKey = g;
    var t = host.AddComponent<NovaVesselStructureProxiedTopic>();
    NovaVesselStructureProxiedTopic.PendingPrefix = null;
    NovaVesselStructureProxiedTopic.PendingKey = default;
    return t;
  }
}

/// <summary>
/// Drives <see cref="NovaTopicFamilies.RegisterAll"/> exactly once
/// at <c>Startup.Instantly</c> (before
/// <see cref="NovaTelemetryInstaller"/>'s MainMenu attach, which
/// wakes the proxy that uses the registry).
/// </summary>
[KSPAddon(KSPAddon.Startup.Instantly, true)]
public sealed class NovaTopicFamiliesAddon : MonoBehaviour {
  private void Awake() {
    NovaTopicFamilies.RegisterAll();
  }
}
