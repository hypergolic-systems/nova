using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nova.Core.Communications;
using Nova.Core.Components.Communications;
using Nova.Core.Utils;

namespace Nova.Tests.Communications;

[TestClass]
public class HorizonFilterTests {

  // Antenna whose self-link r_max sits at ~RefDistance · 8 (for the
  // bucket-1 threshold with N=10 default). With RefDistance=100, the
  // pair MaxUsefulRange between two of these is ~800m.
  private static Antenna Modest() => new() {
    TxPower = 1, Gain = 1, MaxRate = 1000, RefDistance = 100,
  };

  private static Endpoint EndpointAt(string id, Func<double, Vec3d> positionAt,
      params Antenna[] antennas) {
    var ep = new Endpoint { Id = id, PositionAt = positionAt };
    ep.Antennas.AddRange(antennas);
    return ep;
  }

  // Position closure that increments a counter on every call. Lets
  // tests verify the pre-screen actually shortens the sweep instead of
  // running the full 200-step bucket bisection.
  private sealed class CountingPosition {
    public int Calls;
    private readonly Func<double, Vec3d> inner;
    public CountingPosition(Func<double, Vec3d> inner) { this.inner = inner; }
    public Vec3d Get(double ut) { Calls++; return inner(ut); }
  }

  [TestMethod]
  public void Prescreen_FarStationaryPair_SkipsBisection() {
    // Two stationary endpoints separated by far more than their pair
    // r_max. The pre-screen should detect "always out of range" and
    // skip the 200-step bucket sweep — measured via the PositionAt
    // call counter. Default sweep would do ~200 calls per direction;
    // pre-screen does PrescreenSamples + 1 (current + samples).
    var a = new CountingPosition(Orbits.Stationary(Vec3d.Zero));
    var b = new CountingPosition(Orbits.Stationary(new Vec3d(1e6, 0, 0)));
    var net = new CommunicationsNetwork();
    net.AddEndpoint(EndpointAt("A", a.Get, Modest()));
    net.AddEndpoint(EndpointAt("B", b.Get, Modest()));

    net.Solve(0);

    // Both directions should land at the horizon cap.
    var horizonCapUT = CommunicationsParameters.MaxHorizonSeconds;
    foreach (var link in net.Graph.Links) {
      Assert.AreEqual(horizonCapUT, link.NextEventUT, 1e-9,
          $"link {link.From.Id}→{link.To.Id} should sit at horizon cap");
    }

    // BuildGraph hits each PositionAt once. Pre-screen for the unordered
    // pair runs once and uses ~PrescreenSamples + 1 calls per endpoint.
    // Worst-case budget: 2 + 2·(PrescreenSamples + 1) per endpoint.
    var maxExpected = 2 * (CommunicationsParameters.PrescreenSamples + 2);
    Assert.IsTrue(a.Calls <= maxExpected,
        $"A.PositionAt called {a.Calls} times, expected ≤ {maxExpected} (pre-screen short-circuit)");
    Assert.IsTrue(b.Calls <= maxExpected,
        $"B.PositionAt called {b.Calls} times, expected ≤ {maxExpected}");
  }

  [TestMethod]
  public void Prescreen_InRangePair_StillRunsBisection() {
    // Pair within usable range. Pre-screen should NOT short-circuit;
    // detailed bisection runs and finds a real horizon (or the cap if
    // bucket is stable). Counter confirms full sweep.
    var a = new CountingPosition(Orbits.Stationary(Vec3d.Zero));
    var b = new CountingPosition(Orbits.Stationary(new Vec3d(50, 0, 0)));
    var net = new CommunicationsNetwork();
    net.AddEndpoint(EndpointAt("A", a.Get, Modest()));
    net.AddEndpoint(EndpointAt("B", b.Get, Modest()));

    net.Solve(0);

    // Stationary in-range pair → bucket constant → horizon cap.
    foreach (var link in net.Graph.Links) {
      Assert.AreEqual(CommunicationsParameters.MaxHorizonSeconds,
          link.NextEventUT, 1e-9);
    }

    // Detailed sweep should run for both directions: at least
    // HorizonSearchSteps · 2 (one direction) + same for the other.
    var minExpected = CommunicationsParameters.HorizonSearchSteps;
    Assert.IsTrue(a.Calls >= minExpected,
        $"A.PositionAt called {a.Calls} times, expected ≥ {minExpected} (full sweep)");
  }

