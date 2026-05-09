using System;
using UnityEngine;
using Nova.Ffi;

namespace Nova.Components;

/// <summary>
/// Per-vessel state holder. The Rust simulator owns the canonical
/// vessel; this module just attaches to the corresponding handle.
///
/// The Rust vessel is created BEFORE the KSP <see cref="Vessel"/>
/// exists — see <see cref="Persistence.NovaCraftLoader.Load"/> (VAB
/// launch) and <see cref="Persistence.NovaSaveLoader"/> (save load).
/// Both routes register with <see cref="NovaWorldAddon.RegisterVessel"/>
/// using a known persistent id. KSP then creates a <see cref="Vessel"/>
/// with the same id (per stock <c>ShipConstruction.cs:470</c>:
/// <c>vessel.persistentId = ship.persistentId;</c>), and we look the
/// handle up here.
///
/// If <see cref="OnLoadVessel"/> can't find a handle, that's a bug in
/// the spawn path — every vessel reaching us must already be alive in
/// the Rust simulator.
/// </summary>
public class NovaVesselModule : VesselModule {

  public NovaVesselHandle Handle { get; private set; }

  public override void OnLoadVessel() {
    base.OnLoadVessel();
    var addon = NovaWorldAddon.Instance;
    if (addon == null) {
      NovaLog.LogError($"NovaVesselModule.OnLoadVessel: NovaWorldAddon not available (vessel {vessel?.vesselName})");
      return;
    }
    Handle = addon.LookupHandle(vessel.persistentId);
    if (Handle == null) {
      NovaLog.LogError(
        $"NovaVesselModule.OnLoadVessel: no Rust handle for vessel " +
        $"{vessel?.vesselName} (pid={vessel?.persistentId}). " +
        $"All vessels must be registered with the simulator before " +
        $"KSP creates them — check the spawn path.");
    }
  }

  private void OnDestroy() {
    if (vessel != null) {
      NovaWorldAddon.Instance?.UnregisterVessel(vessel.persistentId);
    }
    Handle = null; // The addon disposed it via UnregisterVessel.
  }

  // The .nvc has no orbit (the launchpad is KSP's call), so VAB-
  // launched vessels arrive in Rust as `Situation::Abstract`. Once
  // KSP wires up `vessel.orbitDriver`, we mirror the orbit through to
  // upgrade the Rust-side situation to `Orbit`. One-shot per vessel.
  // (Save loads carry the orbit in the proto so the Rust vessel is
  // already in `Situation::Orbit` when this fires — the local
  // orbitDriver matches and the push is a no-op repeat.)
  private bool _orbitPushed;
  private void MaybePushOrbit() {
    if (_orbitPushed) return;
    if (Handle == null) return;
    var o = vessel?.orbitDriver?.orbit;
    if (o == null) return;
    NovaWorldAddon.Instance?.SetVesselOrbit(
        vessel.persistentId,
        o.semiMajorAxis,
        o.eccentricity,
        o.inclination * Math.PI / 180.0,
        o.LAN * Math.PI / 180.0,
        o.argumentOfPeriapsis * Math.PI / 180.0,
        o.meanAnomalyAtEpoch,
        o.epoch,
        (uint)(vessel.mainBody?.flightGlobalsIndex ?? 0));
    _orbitPushed = true;
  }

  public void FixedUpdate() {
    try {
      var addon = NovaWorldAddon.Instance;
      if (addon == null) return;
      MaybePushOrbit();
      addon.Tick(Planetarium.GetUniversalTime());
      MaybeLogState();
    } catch (Exception e) {
      NovaLog.LogError($"NovaVesselModule.FixedUpdate: {e.Message}\n{e.StackTrace}");
    }
  }

  // Periodic FFI-state dump for smoke-testing the Rust simulator from
  // the KSP log. One line per active vessel every ~2 wall-clock seconds.
  // Wall-clock gated so timewarp doesn't flood the log.
  private float _lastLogWallTime;
  private void MaybeLogState() {
    if (Handle == null) return;
    if (vessel == null || !vessel.loaded) return;
    if (vessel != FlightGlobals.ActiveVessel) return;
    var now = Time.unscaledTime;
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
