using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Nova.Core.Communications;
using Nova.Core.Components.Communications;
using Nova.Core.Utils;

namespace Nova.Communications;

// Owns the singleton CommunicationsNetwork for the active flight scene.
// Registers KSC at boot, ticks Solve every FixedUpdate, and logs the
// effective bandwidth from each tracked vessel to KSC at 1 Hz.
//
// Vessel endpoints are pushed in by NovaVesselModule (which already
// owns vessel lifecycle), keyed by vessel.id. This addon stays focused
// on network state and reporting; it does not subscribe to vessel
// GameEvents itself.
[KSPAddon(KSPAddon.Startup.Flight, false)]
public class NovaCommunicationsAddon : MonoBehaviour {

  public static NovaCommunicationsAddon Instance { get; private set; }
  public CommunicationsNetwork Network { get; private set; }

  private Endpoint kscEndpoint;
  private readonly Dictionary<Guid, Endpoint> vesselEndpoints = new();
  private double lastLogUT = double.NegativeInfinity;
  private const double LogIntervalSeconds = 1.0;

  // Event-driven solve scheduling. Solve only fires when the
  // network's forecast horizon is reached (rate quantisation +
  // LinkHorizon bisection give per-link NextEventUT; MaxTickDt
  // folds them) or when topology changes set NeedsSolve. Initial
  // value forces a first solve on the first FixedUpdate.
  private double nextSolveUT = double.NegativeInfinity;

  private void Awake() {
    Instance = this;
    Network = new CommunicationsNetwork();
    NovaBodyRegistry.Reset();  // scene-fresh
    BuildKscEndpoint();
    Network.AddEndpoint(kscEndpoint);
    NovaLog.Log($"[Comms] addon online; KSC registered, network has {Network.Endpoints.Count} endpoint(s)");
  }

  // KSC position at any UT, in the same absolute frame
  // `vessel.orbit.getTruePositionAtUT` lives in. Snapshot KSC's
  // body-relative offset via KSP's authoritative
  // `GetWorldSurfacePosition` (which threads through BodyFrame +
  // directRotAngle correctly), then rotate that offset around the
  // body's spin axis (+Y, KSP convention) by `omega·dt` for future
  // UTs. Kerbin's centre at UT comes from `getTruePositionAtUT`.
  //
  // Bypasses GroundStation's lat/lon/omega formula because that
  // lives in Nova.Core (no KSP types) and so can't see BodyFrame.
  private void BuildKscEndpoint() {
    const double KSC_LAT = -0.0972;
    const double KSC_LON = -74.5577;
    const double KSC_ALT = 75;

    var homeBody = FlightGlobals.GetHomeBody();
    var captureUT = Planetarium.GetUniversalTime();
    var omega = 2 * Math.PI / homeBody.rotationPeriod;
    var kscWorldNow = homeBody.GetWorldSurfacePosition(KSC_LAT, KSC_LON, KSC_ALT);
    var kerbinCentreNow = homeBody.position;
    var kscOffsetWorldNow = kscWorldNow - kerbinCentreNow;

    Vec3d PositionAt(double ut) {
      var dt = ut - captureUT;
      var dTheta = omega * dt;
      var c = Math.Cos(dTheta);
      var s = Math.Sin(dTheta);
      // Right-handed rotation around +Y (Kerbin spins eastward; sign verified empirically).
      var rotated = new Vector3d(
        kscOffsetWorldNow.x * c - kscOffsetWorldNow.z * s,
        kscOffsetWorldNow.y,
        kscOffsetWorldNow.x * s + kscOffsetWorldNow.z * c);
      return (homeBody.getTruePositionAtUT(ut) + rotated).ToNova();
    }

    kscEndpoint = new Endpoint {
      Id = "KSC",
      PositionAt = PositionAt,
      // SurfaceMotion is captured day-one for the future analytical
      // surface↔Kepler solver; v1 dispatcher still routes KSC links
      // through numerical because no SurfaceMotion solver exists yet.
      Motion = new SurfaceMotion {
        Parent = NovaBodyRegistry.For(homeBody),
        LatitudeDeg = KSC_LAT,
        LongitudeDeg = KSC_LON,
        AltitudeM = KSC_ALT,
      },
    };
    kscEndpoint.Antennas.Add(new Antenna {
      TxPower = 1.0e9,
      Gain = 1.0e3,
      MaxRate = 1.0e9,
      RefDistance = 1.0e10,
    });
  }

  private void OnDestroy() {
    Instance = null;
  }

