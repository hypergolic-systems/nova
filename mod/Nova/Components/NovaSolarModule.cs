using System.Linq;
using UnityEngine;
using Nova.Core.Components.Electrical;
using Nova.Core.Utils;

namespace Nova.Components;

public class NovaSolarModule : NovaPartModule {

  [KSPField]
  public string secondaryTransformName = "suncatcher";

  [KSPField]
  public string pivotName = "sunPivot";

  [KSPField]
  public bool isTracking;

  protected SolarPanel solarPanel;

  public override void OnStart(StartState state) {
    base.OnStart(state);

    solarPanel = Components.OfType<SolarPanel>().First();
    solarPanel.IsTracking = isTracking;
    ExtractPanelDirection();

    if (state == StartState.Editor) return;

    var vesselModule = vessel.FindVesselModuleImplementing<NovaVesselModule>();
    if (vesselModule != null)
      vesselModule.InvalidateSolarData();
  }

  public void ExtractPanelDirection() {
    if (isTracking) {
      var pivot = part.FindModelTransform(pivotName);
      if (pivot != null) {
        var axis = TransformDirection(pivot.up);
        solarPanel.PanelDirection = new Vec3d(axis.x, axis.y, axis.z);
      }
    } else {
      var surface = part.FindModelTransform(secondaryTransformName);
      if (surface != null) {
        var normal = TransformDirection(surface.forward);
        solarPanel.PanelDirection = new Vec3d(normal.x, normal.y, normal.z);
      }
    }
  }

  protected Vector3 TransformDirection(Vector3 worldDir) {
    // In flight, convert to vessel-local. In editor, use ship root.
    if (vessel != null)
      return vessel.transform.InverseTransformDirection(worldDir);
    var ship = EditorLogic.fetch?.ship;
    if (ship != null && ship.parts.Count > 0)
      return ship.parts[0].transform.InverseTransformDirection(worldDir);
    return worldDir;
  }
}