  [TestMethod]
  public void Filter_DoesNotMissCrossings_ForReceedingPair() {
    // A satellite that starts in-range and moves out. Pre-screen sees
    // current distance ≤ pair r_max → does NOT short-circuit. Bisection
    // finds the bucket transition normally. (Regression check: the
    // pre-screen must not fire just because *some* future sample is
    // out of range.)
    var net = new CommunicationsNetwork();
    var ground = EndpointAt("G", Orbits.Stationary(Vec3d.Zero), Modest());
    var sat = EndpointAt("S",
        Orbits.Linear(new Vec3d(50, 0, 0), new Vec3d(1, 0, 0)),
        Modest());
    net.AddEndpoint(ground); net.AddEndpoint(sat);

    net.Solve(0);
    var dt = net.MaxTickDt();

    // A real event should be forecast — not the horizon cap. With the
    // satellite receding from r=50 to r ~= 86450 over the horizon, it
    // crosses every bucket plus the bucket-0 boundary along the way.
    Assert.IsTrue(dt < CommunicationsParameters.MaxHorizonSeconds - 1,
        $"expected a real geometry event well before horizon cap, got {dt}");
  }

  [TestMethod]
  public void PairMaxRange_AsymmetricAntennas_UsesMaxOverPairs() {
    // A has a weak antenna; B has a strong one. The strong-pair r_max
    // exceeds A's self-r_max. The filter must use the MAX over pairs
    // — otherwise the strong asymmetric link gets falsely pre-screened.
    var weak   = new Antenna { TxPower = 1, Gain = 1,    MaxRate = 100,  RefDistance = 100 };
    var strong = new Antenna { TxPower = 1, Gain = 1000, MaxRate = 1000, RefDistance = 100 };

    // Distance 5000m: weak↔weak would be far past range; strong→weak
    // lifts the pair above bucket 0.
    var net = new CommunicationsNetwork();
    var a = EndpointAt("A", Orbits.Stationary(Vec3d.Zero), weak);
    var b = EndpointAt("B", Orbits.Stationary(new Vec3d(5000, 0, 0)), strong);
    net.AddEndpoint(a); net.AddEndpoint(b);

    net.Solve(0);

    // At least one direction must report a non-zero rate (proof that
    // the pre-screen did NOT incorrectly skip — and BuildGraph found
    // a non-bucket-0 link).
    var anyNonZero = net.Graph.Links.Any(l => l.RateBps > 0);
    Assert.IsTrue(anyNonZero,
        "asymmetric strong-pair link should not be pre-screened away");
  }

  [TestMethod]
  public void Prescreen_ZeroAntennas_HasNoLinks() {
    // Edge case: an endpoint with no antennas can never participate.
    // The pair-iteration loop skips it before pre-screen is reached.
    var net = new CommunicationsNetwork();
    var a = EndpointAt("A", Orbits.Stationary(Vec3d.Zero), Modest());
    var silent = new Endpoint { Id = "Silent", PositionAt = Orbits.Stationary(new Vec3d(50, 0, 0)) };
    net.AddEndpoint(a); net.AddEndpoint(silent);

    var g = net.Solve(0);
    Assert.AreEqual(0, g.Links.Count);
  }

  [TestMethod]
  public void Symmetric_BothDirections_GetSameHorizonForSymmetricPair() {
    // Identical antennas + recede-in-line motion → the two directed
    // links share the same r(t) and bucket transitions. Both should
    // forecast the same NextEventUT (within bisection threshold).
    var net = new CommunicationsNetwork();
    var ground = EndpointAt("G", Orbits.Stationary(Vec3d.Zero), Modest());
    var sat = EndpointAt("S",
        Orbits.Linear(new Vec3d(50, 0, 0), new Vec3d(1, 0, 0)),
        Modest());
    net.AddEndpoint(ground); net.AddEndpoint(sat);

    net.Solve(0);
    var gs = net.Graph.Links.First(l => l.From.Id == "G" && l.To.Id == "S");
    var sg = net.Graph.Links.First(l => l.From.Id == "S" && l.To.Id == "G");
    var threshold = CommunicationsParameters.MaxHorizonSeconds * 1e-6;
    Assert.AreEqual(gs.NextEventUT, sg.NextEventUT, threshold,
        "symmetric pair should produce identical NextEventUT in both directions");
  }
}
