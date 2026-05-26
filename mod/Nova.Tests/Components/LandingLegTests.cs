using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nova.Core.Components;
using Nova.Core.Components.Electrical;
using Nova.Core.Components.Structural;
using Nova.Core.Resources;
using Buffer = Nova.Core.Resources.Buffer;

namespace Nova.Tests.Components;

[TestClass]
public class LandingLegTests {

  private static LandingLeg MakeLeg(double motorPowerW = 100, double deploySeconds = 2) {
    return new LandingLeg {
      MotorPowerW = motorPowerW,
      DeploySeconds = deploySeconds,
      Activated = true,
      Position = 0,
      TargetPosition = 0,
    };
  }

  private static Battery MakeBattery(double capacity, double contents,
      double maxRate = 1e6) {
    return new Battery {
      Buffer = new Buffer {
        Resource = Resource.ElectricCharge,
        Capacity = capacity, Contents = contents,
        MaxRateIn = maxRate, MaxRateOut = maxRate,
      },
    };
  }

  private static VirtualVessel BuildVessel(params VirtualComponent[] components) {
    var vv = new VirtualVessel();
    vv.AddPart(1u, "test", 1.0, components.ToList());
    vv.UpdatePartTree(new Dictionary<uint, uint?> { { 1u, null } });
    vv.InitializeSolver(0);
    return vv;
  }

  // ---------- Construction + derived ----------

  [TestMethod]
  public void FullSpeedPerSecond_IsReciprocalOfDeploySeconds() {
    Assert.AreEqual(0.5, new LandingLeg { DeploySeconds = 2 }.FullSpeedPerSecond);
    Assert.AreEqual(0.25, new LandingLeg { DeploySeconds = 4 }.FullSpeedPerSecond);
  }

  [TestMethod]
  public void FullSpeedPerSecond_HandlesZeroDeploySeconds() {
    // Defensive — a misconfigured cfg shouldn't divide by zero.
    Assert.AreEqual(0, new LandingLeg { DeploySeconds = 0 }.FullSpeedPerSecond);
  }

  [TestMethod]
  public void IsMoving_TrueWhenPositionNeTarget() {
    var leg = MakeLeg();
    Assert.IsFalse(leg.IsMoving);
    leg.TargetPosition = 1.0;
    Assert.IsTrue(leg.IsMoving);
    leg.Position = 1.0;
    Assert.IsFalse(leg.IsMoving);
  }

  [TestMethod]
  public void MaxEcW_EqualsMotorPowerW() {
    Assert.AreEqual(75, new LandingLeg { MotorPowerW = 75 }.MaxEcW);
  }

  // ---------- EC demand ----------

  [TestMethod]
  public void Stationary_DemandsZeroEc() {
    var leg = MakeLeg(motorPowerW: 100);
    leg.Position = 0; leg.TargetPosition = 0;
    var vessel = BuildVessel(leg, MakeBattery(1e6, 5e5));
    vessel.Tick(0.001);
    Assert.AreEqual(0, leg.CurrentEcW, "Stationary leg draws no power");
    Assert.AreEqual(0, leg.Activity, 1e-9);
  }

  [TestMethod]
  public void Moving_WithSufficientBus_DrawsFullMotorPower() {
    var leg = MakeLeg(motorPowerW: 100);
    leg.Position = 0; leg.TargetPosition = 1;
    var vessel = BuildVessel(leg, MakeBattery(1e6, 5e5));
    vessel.Tick(0.001);
    Assert.AreEqual(1.0, leg.Activity, 1e-3,
        "Full-bus motor should solve to Activity=1");
    Assert.AreEqual(100, leg.CurrentEcW, 1e-3);
  }

  [TestMethod]
  public void Moving_WithStarvedBus_FreezesAtZeroActivity() {
    var leg = MakeLeg(motorPowerW: 100);
    leg.Position = 0.5; leg.TargetPosition = 1.0;
    // No source — bus offers nothing, leg freezes.
    var vessel = BuildVessel(leg, MakeBattery(1, 0, maxRate: 0));
    vessel.Tick(0.001);
    Assert.AreEqual(0, leg.Activity, 1e-6);
    Assert.AreEqual(0, leg.CurrentEcW, 1e-6);
    Assert.IsTrue(leg.IsMoving,
        "IsMoving stays true while position != target — freeze is a per-tick consequence");
  }

