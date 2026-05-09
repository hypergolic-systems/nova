using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nova.Core.Communications;
using Nova.Core.Components.Communications;
using Nova.Core.Utils;

namespace Nova.Tests.Communications;

[TestClass]
public class GroundStationTests {

  // Stub homeworld at the origin so the rotating-body math is the only
  // contributor to the emitted position.
  private static readonly Func<double, Vec3d> OriginBody = _ => Vec3d.Zero;

  private static List<Antenna> OneAntenna() => new() {
    new() { TxPower = 1, Gain = 1, MaxRate = 1, RefDistance = 1 },
  };

  [TestMethod]
  public void SurfaceEndpoint_Equator_LongitudeZero_AtUtZero_LiesOnPlusX() {
    var ep = GroundStation.SurfaceEndpoint(
      id: "test",
      latitudeDeg: 0, longitudeDeg: 0, altitudeM: 0,
      bodyRadiusM: 100, siderealPeriodS: 10,
      bodyPositionAt: OriginBody,
      antennas: OneAntenna());

    var p = ep.PositionAt(0);
    Assert.AreEqual(100, p.X, 1e-9);
    Assert.AreEqual(0, p.Y, 1e-9);
    Assert.AreEqual(0, p.Z, 1e-9);
  }

  [TestMethod]
  public void SurfaceEndpoint_RotatesEastward_HalfPeriodFlipsSign() {
    var ep = GroundStation.SurfaceEndpoint(
      id: "test",
      latitudeDeg: 0, longitudeDeg: 0, altitudeM: 0,
      bodyRadiusM: 100, siderealPeriodS: 10,
      bodyPositionAt: OriginBody,
      antennas: OneAntenna());

    var p = ep.PositionAt(5); // half a sidereal day
    Assert.AreEqual(-100, p.X, 1e-9);
    Assert.AreEqual(0, p.Y, 1e-9);
    Assert.AreEqual(0, p.Z, 1e-6);
  }

  [TestMethod]
  public void SurfaceEndpoint_NorthPole_PositionInvariantUnderRotation() {
    var ep = GroundStation.SurfaceEndpoint(
      id: "pole",
      latitudeDeg: 90, longitudeDeg: 0, altitudeM: 0,
      bodyRadiusM: 100, siderealPeriodS: 10,
      bodyPositionAt: OriginBody,
      antennas: OneAntenna());

    var p0 = ep.PositionAt(0);
    var p1 = ep.PositionAt(2.5);
    var p2 = ep.PositionAt(7.7);

    Assert.AreEqual(0, p0.X, 1e-9);
    Assert.AreEqual(100, p0.Y, 1e-9);
    Assert.AreEqual(0, p0.Z, 1e-9);
    Assert.AreEqual(p0.X, p1.X, 1e-9);
    Assert.AreEqual(p0.Y, p1.Y, 1e-9);
    Assert.AreEqual(p0.Z, p1.Z, 1e-9);
    Assert.AreEqual(p0.X, p2.X, 1e-9);
    Assert.AreEqual(p0.Y, p2.Y, 1e-9);
    Assert.AreEqual(p0.Z, p2.Z, 1e-9);
  }

  [TestMethod]
  public void SurfaceEndpoint_DistanceFromBodyCentre_EqualsRadiusPlusAltitude() {
    var ep = GroundStation.SurfaceEndpoint(
      id: "wherever",
      latitudeDeg: 37.0, longitudeDeg: -122.0, altitudeM: 250,
      bodyRadiusM: 600_000, siderealPeriodS: 21549.425,
      bodyPositionAt: OriginBody,
      antennas: OneAntenna());

    for (int i = 0; i < 5; i++) {
      var p = ep.PositionAt(i * 1234.0);
      Assert.AreEqual(600_250, p.Magnitude, 1e-6);
    }
  }

  [TestMethod]
  public void SurfaceEndpoint_BodyTranslation_TracksHomeworld() {
    var moving = (Func<double, Vec3d>)(ut => new Vec3d(1e9 + ut, 0, 0));
    var ep = GroundStation.SurfaceEndpoint(
      id: "test",
      latitudeDeg: 0, longitudeDeg: 0, altitudeM: 0,
      bodyRadiusM: 100, siderealPeriodS: 10,
      bodyPositionAt: moving,
      antennas: OneAntenna());

    var p = ep.PositionAt(0);
    Assert.AreEqual(1e9 + 100, p.X, 1e-3);

    var pLater = ep.PositionAt(5);
    Assert.AreEqual(1e9 + 5 - 100, pLater.X, 1e-3); // body moved by 5 along +X; surface point now on -X side of (translated) body
  }

  [TestMethod]
  public void Ksc_HasOneAntenna_AndIdMatches() {
    var ep = GroundStation.Ksc(OriginBody);
    Assert.AreEqual("KSC", ep.Id);
    Assert.AreEqual(1, ep.Antennas.Count);
    Assert.IsTrue(ep.Antennas[0].TxPower > 0);
    Assert.IsTrue(ep.Antennas[0].MaxRate > 0);
  }

  [TestMethod]
  public void Ksc_PositionMagnitude_IsKerbinRadiusPlusAltitude() {
    var ep = GroundStation.Ksc(OriginBody);
    var p = ep.PositionAt(123.0);
    // 600 km + 75 m
    Assert.AreEqual(600_075, p.Magnitude, 1e-3);
  }
}
