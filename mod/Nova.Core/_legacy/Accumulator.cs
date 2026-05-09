using System;
using Nova.Core.Resources;
using Nova.Core.Systems;

namespace Nova.Core.Components;

// Off-LP storage cell with lerp-based state and an owned refill device.
// Mirrors Resource.Buffer's lerp model, but for component-internal
// reservoirs that don't participate in solver conservation rows:
//
//   • FuelCell.Manifold       — LH₂ + LOx mix (Staging-refilled).
//   • ReactionWheel.Buffer    — energy reserve (Process-refilled).
//
// The Accumulator is self-contained: it holds an abstract quantity
// (Capacity + lerp state), owns a unified refill Device with
// hysteresis on/off control (the Device routes to whichever solver
// matches the inputs' domain), and exposes a single TapRate setter
// for the drain side. Owners stop carrying boilerplate around device
// construction, hysteresis flips, or forecast math.
//
// Lerp:
//   Contents(t) = clamp(BaselineContents + Rate × (t - BaselineUT), 0, Capacity)
//   Rate        = RefillActivity · RefillRate − TapRate    (signed; + = filling)
//
// Hysteresis: refill flips ON when FillFraction ≤ RefillOnFraction
// (default 10%), OFF when FillFraction ≥ RefillOffFraction (default
// 100%). The Accumulator pushes the resulting on/off into the
// refill Device's Demand each OnPreSolve.
//
// ValidUntil is the absolute UT of the next forecasted hysteresis flip;
// the owning component bubbles it up via cmp.ValidUntil so the Tick
// scheduler can step to the event.
//
// See `mod/Nova.Core/Resources/Buffer.cs` for the underlying lerp
// design rationale.
public class Accumulator {
  public double Capacity;

  // Baseline state. Direct field access for owners (snapshot/restore
  // code paths). Most callers should go through the property surface.
  public double BaselineContents;
  public double BaselineUT;

  // Shared clock — installed by Configure(). Tests can leave this
  // null; ContentsAt then collapses to a static-value lookup at
  // BaselineUT.
  internal SimClock Clock;

  // ── Refill side ──────────────────────────────────────────────────
  // The Accumulator owns a single unified Device for the refill side;
  // VesselSystems.AddDevice routes it to Staging or Process based on
  // the inputs' domain. The Accumulator pushes hysteresis-driven
  // activation into Demand on OnPreSolve and reads the achieved
  // Activity back on OnPostSolve.
  internal Device refillDevice;
  // Capacity-units per second when refill activity = 1.
  public double RefillRate;
  // Last-solve achieved fraction (0..1). Updated in OnPostSolve.
  public double RefillActivity;

  // ── Hysteresis ───────────────────────────────────────────────────
  public double RefillOnFraction  = 0.10;
  public double RefillOffFraction = 1.00;
  public bool   RefillActive;

  // Absolute UT of the next forecasted hysteresis flip. +∞ when no
  // flip is reachable from the current state. Owner exposes this via
  // cmp.ValidUntil so VirtualVessel.ComputeNextExpiry sees it.
  public double ValidUntil { get; private set; } = double.PositiveInfinity;

  // ── Drain side ───────────────────────────────────────────────────
  // Continuous drain rate (capacity-units/sec). Setter rebaselines
  // the lerp at "now" and recomputes net Rate. Discrete-tap semantics
  // are recoverable: piecewise-constant TapRate updates over a known
  // dt are equivalent to Tap(rate × dt).
  private double _tapRate;
  public double TapRate {
    get => _tapRate;
    set {
      RebaselineNow();
      _tapRate = value;
      RecomputeRate();
      RefreshValidUntil();
    }
  }

  // ── Lerp state ───────────────────────────────────────────────────
  // Net signed rate (+ = filling). Updated whenever RefillActivity or
  // TapRate changes. Read-only externally — use ConfigureRefill / the
  // OnPostSolve hook to drive the refill side and TapRate setter to
  // drive the drain side.
  public double Rate { get; private set; }

  // Current Contents, lerped to the shared clock's UT and clamped to
  // [0, Capacity]. Setter rebaselines.
  public double Contents {
    get => ContentsAt(Clock?.UT ?? BaselineUT);
    set {
      BaselineContents = value;
      BaselineUT = Clock?.UT ?? BaselineUT;
    }
  }