  [TestMethod]
  public void StationaryAfterDeploy_BusFreesUpForOtherLoads() {
    var leg = MakeLeg(motorPowerW: 100);
    leg.Position = 1; leg.TargetPosition = 1;
    var sibling = new Light { Rate = 50 };
    var vessel = BuildVessel(leg, sibling, MakeBattery(1e6, 5e5));
    vessel.Tick(0.001);
    Assert.IsTrue(sibling.Activity > 0.99,
        "Sibling load should run at full demand when leg is idle");
    Assert.AreEqual(0, leg.CurrentEcW);
  }

  // ---------- Persistence round-trip ----------
  //
  // Tests pair field-equality (proto source-of-truth) with observable
  // round-trip on a second instance — per the
  // `feedback_persistence_tests_two_shapes` policy in memory.

  [TestMethod]
  public void Save_WritesAllProtoFields() {
    var leg = MakeLeg();
    leg.RequiresStaging = true;
    leg.StartsDeployed = true;
    leg.Activated = true;
    leg.Position = 0.42;
    leg.TargetPosition = 1.0;

    var state = new Nova.Core.Persistence.Protos.PartState();
    leg.Save(state);

    Assert.IsNotNull(state.LandingLeg);
    Assert.IsTrue(state.LandingLeg.RequiresStaging);
    Assert.IsTrue(state.LandingLeg.StartsDeployed);
    Assert.IsTrue(state.LandingLeg.Activated);
    Assert.AreEqual(0.42, state.LandingLeg.Position, 1e-9);
    Assert.AreEqual(1.0, state.LandingLeg.TargetPosition, 1e-9);
  }

  [TestMethod]
  public void Save_And_Load_RoundTrip() {
    var src = MakeLeg();
    src.RequiresStaging = true;
    src.StartsDeployed = true;
    src.Activated = true;
    src.Position = 0.42;
    src.TargetPosition = 1.0;

    var state = new Nova.Core.Persistence.Protos.PartState();
    src.Save(state);

    var dst = MakeLeg();
    dst.RequiresStaging = false;
    dst.StartsDeployed = false;
    dst.Activated = false;
    dst.Position = 0; dst.TargetPosition = 0;
    dst.Load(state);

    Assert.IsTrue(dst.RequiresStaging);
    Assert.IsTrue(dst.StartsDeployed);
    Assert.IsTrue(dst.Activated);
    Assert.AreEqual(0.42, dst.Position, 1e-9);
    Assert.AreEqual(1.0, dst.TargetPosition, 1e-9);
    Assert.IsTrue(dst.LoadedFromSave);
  }

  [TestMethod]
  public void Load_WithNoLandingLegState_LeavesDefaults() {
    var leg = MakeLeg();
    leg.Activated = false;
    leg.RequiresStaging = true;
    leg.Load(new Nova.Core.Persistence.Protos.PartState());
    Assert.IsFalse(leg.Activated);
    Assert.IsTrue(leg.RequiresStaging);
    Assert.IsFalse(leg.LoadedFromSave);
  }

  // ---------- Clone ----------

  [TestMethod]
  public void Clone_PreservesAllState() {
    var src = MakeLeg(motorPowerW: 75, deploySeconds: 3);
    src.RequiresStaging = false;
    src.StartsDeployed = true;
    src.Activated = true;
    src.Position = 0.6;
    src.TargetPosition = 1.0;
    var dst = (LandingLeg)src.Clone();
    Assert.AreEqual(75, dst.MotorPowerW);
    Assert.AreEqual(3, dst.DeploySeconds);
    Assert.IsFalse(dst.RequiresStaging);
    Assert.IsTrue(dst.StartsDeployed);
    Assert.IsTrue(dst.Activated);
    Assert.AreEqual(0.6, dst.Position);
    Assert.AreEqual(1.0, dst.TargetPosition);
  }
}
