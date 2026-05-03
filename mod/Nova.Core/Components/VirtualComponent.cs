using System;
using Nova.Core.Persistence.Protos;
using Nova.Core.Resources;
using Nova.Core.Systems;

namespace Nova.Core.Components;

public class VirtualComponent {

  public string Name { get; private set; }

  // Back-reference to the host vessel. Set by VirtualVessel.AddPart
  // and refreshed during WalkPartTree (so Clone() ends up pointing at
  // the clone). Components that need cross-vessel access (e.g. the
  // Thermometer walking storages) read this directly.
  public VirtualVessel Vessel { get; internal set; }

  // Wake-time for event-driven components (slice rollover, scheduled
  // emissions, etc). VirtualVessel.Tick advances simulationTime to the
  // earliest ValidUntil across all devices and components, then calls
  // Update(nowUT) on every component whose ValidUntil has elapsed. The
  // component's Update MUST advance ValidUntil before returning (or set
  // it to PositiveInfinity to stop scheduling) — otherwise the loop will
  // re-fire it indefinitely. Default = never wake me; mirrors the
  // Device.ValidUntil pattern already used by the LP solver.
  public double ValidUntil { get; set; } = double.PositiveInfinity;

  public VirtualComponent() {
    Name = GetType().Name;
  }

  public virtual VirtualComponent Clone() {
    return (VirtualComponent) MemberwiseClone();
  }

  public virtual void SaveStructure(PartStructure ps) {}
  public virtual void LoadStructure(PartStructure ps) {}
  public virtual void Save(PartState state) {}
  public virtual void Load(PartState state) {}

  // Register this component's per-tick footprint with the vessel-level
  // systems. Called once at vessel build time. `node` is the Staging
  // node this component lives on (parts under the same decoupler share
  // a node). Topological-resource components (Engine, Rcs, FuelCell
  // refill, TankVolume) wire into `systems.Staging` via the node;
  // Uniform-resource components (Light, Battery, Solar, Command,
  // Thermometer, FuelCell production, ReactionWheel refill) wire into
  // `systems.Process`.
  public virtual void OnBuildSystems(VesselSystems systems, StagingFlowSystem.Node node) {}

  public virtual void OnPreSolve() {}

  // Tick-start hook: refresh forecasts derived from externally-mutated
  // state (e.g. ReactionWheel throttles set by SolveAttitude between
  // ticks, which don't invalidate the LP but do invalidate the buffer-
  // empty forecast). Called once at the top of VirtualVessel.Tick.
  public virtual void OnTickBegin() {}

  // Post-solve hook: distribute solved Activities into component-internal
  // state (per-panel CurrentRate, per-cell ValidUntil, per-wheel
  // refill.ValidUntil, etc.). Called by VirtualVessel.DoSolve after
  // systems.Solve().
  public virtual void OnPostSolve() {}

  // Clock-advance hook: integrate component-internal accumulators using
  // the most recent solve's Activities over the elapsed `dt`. Called by
  // VirtualVessel.Tick on each non-zero clock advance. Buffer-based
  // resources (the system-owned LP/staging Buffers) lerp themselves
  // against the shared SimClock and don't need this — only off-system
  // accumulators (FuelCell.Manifold, ReactionWheel.Buffer) do.
  public virtual void OnAdvance(double dt) {}

  public virtual void Update(double nowUT) {}

  public static bool Is(Type type) {
    return type.BaseType != null && (type.BaseType == typeof(VirtualComponent) || Is(type.BaseType));
  }
}