  public double ContentsAt(double ut) {
    var projected = BaselineContents + Rate * (ut - BaselineUT);
    if (projected < 0) return 0;
    if (projected > Capacity) return Capacity;
    return projected;
  }

  public void Refresh(double ut) {
    BaselineContents = ContentsAt(ut);
    BaselineUT = ut;
  }

  public double FillFraction => Capacity > 1e-9 ? Contents / Capacity : 1.0;
  public bool IsEmpty => Contents <= 1e-9;
  public bool IsFull  => Contents >= Capacity - 1e-9;

  // Time for Contents to reach `targetFrac × Capacity` given a signed
  // netRate. +∞ when the rate doesn't move toward the target.
  //
  // Edge case: when Contents is already AT the target and netRate
  // keeps pushing past, we return 0 (not +∞) — the clamp absorbs the
  // over-fill silently, so the 0 forecast forces an immediate re-solve
  // so OnPreSolve can flip the hysteresis flag.
  public double TimeToFraction(double targetFrac, double netRate) {
    if (Capacity <= 0) return double.PositiveInfinity;
    double target = targetFrac * Capacity;
    double slack = target - Contents;
    if (slack > 0 && netRate > 1e-12) return slack / netRate;
    if (slack < 0 && netRate < -1e-12) return slack / netRate;
    if (Math.Abs(slack) < 1e-12 && Math.Abs(netRate) > 1e-12) return 0;
    return double.PositiveInfinity;
  }

  // ── Configure ────────────────────────────────────────────────────
  // Wire the refill side. The inputs' resource domain picks Staging
  // (Topological — coupled multi-input water-fill) or Process
  // (Uniform — LP) automatically via VesselSystems.AddDevice.
  // RefillRate = sum of per-input rates — the lerp uses the total.
  // Installs the SimClock and rebaselines BaselineUT to "now" so the
  // lerp anchors against the live clock from here on.
  public void Configure(VesselSystems systems,
      StagingFlowSystem.Node node,
      params (Resource resource, double rate)[] inputs) {
    refillDevice = systems.AddDevice(node, inputs: inputs);
    double total = 0;
    foreach (var (_, rate) in inputs) total += rate;
    RefillRate = total;
    refillDevice.Demand = RefillActive ? 1.0 : 0.0;
    InstallClock(systems.Clock);
  }

  // ── Lifecycle ────────────────────────────────────────────────────
  // Pre-solve: hysteresis flip on FillFraction, push the resulting
  // active state into the underlying refill device's Demand for the
  // upcoming solve.
  public void OnPreSolve() {
    double frac = FillFraction;
    if (RefillActive && frac >= RefillOffFraction) RefillActive = false;
    else if (!RefillActive && frac <= RefillOnFraction) RefillActive = true;
    if (refillDevice != null)
      refillDevice.Demand = RefillActive ? 1.0 : 0.0;
  }

  // Post-solve: capture the refill device's achieved Activity,
  // recompute net Rate (rebaselining at "now"), and forecast the next
  // hysteresis flip.
  public void OnPostSolve() {
    RefillActivity = refillDevice?.Activity ?? 0;
    RebaselineNow();
    RecomputeRate();
    RefreshValidUntil();
  }

  // ── Internals ────────────────────────────────────────────────────
  private void InstallClock(SimClock clock) {
    Clock = clock;
    BaselineUT = clock.UT;
  }

  private void RebaselineNow() {
    var t = Clock?.UT ?? BaselineUT;
    BaselineContents = ContentsAt(t);
    BaselineUT = t;
  }

  private void RecomputeRate() {
    Rate = RefillActivity * RefillRate - _tapRate;
  }

  private void RefreshValidUntil() {
    double dt = RefillActive
      ? TimeToFraction(RefillOffFraction, Rate)
      : TimeToFraction(RefillOnFraction,  Rate);
    ValidUntil = double.IsPositiveInfinity(dt)
      ? double.PositiveInfinity
      : (Clock?.UT ?? BaselineUT) + dt;
  }
}
