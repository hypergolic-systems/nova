using System;
using System.IO;
using UnityEngine;
using Nova.Ffi;
using Nova.Persistence;
using Proto = Nova.Core.Persistence.Protos;
// `using System.Collections.Generic;` is intentionally omitted —
// the only collection use lives inside `PartsMatch` and we qualify
// HashSet there to avoid widening the namespace.

namespace Nova.Components;

/// <summary>
/// Per-vessel state holder. Owns the <see cref="NovaVesselHandle"/>
/// for this vessel — built at load time by sending the vessel's
/// `Proto.VesselStructure` + `Proto.VesselState` bytes to the Rust
/// simulator via <see cref="NovaWorldAddon.RegisterVessel"/>.
///
/// Phase-1 stub: the proto-bytes wiring isn't connected yet. The
/// handle is null until the proto-build path lands; FFI-reading part
/// modules (NovaBatteryModule, NovaCommandModule) check
/// <c>Handle == null</c> and silently no-op.
/// </summary>
public class NovaVesselModule : VesselModule {

  /// <summary>
  /// Rust simulator handle for this vessel. Null until the
  /// proto-driven vessel-create path lands. After that:
  ///  - built when KSP's <c>ProtoVessel.Load</c> postfix fires for
  ///    a save-loaded vessel (we already have the proto bytes there);
  ///  - or built on first-launch from <c>NovaVesselBuilder.BuildFromParts</c>
  ///    (the proto types let us round-trip without reaching for a
  ///    different code path).
  /// Disposed on vessel destroy.
  /// </summary>
  public NovaVesselHandle Handle { get; private set; }

  protected override void OnAwake() {
    base.OnAwake();
  }

  private void OnDestroy() {
    if (vessel != null) {
      NovaWorldAddon.Instance?.UnregisterVessel(vessel.persistentId);
    }
    Handle?.Dispose();
    Handle = null;
  }

  /// <summary>
  /// First-launch path. We previously hooked <c>onVesselPartCountChanged</c>
  /// for this, but it fires from inside <c>Vessel.Initialize</c> before
  /// <c>orbitDriver</c> is wired up — and stock <c>vessel.orbit</c> is
  /// <c>orbitDriver.orbit</c> with no null guard. We need the orbit to
  /// build the FFI vessel (Rust requires it), so we defer registration
  /// to <see cref="FixedUpdate"/>: by the first physics frame the
  /// orbit driver is always ready.
  ///
  /// Canonical flow: a launch from the VAB went through
  /// <see cref="NovaCraftLoader.Load"/>, which stashed the .nvc's
  /// (remapped) <c>Proto.Vessel</c> in
  /// <see cref="NovaCraftLoader.PendingVessel"/>. We pick it up here
  /// and feed it straight to <see cref="RegisterFromProto"/> so KSP-
  /// side parts and the nova-sim vessel are forks of the same
  /// canonical bytes.
  ///
  /// Fallback (no pending proto): synth a proto from live parts via
  /// <see cref="BuildLocalProto"/>. Used for stock-spawned vessels
  /// (asteroids, comets) and for safety on edge-case paths that
  /// bypass NovaCraftLoader.
  /// </summary>
  private void TryRegister() {
    if (Handle != null) return;
    if (vessel == null || !vessel.loaded) return;
    if (vessel.parts == null || vessel.parts.Count == 0) return;
    if (vessel.orbitDriver?.orbit == null) return;

    var pending = NovaCraftLoader.PendingVessel;
    if (pending != null && PartsMatch(pending, vessel)) {
      NovaCraftLoader.PendingVessel = null;
      pending.Structure.PersistentId = vessel.persistentId;
      pending.Structure.VesselId = vessel.id.ToString();
      if (pending.State == null) pending.State = new Proto.VesselState();
      if (string.IsNullOrEmpty(pending.State.Name))
        pending.State.Name = vessel.vesselName ?? "";
      pending.State.Situation = (int)vessel.situation;
      var o = vessel.orbitDriver.orbit;
      pending.Structure.Orbit = new Proto.OrbitalState {
        Inclination = o.inclination * Math.PI / 180.0,
        Eccentricity = o.eccentricity,
        SemiMajorAxis = o.semiMajorAxis,
        Lan = o.LAN * Math.PI / 180.0,
        ArgumentOfPeriapsis = o.argumentOfPeriapsis * Math.PI / 180.0,
        MeanAnomalyAtEpoch = o.meanAnomalyAtEpoch,
        Epoch = o.epoch,
        BodyIndex = vessel.mainBody?.flightGlobalsIndex ?? 0,
      };
      RegisterFromProto(pending);
      return;
    }

    BuildAndRegister();
  }

  static bool PartsMatch(Proto.Vessel proto, Vessel vessel) {
    if (proto.Structure?.Parts == null) return false;
    if (proto.Structure.Parts.Count != vessel.parts.Count) return false;
    var protoIds = new System.Collections.Generic.HashSet<uint>();
    foreach (var p in proto.Structure.Parts) protoIds.Add(p.Id);
    foreach (var p in vessel.parts) {
      if (!protoIds.Contains(p.persistentId)) return false;
    }
    return true;
  }

  /// <summary>
  /// Revert / save-load path. Called by
  /// <c>ProtoVesselPatches.Load_Postfix</c> after KSP rehydrates the
  /// vessel from its persisted ConfigNode. By the time we get here
  /// <c>vessel.parts</c> is populated, so we can use the same live-
  /// parts builder as <see cref="OnPartCountChanged"/>.
  /// </summary>
  public void OnVesselFullLoad(ConfigNode node) {
    if (vessel == null || vessel.parts == null || vessel.parts.Count == 0) return;
    Handle?.Dispose();
    Handle = null;
    BuildAndRegister();
  }

