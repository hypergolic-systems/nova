using Nova.Core.Resources;
using Nova.Core.Systems;

namespace Nova.Core.Components.Electrical;

public class Light : VirtualComponent {
  public double Rate;

  private ProcessFlowSystem.Device device;

  public double Satisfaction => device != null ? device.Satisfaction : 0;
  public double Activity => device != null ? device.Activity : 0;
  public double ActualRate => Rate * Activity;

  public override void OnBuildSystems(VesselSystems systems, StagingFlowSystem.Node node) {
    device = systems.Process.AddDevice(ProcessFlowSystem.Priority.Low);
    device.AddInput(Resource.ElectricCharge, Rate);
    device.Demand = 1.0;
  }
}
