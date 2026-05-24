using System;
using System.Collections.Generic;
using System.Linq;
using Nova.Core.Components;
using Nova.Core.Components.Communications;
using Nova.Core.Utils;

namespace Nova.Core.Communications;

// One node in the communications graph. Aggregates one or more
// antennas and exposes a UT→position function. Vessels and ground
// stations both project to Endpoints; the Network does not know
// which is which.
public class Endpoint {

  // Stable identity. For vessels: the KSP vessel id (Guid string).
  // For ground stations: a fixed name like "KSC".
  public string Id;

  // World-frame position at the given UT. Drivers wrap the
  // appropriate KSP/Planetarium calls; tests pass deterministic stubs.
  // Authoritative for any caller that needs a numeric position.
  public Func<double, Vec3d> PositionAt;

  // Optional solver hint: structured motion model the analytical
  // horizon solver can inspect. When both endpoints of a directed
  // link expose a compatible model (e.g. both KeplerMotion around
  // the same parent body), ComputeLinkHorizons routes through
  // AnalyticalLinkHorizon instead of numerical bisection. Null →
  // always falls back to numerical.
  public MotionModel Motion;

  // True iff PositionAt is reliable for forecasting future state —
  // i.e. the closure can be queried at arbitrary future UTs and
  // returns positions consistent with what the endpoint will
  // actually do. Default true (preserves test fixture behaviour).
  //
  // Off-rails KSP vessels under thrust set this false: KSP's
  // `getTruePositionAtUT` projects a free-flight trajectory the
  // actual vessel diverges from, so any horizon computed from it
  // is wrong. ComputeLinkHorizons skips bisection for any pair
  // involving an unpredictable endpoint and pins NextEventUT at
  // the horizon cap; the driver is expected to handle bucket
  // transitions reactively (see AnyLinkBucketDifference).
  public bool IsPredictable = true;

  // Body whose SOI this endpoint currently sits in. Drives the link's
  // occluder set together with the other endpoint's PrimaryBody. May
  // be null for test fixtures without body context — such endpoints
  // behave as "always unblocked" (empty occluder set), which is the
  // safe default. The KSP-side addon sets this from
  // `vessel.orbit.referenceBody` for vessels and the home body for
  // ground stations.
  public Body PrimaryBody;

  // Antennas attached to this endpoint. The Network selects the best
  // (transmit, receive) pair per directed edge when computing rate.
  public List<Antenna> Antennas = new();

  // Cached summary of this endpoint's path back to the network's
  // designated home (KSC in the flight scene). Populated by
  // CommunicationsNetwork.RefreshHomePathSummaries after each Solve;
  // consumers (telemetry topics, UI) read these fields directly
  // instead of re-running MaxRatePath.Find every frame — the search
  // allocates a Dictionary/HashSet/List per call, which adds up at
  // 60 Hz × N vessels and shows up as GC pressure. `HasPath` is false
  // when no positive-rate route exists; on the home endpoint itself
  // the summary is `default` (self-link not meaningful).
  public PathSummary PathToHome;

  // Build an Endpoint from a VirtualVessel by harvesting its Antenna
  // components. Used by the future addon driver; useful for any
  // integration test that wants a vessel-shaped endpoint.
  public static Endpoint FromVessel(VirtualVessel vessel, string id, Func<double, Vec3d> positionAt) {
    var ep = new Endpoint { Id = id, PositionAt = positionAt };
    ep.Antennas.AddRange(vessel.AllComponents().OfType<Antenna>());
    return ep;
  }
}

// Cached connectivity summary from one endpoint to a designated home.
// Refreshed by CommunicationsNetwork once per Solve; readers can
// poll without allocating. `Direct*` fields cover the single direct
// edge (vessel→home) — they're 0 when no direct edge exists (vessel
// reachable only via relay), in which case `HasPath`/`BottleneckBps`
// still reflect the relayed path. `NextHopId` is the Id of the
// first endpoint along the chosen vessel→home path: equals home.Id
// when direct, equals a relay vessel's Id otherwise — empty when
// HasPath is false.
// `DirectSnrFloor` is the linear SNR threshold below which the
// direct edge's quantised rate drops to zero — the noise floor for
// THIS antenna pair, not the model-wide N0 = 1.0. Computed from the
// chosen TX antenna's reference SNR via the bucket-1 cutoff
// (1 + SNR_ref)^(1/N) − 1.
public struct PathSummary {
  public bool HasPath;
  public double BottleneckBps;
  public double DirectSnr;
  public double DirectRateBps;
  public double DirectMaxRateBps;
  public double DirectSnrFloor;
  public string NextHopId;
  // Ordered Links along the chosen path source→home, populated once
  // per Solve by RefreshHomePathSummaries. Null on the home endpoint
  // and when HasPath is false. Readers must treat this as a snapshot
  // — the underlying Link references are reused across Solves but
  // their fields (RateBps etc.) are stable within a Solve cycle.
  public IReadOnlyList<Link> Path;
}
