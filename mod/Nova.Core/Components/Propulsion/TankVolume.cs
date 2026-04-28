using System.Collections.Generic;
using System.Linq;
using Nova.Core.Persistence;
using Nova.Core.Persistence.Protos;
using Nova.Core.Resources;
using Nova.Core.Flight;
using Nova.Core.Utils;

namespace Nova.Core.Components.Propulsion;

public class TankVolume : VirtualComponent {
  public double Volume;
  public List<Buffer> Tanks = new();

  public TankVolume() {}

  public TankVolume(TankVolumeStructure structure) {
    Volume = structure.Volume;
    foreach (var t in structure.Tanks) {
      Tanks.Add(new Buffer {
        Resource = Resource.Get(t.Resource),
        Capacity = t.Capacity,
        Contents = t.Capacity, // default: full
        MaxRateOut = t.MaxRateOut > 0 ? t.MaxRateOut : double.PositiveInfinity,
        MaxRateIn = t.MaxRateIn > 0 ? t.MaxRateIn : double.PositiveInfinity,
      });
    }
  }

  public override void LoadStructure(PartStructure ps) {
    if (ps.TankVolume == null) return;
    var s = ps.TankVolume;
    Volume = s.Volume;
    Tanks.Clear();
    foreach (var t in s.Tanks) {
      Tanks.Add(new Buffer {
        Resource = Resource.Get(t.Resource),
        Capacity = t.Capacity,
        Contents = t.Capacity,
        MaxRateOut = t.MaxRateOut > 0 ? t.MaxRateOut : double.PositiveInfinity,
        MaxRateIn = t.MaxRateIn > 0 ? t.MaxRateIn : double.PositiveInfinity,
      });
    }
  }

  public override void SaveStructure(PartStructure ps) {
    var s = new TankVolumeStructure { Volume = Volume };
    s.Tanks.AddRange(Tanks.Select(t => new TankStructure {
      Resource = t.Resource.Name,
      Capacity = t.Capacity,
      MaxRateOut = t.MaxRateOut,
      MaxRateIn = t.MaxRateIn,
    }));
    ps.TankVolume = s;
  }

  public override void Save(PartState state) {
    state.TankVolume = new TankVolumeState {
      Amounts = Tanks.Select(t => t.Contents).ToArray(),
    };
  }

  public override void Load(PartState state) {
    if (state.TankVolume == null) return;
    for (int i = 0; i < state.TankVolume.Amounts.Length && i < Tanks.Count; i++)
      Tanks[i].Contents = state.TankVolume.Amounts[i];
  }

  public override VirtualComponent Clone() {
    var clone = new TankVolume { Volume = Volume };
    clone.Tanks = Tanks.Select(t => new Buffer {
      Resource = t.Resource,
      Capacity = t.Capacity,
      Contents = t.Contents,
      MaxRateOut = t.MaxRateOut,
      MaxRateIn = t.MaxRateIn,
    }).ToList();
    return clone;
  }

  // Replace Tanks in place. The list reference stays the same so any
  // cached references (NovaTankModule.tankVolume, NovaPartModule.Components)
  // see the new shape without re-plumbing. Used by the editor "Set Tank
  // Config" path; not safe to call mid-flight (the solver topology is
  // built from the buffer list at vessel-modify time).
  public void Reconfigure(List<Buffer> newTanks) {
    Tanks.Clear();
    foreach (var t in newTanks) Tanks.Add(t);
  }

  public override void OnBuildSolver(ResourceSolver solver, ResourceSolver.Node node) {
    for (int i = 0; i < Tanks.Count; i++) {
      var tank = Tanks[i];
      var buf = node.AddBuffer(tank.Resource, tank.Capacity);
      buf.FlowLimits(tank.MaxRateIn, tank.MaxRateOut);
      buf.Contents = tank.Contents;
      Tanks[i] = buf;
    }
  }
}
