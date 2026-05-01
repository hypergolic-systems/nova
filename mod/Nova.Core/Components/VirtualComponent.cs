using System;
using Nova.Core.Persistence.Protos;
using Nova.Core.Resources;

namespace Nova.Core.Components;

public class VirtualComponent {

  public string Name { get; private set; }

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

  public virtual void OnBuildSolver(ResourceSolver solver, ResourceSolver.Node node) {}

  public virtual void OnPreSolve() {}

  public virtual void Update(double nowUT) {}

  public static bool Is(Type type) {
    return type.BaseType != null && (type.BaseType == typeof(VirtualComponent) || Is(type.BaseType));
  }
}