  public void AddVesselEndpoint(Vessel v, IList<Antenna> antennas) {
    if (v == null || antennas == null || antennas.Count == 0) return;
    if (vesselEndpoints.ContainsKey(v.id)) return;

    var motion = ExtractMotionFor(v);
    var ep = new Endpoint {
      Id = v.id.ToString("D"),
      PositionAt = ut => v.orbit.getTruePositionAtUT(ut).ToNova(),
      Motion = motion,
      // Off-rails vessels (no Motion) follow non-deterministic
      // trajectories; predicting future UTs from getTruePositionAtUT
      // gives wrong horizons. The reactive bucket-watch in FixedUpdate
      // handles transitions instead. Saves ~bisection cost per Solve
      // for any pair involving this endpoint.
      IsPredictable = motion != null,
    };
    ep.Antennas.AddRange(antennas);
    Network.AddEndpoint(ep);
    vesselEndpoints[v.id] = ep;
    var motionTag = ep.Motion is KeplerMotion ? "rails-Kepler" : "active/surface";
    NovaLog.Log($"[Comms] +endpoint {v.vesselName} ({antennas.Count} antenna(s), {motionTag})");
  }

  // Extract the analytical motion model for a vessel — but only when
  // it's safe to treat the orbit as Keplerian. Active-flight vessels
  // (under thrust, atmospheric drag, on physics) have non-Kepler
  // trajectories; landed/splashed vessels track the surface (they're
  // SurfaceMotion-shaped, but v1 has no surface↔Kepler solver). Both
  // fall back to the opaque PositionAt closure (numerical bisection).
  private static MotionModel ExtractMotionFor(Vessel v) {
    if (v.LandedOrSplashed) return null;          // surface-bound; use closure
    if (!v.packed) return null;                   // off-rails / under physics
    if (v.orbit == null || v.orbit.referenceBody == null) return null;
    return NovaBodyRegistry.ExtractKepler(v.orbit, NovaBodyRegistry.For(v.orbit.referenceBody));
  }

  // Refresh a vessel's endpoint — drop and re-add. Used when the
  // vessel transitions on/off rails so Motion is re-evaluated.
  public void RefreshVesselEndpoint(Vessel v) {
    if (v == null) return;
    if (!vesselEndpoints.TryGetValue(v.id, out var existing)) return;
    var antennas = existing.Antennas.ToList();
    Network.RemoveEndpoint(existing);
    vesselEndpoints.Remove(v.id);
    AddVesselEndpoint(v, antennas);
  }

  public void RemoveVesselEndpoint(Guid vesselId) {
    if (!vesselEndpoints.TryGetValue(vesselId, out var ep)) return;
    Network.RemoveEndpoint(ep);
    vesselEndpoints.Remove(vesselId);
    NovaLog.Log($"[Comms] -endpoint {vesselId:D}");
  }

  private void FixedUpdate() {
    if (Network == null) return;
    var ut = Planetarium.GetUniversalTime();

    // Reactive bucket-watch for off-rails endpoints. Predictive
    // bisection on `getTruePositionAtUT` for an under-thrust vessel
    // forecasts events on a hypothetical free-flight trajectory the
    // actual vessel diverges from — wrong horizons, stale cached
    // rates. Instead, recompute current bucket here and force a
    // re-solve if it stepped. For on-rails endpoints (Motion set),
    // the predictive path is exact and we let it stand.
    foreach (var kv in vesselEndpoints) {
      if (kv.Value.Motion != null) continue;
      if (Network.AnyLinkBucketDifference(kv.Value, ut)) {
        Network.Invalidate();
        break;
      }
    }

    if (ut >= nextSolveUT || Network.NeedsSolve) {
      var sw = System.Diagnostics.Stopwatch.StartNew();
      Network.Solve(ut);
      sw.Stop();
      var maxDt = Network.MaxTickDt();
      nextSolveUT = ut + maxDt;
      NovaLog.Log($"[Comms] solve: {sw.Elapsed.TotalMilliseconds:F3}ms ({Network.Endpoints.Count} ep, {Network.Graph.Links.Count} links) | next in {maxDt:F1}s");
    }

    if (ut - lastLogUT < LogIntervalSeconds) return;
    lastLogUT = ut;

    var graph = Network.Graph;
    var kscPos = kscEndpoint.PositionAt(ut);
    foreach (var kv in vesselEndpoints) {
      var ep = kv.Value;
      var v = FlightGlobals.Vessels.FirstOrDefault(x => x.id == kv.Key);
      var name = v?.vesselName ?? kv.Key.ToString("D");
      var distKm = (ep.PositionAt(ut) - kscPos).Magnitude / 1000.0;
      var directLink = graph.Links.FirstOrDefault(l => l.From == ep && l.To == kscEndpoint);
      var directInfo = directLink == null
        ? "no direct edge"
        : $"snr={directLink.Snr:E2} rate={directLink.RateBps:F2}";
      var path = MaxRatePath.Find(graph, ep, kscEndpoint);
      if (path == null) {
        NovaLog.Log($"[Comms] {name} → KSC: no path | d={distKm:F1}km links={graph.Links.Count} | {directInfo}");
      } else {
        var bottleneck = path.Min(l => l.RateBps);
        NovaLog.Log($"[Comms] {name} → KSC: {bottleneck:F1} bps over {path.Count} hop(s) | d={distKm:F1}km | {directInfo}");
      }
    }
  }
}
