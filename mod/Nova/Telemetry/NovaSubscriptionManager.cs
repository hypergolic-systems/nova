using System;
using Dragonglass.Telemetry;
using UnityEngine;

namespace Nova.Telemetry;

// Translates SubscriptionBus events for `NovaVesselStructure/<id>`
// and `NovaPart/<id>` topics into AddComponent / Destroy calls on
// the matching Vessel / Part GameObject. Mirrors Dragonglass's
// PartSubscriptionManager pattern: no internal bookkeeping, the
// topic component itself is the "is there a sampler for this id?"
// answer via GetComponent<T>(). Always-on; lives on the persistent
// telemetry host so it handles signals across scenes.
public sealed class NovaSubscriptionManager : MonoBehaviour {
  private const string LogPrefix = "[Nova/Telemetry] ";
  private const string VesselPrefix = "NovaVesselStructure/";
  private const string PartPrefix = "NovaPart/";

  private void OnEnable() {
    SubscriptionBus.SubscribeRequested += OnSubscribe;
    SubscriptionBus.UnsubscribeRequested += OnUnsubscribe;
  }

  private void OnDisable() {
    SubscriptionBus.SubscribeRequested -= OnSubscribe;
    SubscriptionBus.UnsubscribeRequested -= OnUnsubscribe;
  }

  private void OnSubscribe(string topicName) {
    if (topicName == null) return;
    if (topicName.StartsWith(VesselPrefix, StringComparison.Ordinal)) {
      if (TryResolveVessel(topicName, VesselPrefix, out var vessel)) {
        AttachIfMissing<NovaVesselStructureTopic>(vessel.gameObject);
      }
      return;
    }
    if (topicName.StartsWith(PartPrefix, StringComparison.Ordinal)) {
      if (TryResolvePart(topicName, PartPrefix, out var part)) {
        AttachIfMissing<NovaPartTopic>(part.gameObject);
      }
    }
  }

  private void OnUnsubscribe(string topicName) {
    if (topicName == null) return;
    if (topicName.StartsWith(VesselPrefix, StringComparison.Ordinal)) {
      if (TryResolveVessel(topicName, VesselPrefix, out var vessel)) {
        DetachIfPresent<NovaVesselStructureTopic>(vessel.gameObject);
      }
      return;
    }
    if (topicName.StartsWith(PartPrefix, StringComparison.Ordinal)) {
      if (TryResolvePart(topicName, PartPrefix, out var part)) {
        DetachIfPresent<NovaPartTopic>(part.gameObject);
      }
    }
  }

  private static void AttachIfMissing<T>(GameObject go) where T : Component {
    if (go.GetComponent<T>() == null) go.AddComponent<T>();
  }

  private static void DetachIfPresent<T>(GameObject go) where T : Component {
    var c = go.GetComponent<T>();
    if (c != null) Destroy(c);
  }

  private static bool TryResolveVessel(string topicName, string prefix, out Vessel vessel) {
    vessel = null;
    var guid = topicName.Substring(prefix.Length);
    if (string.IsNullOrEmpty(guid)) return false;
    if (FlightGlobals.Vessels == null) return false;
    for (int i = 0; i < FlightGlobals.Vessels.Count; i++) {
      var v = FlightGlobals.Vessels[i];
      if (v != null && v.id.ToString("D") == guid) {
        vessel = v;
        return true;
      }
    }
    return false;
  }

  private static bool TryResolvePart(string topicName, string prefix, out Part part) {
    part = null;
    if (!uint.TryParse(topicName.Substring(prefix.Length), out uint id)) return false;
    if (FlightGlobals.PersistentLoadedPartIds != null
        && FlightGlobals.PersistentLoadedPartIds.TryGetValue(id, out part)
        && part != null) {
      return true;
    }
    return false;
  }
}
