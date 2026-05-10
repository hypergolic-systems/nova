using System;
using System.Collections.Generic;
using System.Text;
using Dragonglass.Telemetry;
using Dragonglass.Telemetry.Topics;
using Dragonglass.Telemetry.Util;
using Nova.Ffi;
using UnityEngine;

namespace Nova.Telemetry;

/// <summary>
/// Topic-agnostic bridge that exposes every <c>nova/*</c> topic the
/// Rust simulator knows about through Dragonglass's
/// <see cref="TopicBroadcaster"/>. There is intentionally no
/// per-topic-family C# code that touches the bytes — the wire name
/// flows untouched from the UI's <c>subscribe</c> envelope through
/// <see cref="SubscriptionBus"/> into <c>nova_topic_subscribe</c>;
/// Rust hands back a stable pointer to a per-topic buffer; C#
/// reads the version word in <see cref="NovaProxiedTopicBase.Update"/>
/// (no FFI, no allocs) and splices the payload bytes verbatim into
/// the broadcaster's StringBuilder via <see cref="Json.WriteRaw"/>.
///
/// Adding a new topic family is a Rust-only change for the wire
/// shape; the C# side just adds one entry in
/// <see cref="NovaTopicFamilies"/> mapping <c>prefix</c> →
/// <c>parse-key</c> → concrete <see cref="NovaProxiedTopicBase"/>
/// subclass. Different topic families get distinct concrete
/// subclasses (each holding its own <c>PendingKey</c> static slot
/// of the right key type) so multiple Nova topics on the same
/// GameObject are still distinguishable via
/// <see cref="GameObject.GetComponent{T}"/>. We can't use generic
/// <c>NovaProxiedTopic&lt;TKey&gt;</c> for this — Unity rejects
/// generic MonoBehaviours at AddComponent time.
///
/// Lifecycle: attached to Dragonglass's host GameObject by
/// <see cref="NovaTelemetryInstaller"/>.
/// </summary>
public sealed class NovaTopicProxy : MonoBehaviour {
  /// <summary>Attaches a <see cref="NovaProxiedTopicBase"/> for the
  /// given wire-name to <paramref name="host"/>, or returns null if
  /// the name's key portion fails to parse.</summary>
  public delegate NovaProxiedTopicBase FamilyAttacher(GameObject host, string topicName);

  // Prefix → attacher. NovaTopicFamilies populates this once at
  // startup; the proxy iterates on each subscribe.
  private static readonly Dictionary<string, FamilyAttacher> Families = new();

  /// <summary>Register a Nova topic family. Idempotent on the same
  /// prefix; later registrations replace earlier.</summary>
  public static void RegisterFamily(string prefix, FamilyAttacher attacher) {
    Families[prefix] = attacher;
  }

  // One MonoBehaviour per active wire subscription. Keyed by topic
  // name so SubscriptionBus add/remove maps to AddComponent / Destroy.
  private readonly Dictionary<string, NovaProxiedTopicBase> _topics = new();

  private void OnEnable() {
    SubscriptionBus.SubscribeRequested += OnSubscribe;
    SubscriptionBus.UnsubscribeRequested += OnUnsubscribe;
  }

  private void OnDisable() {
    SubscriptionBus.SubscribeRequested -= OnSubscribe;
    SubscriptionBus.UnsubscribeRequested -= OnUnsubscribe;
    foreach (var t in _topics.Values) if (t != null) Destroy(t);
    _topics.Clear();
  }

  private void OnSubscribe(string topicName) {
    if (topicName == null
        || !topicName.StartsWith("nova/", StringComparison.Ordinal)) {
      return;
    }
    if (_topics.ContainsKey(topicName)) return;

    foreach (var kv in Families) {
      if (topicName.StartsWith(kv.Key, StringComparison.Ordinal)) {
        var t = kv.Value(gameObject, topicName);
        if (t != null) {
          _topics[topicName] = t;
          NovaLog.Log("[Nova/Telemetry] subscribed: " + topicName);
        }
        return;
      }
    }
    Debug.LogWarning("[Nova/Telemetry] no family registered for topic: " + topicName);
  }

  private void OnUnsubscribe(string topicName) {
    if (topicName == null) return;
    if (_topics.TryGetValue(topicName, out var t)) {
      _topics.Remove(topicName);
      if (t != null) Destroy(t);
    }
  }
}

