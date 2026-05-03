using Nova.Core.Resources;
using Nova.Core.Systems;

namespace Nova.Core.Components.Electrical;

public class Light : VirtualComponent {
  public double Rate;

  private Device device;

  public double Satisfaction => device?.Satisfaction ?? 0;
  public double Activity => device?.Activity ?? 0;
  public double ActualRate => Rate * Activity;

  public override void OnBuildSystems(VesselSystems systems, StagingFlowSystem.Node node) {
    device = systems.AddDevice(node,
        inputs: new[] { (Resource.ElectricCharge, Rate) });
    device.Demand = 1.0;
  }
}
