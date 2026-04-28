using Nova.Core.Resources;

namespace Nova.Core.Components.Control;

// Command pods, probe cores, cockpits — anything that's a vessel
// control source. Carries a continuous baseline EC draw (avionics,
// flight computer, telemetry, life support overhead) modelled as a
// single Low-priority device with `Demand = 1`. Real spacecraft sit
// in the 1–200 W range depending on class — Dragon's avionics
// idles around 75 W, a cubesat-class probe core at 1–3 W.
//
// Also carries a temporary TEST LOAD — a separate runtime-toggleable
// EC consumer used to exercise the power system (force batteries to
// drain, trip fuel-cell hysteresis, etc.). Off by default; toggled
// from the Power view via the `setCommandTestLoad` op. Doesn't
// persist across save/load — every session starts with it off so a
// forgotten toggle doesn't silently drain a vessel after a quickload.
//
// Both fields are prefab-driven via ComponentFactory.CreateCommand;
// no Structure proto.
public class Command : VirtualComponent {
  public double IdleDraw;       // W — always-on baseline avionics
  public double TestLoadRate;   // W — debug load capacity, gated by TestLoadActive

  public bool TestLoadActive;   // runtime; reset to false on each build

  internal ResourceSolver.Device idleDevice;
  internal ResourceSolver.Device testLoadDevice;

  public double IdleActivity => idleDevice != null ? idleDevice.Activity : 0;
  public double TestLoadActivity => testLoadDevice != null ? testLoadDevice.Activity : 0;

  public override VirtualComponent Clone() {
    return new Command {
      IdleDraw = IdleDraw,
      TestLoadRate = TestLoadRate,
      TestLoadActive = TestLoadActive,
    };
  }

  public override void OnBuildSolver(ResourceSolver solver, ResourceSolver.Node node) {
    if (IdleDraw > 0) {
      idleDevice = node.AddDevice(ResourceSolver.Priority.Low);
      idleDevice.AddInput(Resource.ElectricCharge, IdleDraw);
      idleDevice.Demand = 1.0;
    }
    if (TestLoadRate > 0) {
      testLoadDevice = node.AddDevice(ResourceSolver.Priority.Low);
      testLoadDevice.AddInput(Resource.ElectricCharge, TestLoadRate);
      testLoadDevice.Demand = TestLoadActive ? 1.0 : 0.0;
    }
  }

  public override void OnPreSolve() {
    if (testLoadDevice != null)
      testLoadDevice.Demand = TestLoadActive ? 1.0 : 0.0;
  }
}
