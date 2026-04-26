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
        AttachOrEnable<NovaVesselStructureTopic>(vessel.gameObject);
      }
      return;
    }
    if (topicName.StartsWith(PartPrefix, StringComparison.Ordinal)) {
      if (TryResolvePart(topicName, PartPrefix, out var part)) {
        AttachOrEnable<NovaPartTopic>(part.gameObject);
      }
    }
  }

  private void OnUnsubscribe(string topicName) {
    if (topicName == null) return;
    if (topicName.StartsWith(VesselPrefix, StringComparison.Ordinal)) {
      if (TryResolveVessel(topicName, VesselPrefix, out var vessel)) {
        DisableIfPresent<NovaVesselStructureTopic>(vessel.gameObject);
      }
      return;
    }
    if (topicName.StartsWith(PartPrefix, StringComparison.Ordinal)) {
      if (TryResolvePart(topicName, PartPrefix, out var part)) {
        DisableIfPresent<NovaPartTopic>(part.gameObject);
      }
    }
  }

  // Toggle the topic Behaviour's enabled flag rather than Destroy/AddComponent.
  // Unity defers Destroy to end-of-frame, which races with a synchronous
  // re-subscribe (new component's OnEnable runs first, then the old's deferred
  // OnDisable wipes the new entry from `_byPart` / `_byVessel`). enabled flips
  // run OnEnable/OnDisable synchronously — no race, and the topic Component
  // sticks around for the part's lifetime.
  private static void AttachOrEnable<T>(GameObject go) where T : Behaviour {
    var existing = go.GetComponent<T>();
    if (existing == null) go.AddComponent<T>();
    else if (!existing.enabled) existing.enabled = true;
  }

  private static void DisableIfPresent<T>(GameObject go) where T : Behaviour {
    var c = go.GetComponent<T>();
    if (c != null) c.enabled = false;
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
