using System;
using System.Collections.Generic;
using Nova.Core.Components.Communications;
using Nova.Core.Utils;

namespace Nova.Core.Communications;

// Hard-coded ground-station endpoints. Today this is just KSC; if
// more stations land, they get cfg-driven via the same code path
// rather than expanding the hard-coded set.
public static class GroundStation {

  // KSC on Kerbin. Latitude/longitude/altitude per stock KSP launch
  // pad coordinates; body parameters per the homeworld definition.
  private const double KscLatitudeDeg = -0.0972;
  private const double KscLongitudeDeg = -74.5577;
  private const double KscAltitudeM = 75;
  private const double KerbinRadiusM = 600_000;
  private const double KerbinSiderealPeriodS = 21549.425;

  // KSC's deep-space-network-class transmitter. Tx and Gain set
  // several orders of magnitude above any vessel-mounted antenna so
  // the homeworld stays the dominant link partner; RefDistance set
  // to outer-system reach so distant probes still close a usable
  // link to KSC at full hardware rate.
  private const double KscTxPower = 1.0e9;
  private const double KscGain = 1.0e3;
  private const double KscMaxRate = 1.0e9;
  private const double KscRefDistance = 1.0e10;

  // Build the KSC endpoint. `homeworldPositionAt` supplies the
  // body's centre at a given UT — KSP wraps Planetarium; tests can
  // pass a constant. `initialRotationDeg` is the body's rotation
  // phase at ut=0 (KSP CelestialBody.initialRotation); without it,
  // the surface point is placed in the wrong inertial-frame
  // longitude. Defaults to 0 for tests.
  public static Endpoint Ksc(Func<double, Vec3d> homeworldPositionAt,
      double initialRotationDeg = 0) {
    return SurfaceEndpoint(
      id: "KSC",
      latitudeDeg: KscLatitudeDeg,
      longitudeDeg: KscLongitudeDeg,
      altitudeM: KscAltitudeM,
      bodyRadiusM: KerbinRadiusM,
      siderealPeriodS: KerbinSiderealPeriodS,
      initialRotationDeg: initialRotationDeg,
      bodyPositionAt: homeworldPositionAt,
      antennas: new List<Antenna> {
        new() {
          TxPower = KscTxPower,
          Gain = KscGain,
          MaxRate = KscMaxRate,
          RefDistance = KscRefDistance,
        },
      });
  }

  // Generic surface-mounted endpoint. The body is assumed to spin
  // around its Y axis (KSP convention: polar axis = +Y); rotation
  // is eastward (positive longitude direction with positive UT).
  // Body-fixed (lat, lon, alt) → body-frame Cartesian → world frame
  // via bodyPositionAt(ut) translation.
  //
  // `initialRotationDeg` is the body's rotation phase at ut=0. The
  // body's rotation at UT is `initialRotation + 360°·UT/period`
  // (KSP's CelestialBody convention). Tests pass 0; live KSP code
  // passes `homeBody.initialRotation`.
  public static Endpoint SurfaceEndpoint(
      string id,
      double latitudeDeg, double longitudeDeg, double altitudeM,
      double bodyRadiusM, double siderealPeriodS,
      Func<double, Vec3d> bodyPositionAt,
      List<Antenna> antennas,
      double initialRotationDeg = 0) {

    var latRad = latitudeDeg * Math.PI / 180.0;
    var lonRad = longitudeDeg * Math.PI / 180.0;
    var initialRotationRad = initialRotationDeg * Math.PI / 180.0;
    var radius = bodyRadiusM + altitudeM;
    var omega = 2 * Math.PI / siderealPeriodS;
    var cosLat = Math.Cos(latRad);
    var sinLat = Math.Sin(latRad);

    Vec3d Position(double ut) {
      var phase = lonRad + initialRotationRad + omega * ut;
      var local = new Vec3d(
        radius * cosLat * Math.Cos(phase),
        radius * sinLat,
        radius * cosLat * Math.Sin(phase));
      return bodyPositionAt(ut) + local;
    }

    var ep = new Endpoint { Id = id, PositionAt = Position };
    ep.Antennas.AddRange(antennas);
    return ep;
  }
}
