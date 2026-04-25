using System;
using Nova.Core.Utils;

namespace Nova.Core.Flight;

/// <summary>
/// RCS thruster solver using projected gradient descent on a box-constrained QP.
///
/// Minimizes weighted force + torque error with fuel/regularization penalties:
///   minimize  w_f ‖B_f·t - F_des‖² + w_τ ‖B̃_τ·t - τ̃_des‖² + ε_fuel·Σtᵢ + ε_reg·Σtᵢ²
///   subject to  tᵢ ∈ [0, 1]
///
/// Expanded: minimize tᵀQt + cᵀt where Q is precomputed from thruster geometry
/// and c is recomputed each frame from the desired force/torque.
///
/// Torque coefficients are normalized by L = max lever arm magnitude to keep
/// force and torque at the same scale.
///
/// Fixed weights (w_f >> w_τ) mean force accuracy wins conflicts.
/// When F_des = 0, the force error term becomes "minimize stray force."
/// </summary>
public class RcsSolver {

  public struct Thruster {
    public Vec3d Position;   // vessel-local space
    public Vec3d Direction;  // unit thrust direction, vessel-local
    public double MaxPower;  // kN
    public Vec3d Torque;     // direct torque for pure-torque actuators (reaction wheels)
  }

  public struct Input {
    public Vec3d CoM;            // vessel-local space
    public Vec3d DesiredForce;   // vessel-local, magnitude = intensity
    public Vec3d DesiredTorque;  // vessel-local, magnitude = intensity
  }

  public int ThrusterCount => n;

  private readonly int n;
  private readonly Vec3d[] forces;       // constant: MaxPower * Direction
  private readonly Vec3d[] positions;    // constant: thruster positions
  private readonly Vec3d[] directTorques; // non-zero for pure-torque actuators
  private bool[] isPureTorque;           // true = reaction wheel (no force, no fuel)
  private bool hasPureTorque;            // true if any pure-torque actuator present
  private int logCounter;
  public Action<string> Log;

  // Cached QP state (rebuilt when CoM moves beyond threshold).
  private double[] cachedQ;              // n×n matrix, row-major
  private Vec3d[] normTorques;           // normalized torque vectors
  private Vec3d cachedCoM;
  private double stepSize;               // 1 / λ_max(Q)
  private double cachedL;                // normalization factor

  // Warm-start: previous frame's solution and input magnitude.
  private double[] warmT;
  private double prevInputMag;

  // Per-frame scratch arrays (cached to avoid GC pressure).
  private double[] scratchC;
  private double[] scratchT;
  private double[] scratchTPrev;
  private double[] scratchY;
  private double[] scratchGrad;

  // Priority weights: force >> torque (Mode 1 — max strength).
  private const double WForce = 100.0;
  private const double WTorque = 1.0;

  // Regularization.
  private const double EpsFuel = 1e-3;   // linear fuel penalty (base)
  private const double EpsFuelWheelBoost = 1.0; // extra penalty when wheels present
  private const double EpsReg = 1e-4;    // quadratic: spreads load, guarantees PSD

  private const double CoMThreshold = 0.1; // rebuild Q when CoM moves this far
  private const int MaxIterations = 100;

  public RcsSolver(int thrusterCount) {
    n = thrusterCount;
    forces = new Vec3d[n];
    positions = new Vec3d[n];
    directTorques = new Vec3d[n];
    isPureTorque = new bool[n];
  }

  /// <summary>
  /// Set thruster geometry (vessel-local space). Call once at construction,
  /// or when the thruster layout changes. Invalidates cached Q.
  /// </summary>
  public void SetThrusters(Thruster[] thrusters) {
    for (int i = 0; i < n; i++) {
      positions[i] = thrusters[i].Position;
      forces[i] = thrusters[i].MaxPower * thrusters[i].Direction;
      directTorques[i] = thrusters[i].Torque;
      isPureTorque[i] = thrusters[i].MaxPower == 0 && thrusters[i].Torque.SqrMagnitude > 1e-20;
    }
    hasPureTorque = false;
    for (int i = 0; i < n; i++)
      if (isPureTorque[i]) { hasPureTorque = true; break; }
    cachedQ = null;
    scratchC = null;
    warmT = null;
  }

