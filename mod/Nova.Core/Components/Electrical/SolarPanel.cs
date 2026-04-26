using Nova.Core.Resources;
using Nova.Core.Flight;
using Nova.Core.Utils;

namespace Nova.Core.Components.Electrical;

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
  public double EffectiveRate;
  public bool IsSunlit = true;
  public double ShadowTransitionUT = double.PositiveInfinity;

  private ResourceSolver.Device device;

  public override void OnBuildSolver(ResourceSolver solver, ResourceSolver.Node node) {
    device = node.AddDevice(ResourceSolver.Priority.Low);
    // Topology coefficient is the panel's rated max. Per-tick gating
    // (deploy state + sun visibility) happens via MaxActivity, and the
    // sun-angle / exposure scale lives in EffectiveRate (set by
    // ComputeSolarRates / SolarOptimizer at deploy events).
    device.AddOutput(Resource.ElectricCharge, ChargeRate);
    device.Demand = 1.0;
  }

  public override void OnPreSolve() {
    if (device == null) return;
    // Binary on/off based on deploy state + sun visibility. Magnitude
    // when on is EffectiveRate / ChargeRate (≤ 1), so the LP variable
    // tops out at the optimal-orientation rate, not the rated rate.
    var scale = ChargeRate > 1e-9 ? EffectiveRate / ChargeRate : 0;
    device.MaxActivity = (IsDeployed && IsSunlit) ? scale : 0;
    device.ValidUntil = ShadowTransitionUT;
  }
}
