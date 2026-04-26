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

  private ResourceSolver.Converter converter;

  public override void OnBuildSolver(ResourceSolver solver, ResourceSolver.Node node) {
    converter = node.AddConverter();
    converter.AddOutput(Resource.ElectricCharge, EffectiveRate);
  }

  public override void OnPreSolve() {
    if (converter == null || converter.outputs.Count == 0) return;
    double rate = IsSunlit ? EffectiveRate : 0;
    converter.outputs[0] = (Resource.ElectricCharge, rate);
    converter.ValidUntil = ShadowTransitionUT;
  }}
