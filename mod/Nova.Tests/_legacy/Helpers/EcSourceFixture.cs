using Nova.Core.Components;
using Nova.Core.Resources;
using Nova.Core.Systems;
using Buffer = Nova.Core.Resources.Buffer;

namespace Nova.Tests.Helpers;

/// <summary>
/// Test-only EC source. Registers an unconstrained-flow EC Buffer
/// on the Process system at OnBuildSystems. Substitute for the
/// deleted C# Battery (now Rust-owned) — fixtures that need an EC
/// reservoir to drive a still-C# component (ReactionWheel,
/// Thermometer, etc.) use this.
/// </summary>
public class EcSourceFixture : VirtualComponent {
  public double Capacity = 1e6;
  public double Contents = 5e5;
  public double MaxFlow = 1e6;

  public override VirtualComponent Clone() => new EcSourceFixture {
    Capacity = Capacity, Contents = Contents, MaxFlow = MaxFlow,
  };

  public override void OnBuildSystems(VesselSystems systems, StagingFlowSystem.Node node) {
    systems.Process.AddBuffer(new Buffer {
      Resource = Resource.ElectricCharge,
      Capacity = Capacity,
      Contents = Contents,
      MaxRateIn = MaxFlow,
      MaxRateOut = MaxFlow,
    });
  }
}
