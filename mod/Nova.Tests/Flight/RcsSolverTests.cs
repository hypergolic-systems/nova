using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nova.Core.Resources;
using Nova.Core.Flight;
using Nova.Core.Utils;
namespace Nova.Tests.Flight;

[TestClass]
public class RcsSolverTests {

  private const double Tol = 0.01;

  [TestMethod]
  public void NoInput_AllZero() {
    var thrusters = new[] {
      MakeThruster(new Vec3d(0, 1, 0), new Vec3d(1, 0, 0), 1),
    };
    var input = new RcsSolver.Input {
      CoM = Vec3d.Zero,
      DesiredForce = Vec3d.Zero,
      DesiredTorque = Vec3d.Zero,
    };
    var result = SolveNew(thrusters, input);
    Assert.AreEqual(0, result[0], Tol);
  }

  [TestMethod]
  public void SingleThruster_AlignedTranslation() {
    // Thruster at origin pointing +X, desired force in +X.
    var thrusters = new[] {
      MakeThruster(Vec3d.Zero, new Vec3d(1, 0, 0), 1),
    };
    var input = new RcsSolver.Input {
      CoM = Vec3d.Zero,
      DesiredForce = new Vec3d(1, 0, 0),
      DesiredTorque = Vec3d.Zero,
    };
    var result = SolveNew(thrusters, input);
    // Thruster at CoM → zero torque automatically. Should fire at full.
    Assert.AreEqual(1, result[0], Tol);
  }

  [TestMethod]
  public void SingleThruster_PerpendicularTranslation() {
    // Thruster pointing +X, desired force in +Y.
    var thrusters = new[] {
      MakeThruster(Vec3d.Zero, new Vec3d(1, 0, 0), 1),
    };
    var input = new RcsSolver.Input {
      CoM = Vec3d.Zero,
      DesiredForce = new Vec3d(0, 1, 0),
      DesiredTorque = Vec3d.Zero,
    };
    var result = SolveNew(thrusters, input);
    Assert.AreEqual(0, result[0], Tol);
  }

  [TestMethod]
  public void OpposingThrusters_PureRotation() {
    // Two thrusters producing +Z torque with cancelling forces.
    // Thruster at +Y pointing -X: torque = Cross((0,2,0),(-1,0,0)) = (0,0,2)
    // Thruster at -Y pointing +X: torque = Cross((0,-2,0),(1,0,0)) = (0,0,2)
    // Forces cancel: (-1,0,0) + (1,0,0) = 0.
    var thrusters = new[] {
      MakeThruster(new Vec3d(0, 2, 0), new Vec3d(-1, 0, 0), 1),
      MakeThruster(new Vec3d(0, -2, 0), new Vec3d(1, 0, 0), 1),
    };
    var input = new RcsSolver.Input {
      CoM = Vec3d.Zero,
      DesiredForce = Vec3d.Zero,
      DesiredTorque = new Vec3d(0, 0, 1),
    };
    var result = SolveNew(thrusters, input);
    // Both should fire equally — forces cancel, net torque in +Z.
    // QP targets normalized torque: each thruster produces (0,0,1) normalized,
    // τ̃_des = (0,0,0.5), so t1+t2 ≈ 0.5, each ≈ 0.25.
    Assert.AreEqual(result[0], result[1], Tol);
    Assert.IsTrue(result[0] > 0.2, $"Expected significant throttle, got {result[0]}");
  }

  [TestMethod]
  public void SymmetricPair_TranslationNoTorque() {
    // Two thrusters symmetric about CoM, both pointing +X.
    // Translation in +X should fire both equally with zero net torque.
    var thrusters = new[] {
      MakeThruster(new Vec3d(0, 1, 0), new Vec3d(1, 0, 0), 1),
      MakeThruster(new Vec3d(0, -1, 0), new Vec3d(1, 0, 0), 1),
    };
    var input = new RcsSolver.Input {
      CoM = Vec3d.Zero,
      DesiredForce = new Vec3d(1, 0, 0),
      DesiredTorque = Vec3d.Zero,
    };
    var result = SolveNew(thrusters, input);
    // Full input → full throttle on both aligned thrusters.
    Assert.AreEqual(result[0], result[1], Tol);
    Assert.AreEqual(1.0, result[0], Tol);
  }