  private void BuildAndRegister() {
    try {
      var (structure, state) = BuildLocalProto();
      RegisterFromProto(new Proto.Vessel { Structure = structure, State = state });
    } catch (Exception e) {
      NovaLog.LogError($"BuildAndRegister: {e.Message}\n{e.StackTrace}");
    }
  }

  /// <summary>
  /// Build a <c>Proto.Vessel</c> from the live KSP <see cref="Vessel"/>.
  /// Used as a fallback when no canonical save proto is available
  /// (first-launch from VAB). For save loads, the <c>Proto.Vessel</c>
  /// comes from the .nvs and is passed directly to
  /// <see cref="RegisterFromProto"/>.
  /// </summary>
  public (Proto.VesselStructure structure, Proto.VesselState state) BuildLocalProto() {
    var (structure, state) = NovaVesselBuilder.BuildFromParts(
        vessel.parts, p => p.persistentId);
    structure.PersistentId = vessel.persistentId;
    structure.VesselId = vessel.id.ToString();
    state.Name = vessel.vesselName ?? "";
    state.Situation = (int)vessel.situation;
    var o = vessel.orbitDriver?.orbit;
    if (o != null) {
      structure.Orbit = new Proto.OrbitalState {
        Inclination = o.inclination * Math.PI / 180.0,
        Eccentricity = o.eccentricity,
        SemiMajorAxis = o.semiMajorAxis,
        Lan = o.LAN * Math.PI / 180.0,
        ArgumentOfPeriapsis = o.argumentOfPeriapsis * Math.PI / 180.0,
        MeanAnomalyAtEpoch = o.meanAnomalyAtEpoch,
        Epoch = o.epoch,
        BodyIndex = vessel.mainBody?.flightGlobalsIndex ?? 0,
      };
    }
    return (structure, state);
  }

  /// <summary>
  /// Register (or re-register) this vessel with the Rust simulator
  /// from a canonical <c>Proto.Vessel</c>. This is the path
  /// <see cref="NovaSaveLoader"/> uses: the saved proto crosses the
  /// FFI verbatim, so KSP-side parts and the nova-sim vessel are
  /// forks of the same bytes.
  /// </summary>
  public void RegisterFromProto(Proto.Vessel saved) {
    var addon = NovaWorldAddon.Instance;
    if (addon == null) return;
    if (saved?.Structure == null || saved.State == null) return;
    try {
      var structureBytes = Serialize(saved.Structure);
      var stateBytes = Serialize(saved.State);
      double ut;
      try { ut = Planetarium.GetUniversalTime(); } catch { ut = 0; }

      Handle?.Dispose();
      Handle = addon.RegisterVessel(saved.Structure.PersistentId,
          structureBytes, stateBytes, ut);
      if (Handle != null) {
        NovaLog.Log($"Registered vessel {saved.State.Name} ({saved.Structure.PersistentId}) with Rust simulator");
      }
    } catch (Exception e) {
      NovaLog.LogError($"RegisterFromProto: {e.Message}\n{e.StackTrace}");
    }
  }

  /// <summary>
  /// Compute the structure hash for the current live vessel. Used
  /// by <see cref="NovaSaveLoader"/>'s quickload diff to detect
  /// structural changes since the save.
  /// </summary>
  public byte[] ComputeCurrentStructureHash() {
    if (vessel == null || vessel.parts == null || vessel.parts.Count == 0) return null;
    var (structure, _) = BuildLocalProto();
    return NovaVesselBuilder.ComputeStructureHash(structure);
  }

  private static byte[] Serialize<T>(T proto) {
    using var ms = new MemoryStream();
    ProtoBuf.Serializer.Serialize(ms, proto);
    return ms.ToArray();
  }

  public void FixedUpdate() {
    try {
      var addon = NovaWorldAddon.Instance;
      if (addon == null) return;
      if (Handle == null) TryRegister();
      addon.Tick(Planetarium.GetUniversalTime());
      MaybeLogState();
    } catch (Exception e) {
      NovaLog.LogError($"NovaVesselModule.FixedUpdate: {e.Message}\n{e.StackTrace}");
    }
  }

  // Periodic FFI-state dump for smoke-testing the Rust simulator from
  // the KSP log. One line per loaded vessel every ~2 wall-clock seconds,
  // listing battery contents and command activity per relevant part.
  // Wall-clock gated so timewarp doesn't flood the log.
  private float _lastLogWallTime;
  private void MaybeLogState() {
    if (Handle == null) return;
    if (vessel == null || !vessel.loaded) return;
    if (vessel != FlightGlobals.ActiveVessel) return;
    var now = UnityEngine.Time.unscaledTime;
    if (now - _lastLogWallTime < 2.0f) return;
    _lastLogWallTime = now;

    var sb = new System.Text.StringBuilder();
    sb.Append($"[FfiState] {vessel.vesselName}");
    foreach (var p in vessel.parts) {
      var bm = p.FindModuleImplementing<NovaBatteryModule>();
      if (bm != null) {
        var s = bm.GetState();
        sb.Append($" | bat[{p.persistentId}] {s.Contents:F1}/{s.Capacity:F1}");
      }
      var cm = p.FindModuleImplementing<NovaCommandModule>();
      if (cm != null) {
        var s = cm.GetState();
        sb.Append($" | cmd[{p.persistentId}] {s.IdleActivity * 100.0:F0}%");
      }
    }
    NovaLog.Log(sb.ToString());
  }
}
