using System;
using Nova.Core.Resources;
using Nova.Core.Flight;
using Nova.Core.Utils;
namespace Nova.Core.Components.Propulsion;

public class ReactionWheel : VirtualComponent {
  public double PitchTorque; // kN·m
  public double YawTorque;   // kN·m
  public double RollTorque;  // kN·m

  public double ElectricRate; // EC/s at full throttle

  // Set by solver — net throttle per axis, range [-1, 1]
  public double ThrottlePitch;
  public double ThrottleYaw;
  public double ThrottleRoll;

  private ResourceSolver.Device device;

  public double Satisfaction => device != null ? device.Satisfaction : 1;

  public override void OnBuildSolver(ResourceSolver solver, ResourceSolver.Node node) {
    device = node.AddDevice(ResourceSolver.Priority.High);
    device.AddInput(Resource.ElectricCharge, ElectricRate);
  }

  public override void OnPreSolve() {
    if (device == null) return;
    double intensity = Math.Abs(ThrottlePitch) + Math.Abs(ThrottleRoll) + Math.Abs(ThrottleYaw);
    device.Demand = intensity > 0.01 ? intensity : 0;
  }
}