  [TestMethod]
  public void AsymmetricLeverArm_FurtherThrusterFiresSofter() {
    // Two thrusters both pointing +X, on opposite sides of CoM at different distances.
    // Close at (0,1,0): torque = Cross((0,1,0),(1,0,0)) = (0,0,-1)
    // Far at (0,-5,0): torque = Cross((0,-5,0),(1,0,0)) = (0,0,5)
    // With torque tolerance band (5% of max thruster torque = 0.25):
    // the far thruster can fire harder than strict balance requires.
    // Close thruster should still fire harder than far.
    var thrusters = new[] {
      MakeThruster(new Vec3d(0, 1, 0), new Vec3d(1, 0, 0), 1),  // close
      MakeThruster(new Vec3d(0, -5, 0), new Vec3d(1, 0, 0), 1), // far
    };
    var input = new RcsSolver.Input {
      CoM = Vec3d.Zero,
      DesiredForce = new Vec3d(1, 0, 0),
      DesiredTorque = Vec3d.Zero,
    };
    var result = SolveNew(thrusters, input);
    // Full input → both fire near-full. Close fires at 1.0, far slightly less
    // due to torque penalty (far thruster produces 5x more torque per unit throttle).
    Assert.IsTrue(result[0] >= result[1],
      $"Close thruster ({result[0]}) should fire at least as hard as far ({result[1]})");
    Assert.AreEqual(1.0, result[0], Tol, "Close thruster should be at full");
    Assert.IsTrue(result[1] > 0.9, $"Far thruster should fire near-full, got {result[1]}");
  }

  [TestMethod]
  public void CombinedForceAndTorque() {
    // Four thrusters in a + pattern around CoM, all pointing +X.
    // Request +X translation AND +Z rotation simultaneously.
    var thrusters = new[] {
      MakeThruster(new Vec3d(0, 2, 0), new Vec3d(1, 0, 0), 1),  // top
      MakeThruster(new Vec3d(0, -2, 0), new Vec3d(1, 0, 0), 1), // bottom
      MakeThruster(new Vec3d(0, 0, 2), new Vec3d(1, 0, 0), 1),  // front
      MakeThruster(new Vec3d(0, 0, -2), new Vec3d(1, 0, 0), 1), // back
    };
    var input = new RcsSolver.Input {
      CoM = Vec3d.Zero,
      DesiredForce = new Vec3d(1, 0, 0),
      DesiredTorque = new Vec3d(0, 0, 1),
    };
    var result = SolveNew(thrusters, input);
    // All should produce some thrust (nonzero).
    for (int i = 0; i < 4; i++)
      Assert.IsTrue(result[i] >= -Tol, $"Thruster {i} should not be negative: {result[i]}");
    // At least some should be firing.
    var total = result[0] + result[1] + result[2] + result[3];
    Assert.IsTrue(total > 0.1, $"Expected some thrust, got total {total}");
  }

  [TestMethod]
  public void OpposingThrusters_NoCancellingWaste() {
    // Four thrusters at CoM pointing along ±X and ±Z.
    // Desired force: +Z. Only the +Z thruster should fire.
    // Without fuel minimization, the solver could fire +X and -X at equal
    // throttle (forces cancel, constraints satisfied) — wasting propellant.
    var thrusters = new[] {
      MakeThruster(Vec3d.Zero, new Vec3d(1, 0, 0), 1),   // +X
      MakeThruster(Vec3d.Zero, new Vec3d(-1, 0, 0), 1),  // -X
      MakeThruster(Vec3d.Zero, new Vec3d(0, 0, 1), 1),   // +Z
      MakeThruster(Vec3d.Zero, new Vec3d(0, 0, -1), 1),  // -Z
    };
    var input = new RcsSolver.Input {
      CoM = Vec3d.Zero,
      DesiredForce = new Vec3d(0, 0, 1),
      DesiredTorque = Vec3d.Zero,
    };
    var result = SolveNew(thrusters, input);
    Assert.AreEqual(0, result[0], Tol, "+X thruster should not fire");
    Assert.AreEqual(0, result[1], Tol, "-X thruster should not fire");
    Assert.AreEqual(1, result[2], Tol, "+Z thruster should fire at full");
    Assert.AreEqual(0, result[3], Tol, "-Z thruster should not fire");
  }