  /// <summary>
  /// Solve for per-thruster throttle values via projected gradient descent.
  /// </summary>
  public double[] Solve(Input input) {
    var result = new double[n];
    if (n == 0) return result;

    bool hasForce = input.DesiredForce.SqrMagnitude > 1e-20;
    bool hasTorque = input.DesiredTorque.SqrMagnitude > 1e-20;
    if (!hasForce && !hasTorque) {
      warmT = null; // reset warm-start when idle
      return result;
    }

    // Rebuild Q if needed (CoM shifted or first call).
    if (cachedQ == null || (input.CoM - cachedCoM).SqrMagnitude > CoMThreshold * CoMThreshold)
      BuildQ(input.CoM);

    // Scale desired force/torque by vessel's max capability in that direction.
    // Input magnitude 0-1 maps to 0%-100% of achievable output.
    // Only count thrusters within 60° of the desired direction (cos 60° = 0.5)
    // to avoid inflating the target with weakly-aligned thrusters.
    var fDes = Vec3d.Zero;
    if (hasForce) {
      var fHat = input.DesiredForce.Normalized;
      double forceMag = Math.Min(1.0, input.DesiredForce.Magnitude);
      double maxForceProj = 0;
      for (int i = 0; i < n; i++) {
        if (isPureTorque[i]) continue; // wheels can't produce force
        double proj = Vec3d.Dot(forces[i], fHat);
        if (proj > 0.5 * forces[i].Magnitude)
          maxForceProj += proj;
      }
      fDes = fHat * (forceMag * maxForceProj);
    }

    var tDes = Vec3d.Zero;
    if (hasTorque) {
      var tHat = input.DesiredTorque.Normalized;
      double torqueMag = Math.Min(1.0, input.DesiredTorque.Magnitude);
      double maxTorqueProj = 0;
      for (int i = 0; i < n; i++) {
        double proj = Vec3d.Dot(normTorques[i], tHat);
        if (proj > 0.5 * normTorques[i].Magnitude)
          maxTorqueProj += proj;
      }
      tDes = tHat * (torqueMag * maxTorqueProj);
    }

    // Use cached scratch arrays to avoid per-frame allocations.
    EnsureScratchArrays();
    var c = scratchC;
    var t = scratchT;
    var tPrev = scratchTPrev;
    var y = scratchY;
    var grad = scratchGrad;

    double fuelPenalty = hasPureTorque ? EpsFuelWheelBoost : EpsFuel;
    for (int i = 0; i < n; i++) {
      c[i] = -2.0 * (WForce * Vec3d.Dot(forces[i], fDes)
                    + WTorque * Vec3d.Dot(normTorques[i], tDes));
      if (!isPureTorque[i]) c[i] += fuelPenalty; // no fuel cost for reaction wheels
    }

    // Warm-start from previous frame, scaled by input magnitude ratio.
    // This prevents stale high-throttle solutions from persisting when
    // input drops (e.g. SAS near target), while staying smooth during
    // gradual changes (no hard reset → no jitter).
    double inputMag = fDes.Magnitude + tDes.Magnitude;
    if (warmT == null || prevInputMag < 1e-12) {
      Array.Clear(t, 0, n);
    } else {
      double scale = Math.Min(1.0, inputMag / prevInputMag);
      for (int i = 0; i < n; i++)
        t[i] = warmT[i] * scale;
    }
    Array.Copy(t, tPrev, n);

    // FISTA (accelerated projected gradient descent).
    // Converges in O(√κ) iterations vs O(κ) for plain GD.
    Array.Copy(t, y, n);
    double omega = 1;
    int iters = MaxIterations;
    for (int iter = 0; iter < MaxIterations; iter++) {
      // gradient = 2*Q*y + c
      for (int i = 0; i < n; i++) {
        double g = c[i];
        int row = i * n;
        for (int k = 0; k < n; k++)
          g += 2.0 * cachedQ[row + k] * y[k];
        grad[i] = g;
      }

      // Projected step: t_next = clamp(y - α*grad, 0, 1)
      double maxDelta = 0;
      for (int i = 0; i < n; i++) {
        double prev = t[i];
        double next = y[i] - stepSize * grad[i];
        if (next < 0) next = 0;
        else if (next > 1) next = 1;
        tPrev[i] = prev;
        t[i] = next;
        double delta = Math.Abs(next - prev);
        if (delta > maxDelta) maxDelta = delta;
      }

      // Converge on projected gradient norm, not step size.
      // A thruster at a bound with gradient pushing into the bound is converged.
      double maxProjGrad = 0;
      for (int i = 0; i < n; i++) {
        double g = grad[i];
        // Skip if at bound and gradient pushes into bound.
        if (t[i] <= 0 && g > 0) continue;
        if (t[i] >= 1 && g < 0) continue;
        if (Math.Abs(g) > maxProjGrad) maxProjGrad = Math.Abs(g);
      }
      if (maxProjGrad < 0.1) { iters = iter + 1; break; }

      // Momentum update.
      double omegaNext = (1 + Math.Sqrt(1 + 4 * omega * omega)) / 2;
      double beta = (omega - 1) / omegaNext;
      for (int i = 0; i < n; i++)
        y[i] = t[i] + beta * (t[i] - tPrev[i]);
      omega = omegaNext;
      iters = iter + 1;
    }

    // Store for warm-start.
    if (warmT == null)
      warmT = new double[n];
    Array.Copy(t, warmT, n);
    prevInputMag = inputMag;
    Array.Copy(t, result, n);

    // Compute residual: actual force/torque vs desired.
    var netForce = Vec3d.Zero;
    var netTorque = Vec3d.Zero;
    for (int i = 0; i < n; i++) {
      netForce = netForce + result[i] * forces[i];
      netTorque = netTorque + result[i] * normTorques[i];
    }
    var forceErr = (netForce - fDes).Magnitude;
    var torqueErr = (netTorque - tDes).Magnitude;

    if (logCounter++ % 50 == 0)
      Log?.Invoke($"[RCS] QP: iters={iters} F_des={fDes} τ_des={input.DesiredTorque} " +
                 $"F_err={forceErr:E2} τ_err={torqueErr:E2} " +
                 $"throttles=[{FormatThrottles(result)}]");

    return result;
  }