/// <summary>
/// Base class for Nova topic proxies. Holds the wire name + Rust-
/// side buffer pointer + last-seen version, and implements the
/// shared lifecycle (subscribe / poll / write). Concrete subclasses
/// (one per key type) just carry strongly-typed static handoff slots
/// for the wire-key portion of the name.
///
/// Unity rejects generic MonoBehaviours, so we can't share via
/// <c>NovaProxiedTopic&lt;TKey&gt;</c>; the per-family subclasses
/// each provide a non-generic Unity-friendly type while the logic
/// itself stays here.
/// </summary>
public abstract unsafe class NovaProxiedTopicBase : Topic {
  // Buffer header layout — must match crates/nova-telemetry/src/registry.rs.
  protected const int HeaderVersionOffset = 0;
  protected const int HeaderLenOffset = 8;
  protected const int HeaderPayloadOffset = 16;

  protected string _name;
  protected byte* _buf;
  protected ulong _lastVersion;

  public override string Name => _name ?? string.Empty;

  /// <summary>Concrete subclasses set <see cref="_name"/> in their
  /// own <c>Awake</c> from their per-family static handoff slots,
  /// then chain to <c>base.Awake()</c>. (We don't define <c>Awake</c>
  /// here because Unity calls the most-derived one only if it
  /// exists; centralising it would require subclasses to virtual-
  /// override, which complicates the static-slot read.)</summary>
  protected abstract void Awake();

  protected override void OnEnable() {
    base.OnEnable();
    if (string.IsNullOrEmpty(_name)) return;
    var addon = NovaWorldAddon.Instance;
    _buf = addon != null ? addon.SubscribeTopic(_name) : null;
    _lastVersion = 0;
    MarkDirty();
  }

  protected override void OnDisable() {
    base.OnDisable();
    if (!string.IsNullOrEmpty(_name)) {
      NovaWorldAddon.Instance?.UnsubscribeTopic(_name);
    }
    _buf = null;
  }

  // Cheap update: two pointer reads; no FFI, no managed allocs.
  private void Update() {
    if (_buf == null) return;
    ulong v = *((ulong*)(_buf + HeaderVersionOffset));
    if (v == _lastVersion) return;
    _lastVersion = v;
    MarkDirty();
  }

  public override void WriteData(StringBuilder sb) {
    if (_buf == null) {
      sb.Append("null");
      return;
    }
    uint len = *((uint*)(_buf + HeaderLenOffset));
    if (len == 0) {
      sb.Append("null");
      return;
    }
    Json.WriteRaw(sb, _buf + HeaderPayloadOffset, (int)len);
  }
}

/// <summary>
/// Per-part proxy. Wire family <c>nova/part/{persistentId}</c>;
/// key is the part's KSP <see cref="Part.persistentId"/> (uint).
/// </summary>
public sealed unsafe class NovaPartProxiedTopic : NovaProxiedTopicBase {
  internal static string PendingPrefix;
  internal static uint PendingKey;

  protected override void Awake() {
    _name = PendingPrefix + PendingKey.ToString();
  }
}

/// <summary>
/// Per-vessel structure proxy. Wire family
/// <c>nova/vessel-structure/{guid}</c>; key is the vessel's KSP
/// <see cref="Vessel.id"/> (Guid). The "D" format matches what
/// Dragonglass's <c>flight.vesselId</c> emits, which is what the
/// UI's <c>useNovaVesselStructure</c> hands to the topic factory.
/// </summary>
public sealed unsafe class NovaVesselStructureProxiedTopic : NovaProxiedTopicBase {
  internal static string PendingPrefix;
  internal static Guid PendingKey;

  protected override void Awake() {
    _name = PendingPrefix + PendingKey.ToString("D");
  }
}

/// <summary>
/// Attaches <see cref="NovaTopicProxy"/> to Dragonglass's persistent
/// telemetry host GameObject (<c>"Dragonglass.Telemetry"</c>) at
/// MainMenu — by which time the host exists and survives via
/// <c>DontDestroyOnLoad</c>.
/// </summary>
[KSPAddon(KSPAddon.Startup.MainMenu, true)]
public sealed class NovaTelemetryInstaller : MonoBehaviour {
  private const string LogPrefix = "[Nova/Telemetry] ";
  private const string DgHostName = "Dragonglass.Telemetry";

  private void Start() {
    var host = GameObject.Find(DgHostName);
    if (host == null) {
      Debug.LogWarning(LogPrefix + "Dragonglass.Telemetry GameObject not found; "
          + "nova/* topics will not be served. Is Dragonglass installed?");
      return;
    }
    if (host.GetComponent<NovaTopicProxy>() == null) {
      host.AddComponent<NovaTopicProxy>();
      NovaLog.Log(LogPrefix + "registered NovaTopicProxy");
    }
  }
}