  [TestMethod]
  public void EightQuadBlocks_SymmetricTranslation_OnlyAlignedFire() {
    // 8 quad RCS blocks: 4 at each end of the vessel, 90° apart radially.
    // Each block has 4 nozzles: forward, backward, and two perpendicular.
    // Desired: translate forward (+X). Only the 8 forward-facing nozzles should fire.
    double fwd = 5;  // forward station X
    double aft = -5; // aft station X
    double r = 2;    // radial offset

    var thrusters = new List<RcsSolver.Thruster>();

    // Build a quad block at the given position with nozzles along ±axial and ±radial.
    // Nozzle tips are offset from block center, and directions are slightly perturbed
    // (realistic: KSP joint flex causes ~0.005 rad direction variation between parts).
    int seed = 42;
    void AddBlock(double x, double y, double z, Vec3d radialDir) {
      double d = 0.08;
      double p() => (new Random(seed++).NextDouble() - 0.5) * 0.01; // ±0.005 perturbation
      thrusters.Add(MakeThruster(new Vec3d(x + d, y, z), new Vec3d(1 + p(), p(), p()).Normalized, 1));
      thrusters.Add(MakeThruster(new Vec3d(x - d, y, z), new Vec3d(-1 + p(), p(), p()).Normalized, 1));
      thrusters.Add(MakeThruster(new Vec3d(x, y + d * radialDir.Y, z + d * radialDir.Z),
        (radialDir + new Vec3d(p(), p(), p())).Normalized, 1));
      thrusters.Add(MakeThruster(new Vec3d(x, y - d * radialDir.Y, z - d * radialDir.Z),
        (-1 * radialDir + new Vec3d(p(), p(), p())).Normalized, 1));
    }

    // Forward station: 4 blocks at 90° intervals.
    AddBlock(fwd, r, 0, new Vec3d(0, 1, 0));
    AddBlock(fwd, -r, 0, new Vec3d(0, -1, 0));
    AddBlock(fwd, 0, r, new Vec3d(0, 0, 1));
    AddBlock(fwd, 0, -r, new Vec3d(0, 0, -1));

    // Aft station: 4 blocks at 90° intervals.
    AddBlock(aft, r, 0, new Vec3d(0, 1, 0));
    AddBlock(aft, -r, 0, new Vec3d(0, -1, 0));
    AddBlock(aft, 0, r, new Vec3d(0, 0, 1));
    AddBlock(aft, 0, -r, new Vec3d(0, 0, -1));

    var input = new RcsSolver.Input {
      CoM = Vec3d.Zero,
      DesiredForce = new Vec3d(1, 0, 0),
      DesiredTorque = Vec3d.Zero,
    };

    var result = SolveNew(thrusters.ToArray(), input);

    double totalThrottle = 0;
    for (int i = 0; i < result.Length; i++) totalThrottle += result[i];

    // Dump all throttles for diagnosis.
    for (int i = 0; i < result.Length; i++) {
      if (result[i] > 1e-6)
        Console.WriteLine($"  t[{i}] = {result[i]:F6}  dir={thrusters[i].Direction}  pos={thrusters[i].Position}");
    }

    // Full input → all 8 forward nozzles at full throttle.
    // Non-aligned nozzles should be near-zero.
    for (int block = 0; block < 8; block++) {
      int fwdIdx = block * 4;
      Assert.AreEqual(1.0, result[fwdIdx], Tol, $"Block {block} forward nozzle should fire at full");
      Assert.IsTrue(result[fwdIdx + 1] < 0.01, $"Block {block} backward nozzle should be near-zero, got {result[fwdIdx + 1]}");
      Assert.IsTrue(result[fwdIdx + 2] < 0.01, $"Block {block} radial+ nozzle should be near-zero, got {result[fwdIdx + 2]}");
      Assert.IsTrue(result[fwdIdx + 3] < 0.01, $"Block {block} radial- nozzle should be near-zero, got {result[fwdIdx + 3]}");
    }
  }

