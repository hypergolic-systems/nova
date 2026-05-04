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

  // Antennas attached to this endpoint. The Network selects the best
  // (transmit, receive) pair per directed edge when computing rate.
  public List<Antenna> Antennas = new();

  // Build an Endpoint from a VirtualVessel by harvesting its Antenna
  // components. Used by the future addon driver; useful for any
  // integration test that wants a vessel-shaped endpoint.
  public static Endpoint FromVessel(VirtualVessel vessel, string id, Func<double, Vec3d> positionAt) {
    var ep = new Endpoint { Id = id, PositionAt = positionAt };
    ep.Antennas.AddRange(vessel.AllComponents().OfType<Antenna>());
    return ep;
  }
}
