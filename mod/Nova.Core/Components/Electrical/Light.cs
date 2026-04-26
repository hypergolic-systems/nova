using Nova.Core.Resources;
using Nova.Core.Flight;
using Nova.Core.Utils;

namespace Nova.Core.Components.Electrical;

public class Light : VirtualComponent {
  public double Rate;

  private ResourceSolver.Device device;

  public double Satisfaction => device != null ? device.Satisfaction : 0;
  public double Activity => device != null ? device.Activity : 0;
  public double ActualRate => Rate * Activity;

  public override void OnBuildSolver(ResourceSolver solver, ResourceSolver.Node node) {
    device = node.AddDevice(ResourceSolver.Priority.Low);
    device.AddInput(Resource.ElectricCharge, Rate);
    device.Demand = 1.0;
  }
}
