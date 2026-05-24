using System.Collections.Generic;
using CommNet;
using UnityEngine;
using Vectrosity;
using Nova.Core.Communications;
using Nova.Core.Utils;

namespace Nova.Communications;

// Replacement for stock CommNet.CommNetUI: inherits stock's Vectrosity
// + ScaledSpace + map-view machinery and overrides UpdateDisplay to
// draw the active vessel's path-to-KSC computed by Nova's
// CommunicationsNetwork (one source of truth — stock CommNetwork
// stays empty because Nova's antennas don't implement ICommAntenna).
//
// Lifecycle: instantiated in CommNetScenario.Start via the Harmony
// patch in CommNetScenarioPatches; base.Awake sets CommNetUI.Instance
// to this; base.Start registers UpdateDisplay with TimingManager.
public class NovaCommNetUI : CommNetUI {

  protected override void UpdateDisplay() {
    var v = FlightGlobals.ActiveVessel;
    var addon = NovaCommunicationsAddon.Instance;
    if (v == null || addon == null || addon.Network == null) {
      Deactivate();
      return;
    }

    var ep = addon.GetVesselEndpoint(v.id);
    var path = ep?.PathToHome.Path;
    if (path == null || path.Count == 0) {
      Deactivate();
      return;
    }

    var ut = Planetarium.GetUniversalTime();
    var segCount = path.Count;
    var expected = segCount * 2;

    points.Clear();
    for (int i = 0; i < segCount; i++) {
      points.Add(ToVector3(path[i].From.PositionAt(ut)));
      points.Add(ToVector3(path[i].To.PositionAt(ut)));
    }
    ScaledSpace.LocalToScaledSpace(points);

    var draw3D = MapView.Draw3DLines;
    if (refreshLines || line == null || line.points3.Count != expected || draw3D != draw3dLines) {
      CreateLine(ref line, points);
      draw3dLines = draw3D;
      refreshLines = false;
    } else {
      for (int i = 0; i < expected; i++) {
        line.points3[i] = points[i];
      }
    }
    line.active = true;

    for (int i = 0; i < segCount; i++) {
      var l = path[i];
      var strength = l.MaxRateBps > 0 ? l.RateBps / l.MaxRateBps : 0.0;
      var t = Mathf.Pow((float)strength, colorLerpPower);
      var c = swapHighLow
        ? Color.Lerp(colorHigh, colorLow, t)
        : Color.Lerp(colorLow, colorHigh, t);
      line.SetColor(c, i);
    }

    if (draw3D) {
      line.SetWidth(lineWidth3D);
      line.Draw3D();
    } else {
      line.SetWidth(lineWidth2D);
      line.Draw();
    }
  }

  private void Deactivate() {
    if (line != null) line.active = false;
    points?.Clear();
  }

  private static Vector3 ToVector3(Vec3d v) =>
    new Vector3((float)v.X, (float)v.Y, (float)v.Z);
}
