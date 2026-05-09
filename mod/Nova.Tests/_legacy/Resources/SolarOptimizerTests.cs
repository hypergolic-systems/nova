using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nova.Core.Resources;
using Nova.Core.Flight;
using Nova.Core.Utils;
namespace Nova.Tests.Resources;

[TestClass]
public class SolarOptimizerTests {

  [TestMethod]
  public void NoPanels_ReturnsZero() {
    var result = SolarOptimizer.ComputeOptimalRate(new List<SolarOptimizer.Panel>());
    Assert.AreEqual(0, result);
  }

  [TestMethod]
  public void SingleFixedPanel_ReturnsChargeRate() {
    // A single fixed panel pointing up. Optimal sun direction = panel normal.
    var panels = new List<SolarOptimizer.Panel> {
      new() { Direction = new Vec3d(0, 1, 0), ChargeRate = 10, IsTracking = false }
    };

    var result = SolarOptimizer.ComputeOptimalRate(panels);
    Assert.AreEqual(10, result, 0.2);
  }

  [TestMethod]
  public void TwoOpposedFixedPanels_ReturnsOneChargeRate() {
    // Two panels facing opposite directions — can only illuminate one at a time.
    var panels = new List<SolarOptimizer.Panel> {
      new() { Direction = new Vec3d(0, 1, 0), ChargeRate = 10, IsTracking = false },
      new() { Direction = new Vec3d(0, -1, 0), ChargeRate = 10, IsTracking = false },
    };

    var result = SolarOptimizer.ComputeOptimalRate(panels);
    Assert.AreEqual(10, result, 0.2);
  }

  [TestMethod]
  public void TwoPerpendicularFixedPanels_OptimalAt45Degrees() {
    // Two panels at 90 degrees. Optimal sun at 45 degrees between them.
    // Each gets cos(45) = 1/sqrt(2) ≈ 0.707. Total = 2 * 10 * 0.707 ≈ 14.14
    var panels = new List<SolarOptimizer.Panel> {
      new() { Direction = new Vec3d(0, 1, 0), ChargeRate = 10, IsTracking = false },
      new() { Direction = new Vec3d(1, 0, 0), ChargeRate = 10, IsTracking = false },
    };

    var result = SolarOptimizer.ComputeOptimalRate(panels);
    Assert.AreEqual(14.14, result, 0.5);
  }

  [TestMethod]
  public void SingleTrackingPanel_ReturnsChargeRate() {
    // A tracking panel with vertical axis. Best when sun is perpendicular to axis.
    // sqrt(1 - 0^2) = 1.0 → full chargeRate.
    var panels = new List<SolarOptimizer.Panel> {
      new() { Direction = new Vec3d(0, 1, 0), ChargeRate = 20, IsTracking = true }
    };

    var result = SolarOptimizer.ComputeOptimalRate(panels);
    Assert.AreEqual(20, result, 0.5);
  }

  [TestMethod]
  public void TrackingAndFixedPanel_CombinedOptimization() {
    // Tracking panel with Y axis + fixed panel with X normal.
    // Tracking wants sun perpendicular to Y (in XZ plane) — contribution = 20.
    // Fixed wants sun along X — contribution = 10 * dot(d, X).
    // Best: sun along X (perpendicular to Y axis): tracking=20, fixed=10. Total=30.
    var panels = new List<SolarOptimizer.Panel> {
      new() { Direction = new Vec3d(0, 1, 0), ChargeRate = 20, IsTracking = true },
      new() { Direction = new Vec3d(1, 0, 0), ChargeRate = 10, IsTracking = false },
    };

    var result = SolarOptimizer.ComputeOptimalRate(panels);
    Assert.AreEqual(30, result, 0.5);
  }

  [TestMethod]
  public void ProportionalDistribution() {
    // Verify VirtualVessel.ComputeSolarRates proportioning logic:
    // 2x 20W panels + 1x 50W panel, chargeRates sum to 90.
    // If optimal rate is 80W: 20W panels get 80*(20/90)≈17.78, 50W gets 80*(50/90)≈44.44
    // This tests the optimizer finds the right vessel-wide rate;
    // proportioning is handled by VirtualVessel, not the optimizer.
    var panels = new List<SolarOptimizer.Panel> {
      new() { Direction = new Vec3d(0, 1, 0), ChargeRate = 20, IsTracking = false },
      new() { Direction = new Vec3d(0, 1, 0), ChargeRate = 20, IsTracking = false },
      new() { Direction = new Vec3d(0, 1, 0), ChargeRate = 50, IsTracking = false },
    };

    // All same direction: sun aligns with normal, each gets full chargeRate.
    var result = SolarOptimizer.ComputeOptimalRate(panels);
    Assert.AreEqual(90, result, 1.0);
  }

  [TestMethod]
  public void FibonacciSphereDirections_AreUnitVectors() {
    for (int i = 0; i < 200; i++) {
      var d = SolarOptimizer.FibonacciSphereDirection(i, 200);
      Assert.AreEqual(1.0, d.Magnitude, 1e-10, $"Direction {i} is not unit length");
    }
  }
}
