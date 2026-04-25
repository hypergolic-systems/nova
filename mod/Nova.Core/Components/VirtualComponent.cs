using System;
using Nova.Core.Persistence.Protos;
using Nova.Core.Resources;

namespace Nova.Core.Components;

public class VirtualComponent {

  public string Name { get; private set; }


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

  public static bool Is(Type type) {
    return type.BaseType != null && (type.BaseType == typeof(VirtualComponent) || Is(type.BaseType));
  }
}