  private void EnsureScratchArrays() {
    if (scratchC != null) return;
    scratchC = new double[n];
    scratchT = new double[n];
    scratchTPrev = new double[n];
    scratchY = new double[n];
    scratchGrad = new double[n];
  }

  /// <summary>
  /// Build the Q matrix and step size from thruster geometry and CoM.
  /// Q_ik = w_f·Dot(f_i,f_k) + w_τ·Dot(τ̃_i,τ̃_k) + δ_ik·ε_reg
  /// </summary>
  private void BuildQ(Vec3d com) {
    // Compute torques and normalize by max lever arm.
    normTorques = new Vec3d[n];
    double maxLever = 0;
    for (int i = 0; i < n; i++) {
      if (isPureTorque[i]) continue; // pure-torque has no position
      double leverMag = (positions[i] - com).Magnitude;
      if (leverMag > maxLever) maxLever = leverMag;
    }
    for (int i = 0; i < n; i++) {
      Vec3d torque;
      if (isPureTorque[i])
        torque = directTorques[i];
      else
        torque = Vec3d.Cross(positions[i] - com, forces[i]);
      normTorques[i] = maxLever > 1e-12 ? torque * (1.0 / maxLever) : torque;
    }

    // Build Q matrix (symmetric, stored full for simple indexing).
    cachedQ = new double[n * n];
    for (int i = 0; i < n; i++) {
      for (int k = 0; k < n; k++) {
        double q = WForce * Vec3d.Dot(forces[i], forces[k])
                 + WTorque * Vec3d.Dot(normTorques[i], normTorques[k]);
        if (i == k) q += EpsReg;
        cachedQ[i * n + k] = q;
      }
    }

    // Step size via Gershgorin bound: λ_max ≤ max_i(Σ_k |Q_ik|)
    // We use 1/(2·λ_max) since gradient = 2Qt + c and we want step for the 2Q term.
    double maxRowSum = 0;
    for (int i = 0; i < n; i++) {
      double rowSum = 0;
      int row = i * n;
      for (int k = 0; k < n; k++)
        rowSum += Math.Abs(cachedQ[row + k]);
      if (rowSum > maxRowSum) maxRowSum = rowSum;
    }
    stepSize = maxRowSum > 1e-12 ? 1.0 / (2.0 * maxRowSum) : 1.0;

    cachedCoM = com;
    cachedL = maxLever;

    Log?.Invoke($"[RCS] BuildQ: n={n} L={maxLever:F4} stepSize={stepSize:E4} " +
               $"maxRowSum={maxRowSum:F4}");
  }

  private static string FormatThrottles(double[] arr) {
    var parts = new string[arr.Length];
    for (int i = 0; i < arr.Length; i++)
      parts[i] = arr[i].ToString("F4");
    return string.Join(", ", parts);
  }
}