  [TestMethod]
  public void ProportionalInput_ScalesThrottle() {
    var thrusters = new[] {
      MakeThruster(Vec3d.Zero, new Vec3d(1, 0, 0), 1),
    };
    // Half-magnitude input should produce half throttle.
    var input = new RcsSolver.Input {
      CoM = Vec3d.Zero,
      DesiredForce = new Vec3d(0.5, 0, 0),
      DesiredTorque = Vec3d.Zero,
    };
    var result = SolveNew(thrusters, input);
    Assert.AreEqual(0.5, result[0], Tol);
  }

  [TestMethod]
  public void ProportionalRotation_ScalesThrottle() {
    // Two thrusters producing +Z torque.
    var thrusters = new[] {
      MakeThruster(new Vec3d(0, 2, 0), new Vec3d(-1, 0, 0), 1),
      MakeThruster(new Vec3d(0, -2, 0), new Vec3d(1, 0, 0), 1),
    };
    var input = new RcsSolver.Input {
      CoM = Vec3d.Zero,
      DesiredForce = Vec3d.Zero,
      DesiredTorque = new Vec3d(0, 0, 0.3),
    };
    var result = SolveNew(thrusters, input);
    // 30% input → ~30% of full capability. Both fire equally.
    Assert.IsTrue(result[0] < 0.4, $"Expected scaled throttle, got {result[0]}");
    Assert.IsTrue(result[0] > 0.2, $"Expected nonzero throttle, got {result[0]}");
    Assert.AreEqual(result[0], result[1], Tol);
  }

  [TestMethod]
  public void EmptyThrusterArray() {
    var solver = new RcsSolver(0);
    solver.SetThrusters(new RcsSolver.Thruster[0]);
    var result = solver.Solve(new RcsSolver.Input {
      DesiredForce = new Vec3d(1, 0, 0),
    });
    Assert.AreEqual(0, result.Length);
  }

  // --- Reaction wheel (pure-torque) tests ---

  [TestMethod]
  public void PureTorque_ProducesNoForce() {
    // A pure-torque actuator should not fire for a force-only request.
    var thrusters = new[] {
      MakePureTorque(new Vec3d(5, 0, 0)),
      MakePureTorque(new Vec3d(-5, 0, 0)),
      MakePureTorque(new Vec3d(0, 5, 0)),
      MakePureTorque(new Vec3d(0, -5, 0)),
      MakePureTorque(new Vec3d(0, 0, 5)),
      MakePureTorque(new Vec3d(0, 0, -5)),
    };
    var input = new RcsSolver.Input {
      CoM = Vec3d.Zero,
      DesiredForce = new Vec3d(1, 0, 0),
      DesiredTorque = Vec3d.Zero,
    };
    var result = SolveNew(thrusters, input);
    for (int i = 0; i < result.Length; i++)
      Assert.AreEqual(0, result[i], Tol, $"Wheel slot {i} should not fire for translation");
  }

  [TestMethod]
  public void PureTorque_ProducesTorque() {
    // A pure-torque actuator should fire for a torque request in its axis.
    var thrusters = new[] {
      MakePureTorque(new Vec3d(5, 0, 0)),
      MakePureTorque(new Vec3d(-5, 0, 0)),
    };
    var input = new RcsSolver.Input {
      CoM = Vec3d.Zero,
      DesiredForce = Vec3d.Zero,
      DesiredTorque = new Vec3d(1, 0, 0), // pitch
    };
    var result = SolveNew(thrusters, input);
    Assert.IsTrue(result[0] > 0.1, $"+pitch wheel should fire, got {result[0]}");
    Assert.AreEqual(0, result[1], Tol, "-pitch wheel should not fire");
  }

