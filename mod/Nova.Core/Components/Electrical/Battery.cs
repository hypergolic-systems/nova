using Nova.Core.Persistence;
using Nova.Core.Persistence.Protos;
using Nova.Core.Resources;
using Nova.Core.Systems;

namespace Nova.Core.Components.Electrical;

public class Battery : VirtualComponent {
  public Buffer Buffer = new() {
    Resource = Resource.ElectricCharge,
    Capacity = 0,
    Contents = 0,
    MaxRateIn = 10,
    MaxRateOut = 10,
  };

  public Battery() {}

  public Battery(BatteryStructure structure) {
    Buffer.Capacity = structure.Capacity;
    Buffer.Contents = structure.Capacity; // default: full
  }

  public override void LoadStructure(PartStructure ps) {
    if (ps.Battery == null) return;
    Buffer.Capacity = ps.Battery.Capacity;
    Buffer.Contents = ps.Battery.Capacity;
  }

  public override void SaveStructure(PartStructure ps) {
    ps.Battery = new BatteryStructure { Capacity = Buffer.Capacity };
  }

  public override void Save(PartState state) {
    state.Battery = new BatteryState { Value = Buffer.Contents };
  }

  public override void Load(PartState state) {
    if (state.Battery == null) return;
    Buffer.Contents = state.Battery.Value;
  }

  public override VirtualComponent Clone() {
    return new Battery {
      Buffer = new Buffer {
        Resource = Buffer.Resource,
        Capacity = Buffer.Capacity,
        Contents = Buffer.Contents,
        MaxRateIn = Buffer.MaxRateIn,
        MaxRateOut = Buffer.MaxRateOut,
      },
    };
  }

  public override void OnBuildSystems(VesselSystems systems, StagingFlowSystem.Node node) {
    systems.Process.AddBuffer(Buffer);
  }
}
