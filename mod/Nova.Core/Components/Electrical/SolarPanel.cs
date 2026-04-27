using Nova.Core.Resources;
using Nova.Core.Flight;
using Nova.Core.Utils;

namespace Nova.Core.Components.Electrical;

// Per-panel state for the UI / telemetry. The LP entry is owned by
// `VirtualVessel` as a single aggregate solar Device summed across every
// panel on the vessel — this component contributes its `ChargeRate` to
// that sum, and its deploy/sunlit/EffectiveRate fields drive the per-
// panel rows in the Power view, but it does not register its own LP
// device.
public class SolarPanel : VirtualComponent {
  public double ChargeRate;
  public Vec3d PanelDirection;
  public bool IsTracking;
  public bool IsDeployed = true;
  // True iff the panel can be retracted after deployment. Fixed
  // (non-deployable) panels and one-shot deployables both leave this
  // false — the UI surfaces a toggle only when this is true, an open
  // button only when !IsDeployed.
  public bool IsRetractable;
  // Pro-rata share of the vessel-aggregate optimal rate, in EC/s. Set
  // by `VirtualVessel.ComputeSolarRates` whenever deploy state changes;
  // displayed by the UI's per-panel rows.
  public double EffectiveRate;
  public bool IsSunlit = true;
  public double ShadowTransitionUT = double.PositiveInfinity;
}