  [TestMethod]
  public void WheelPreferredOverRcs() {
    // With both wheel + RCS available for torque, wheel should handle most of it
    // because it has much higher torque capacity.
    // Realistic scenario: wheel torque (15 kN·m) >> RCS torque (~2 kN·m per pair).
    var thrusters = new[] {
      // Two RCS thrusters producing +Z torque (with cancelling forces).
      MakeThruster(new Vec3d(0, 2, 0), new Vec3d(-1, 0, 0), 1),
      MakeThruster(new Vec3d(0, -2, 0), new Vec3d(1, 0, 0), 1),
      // Wheel with +Z/-Z torque — much stronger than RCS.
      MakePureTorque(new Vec3d(0, 0, 15)),
      MakePureTorque(new Vec3d(0, 0, -15)),
    };
    var input = new RcsSolver.Input {
      CoM = Vec3d.Zero,
      DesiredForce = Vec3d.Zero,
      DesiredTorque = new Vec3d(0, 0, 1),
    };
    var result = SolveNew(thrusters, input);
    // Wheel should provide the majority of torque.
    Assert.IsTrue(result[2] > 0.05, $"+Z wheel should fire, got {result[2]}");
    // RCS should contribute much less than the wheel.
    Assert.IsTrue(result[0] < result[2],
      $"RCS ({result[0]}) should contribute less than wheel ({result[2]})");
  }

  [TestMethod]
  public void WheelSaturation_RcsSupplements() {
    // Wheel capacity is small (torque=1), desired torque is large.
    // RCS should supplement.
    var thrusters = new[] {
      MakeThruster(new Vec3d(0, 2, 0), new Vec3d(-1, 0, 0), 1),
      MakeThruster(new Vec3d(0, -2, 0), new Vec3d(1, 0, 0), 1),
      MakePureTorque(new Vec3d(0, 0, 0.5)),
      MakePureTorque(new Vec3d(0, 0, -0.5)),
    };
    var input = new RcsSolver.Input {
      CoM = Vec3d.Zero,
      DesiredForce = Vec3d.Zero,
      DesiredTorque = new Vec3d(0, 0, 1),
    };
    var result = SolveNew(thrusters, input);
    // Both wheel and RCS should contribute.
    Assert.IsTrue(result[2] > 0.1, $"Wheel should be active, got {result[2]}");
    Assert.IsTrue(result[0] > 0.05, $"RCS should supplement, got {result[0]}");
  }

  [TestMethod]
  public void TranslationWithWheel_WheelIdle() {
    // Translation request with wheels present — wheels should stay off.
    var thrusters = new[] {
      MakeThruster(Vec3d.Zero, new Vec3d(1, 0, 0), 1),
      MakePureTorque(new Vec3d(5, 0, 0)),
      MakePureTorque(new Vec3d(-5, 0, 0)),
      MakePureTorque(new Vec3d(0, 5, 0)),
      MakePureTorque(new Vec3d(0, -5, 0)),
      MakePureTorque(new Vec3d(0, 0, 5)),
      MakePureTorque(new Vec3d(0, 0, -5)),
    };
    var input = new RcsSolver.Input {
      CoM = Vec3d.Zero,
      DesiredForce = new Vec3d(1, 0, 0),
      DesiredTorque = Vec3d.Zero,
    };
    var result = SolveNew(thrusters, input);
    Assert.AreEqual(1, result[0], Tol, "RCS thruster should fire at full");
    for (int i = 1; i < result.Length; i++)
      Assert.AreEqual(0, result[i], Tol, $"Wheel slot {i} should be idle");
  }

  private static RcsSolver.Thruster MakePureTorque(Vec3d torque) {
    return new RcsSolver.Thruster { Torque = torque };
  }

  private static double[] SolveNew(RcsSolver.Thruster[] thrusters, RcsSolver.Input input) {
    var solver = new RcsSolver(thrusters.Length);
    solver.SetThrusters(thrusters);
    return solver.Solve(input);
  }

  private static RcsSolver.Thruster MakeThruster(Vec3d position, Vec3d direction, double maxPower) {
    return new RcsSolver.Thruster {
      Position = position,
      Direction = direction,
      MaxPower = maxPower,
    };
  }
}
