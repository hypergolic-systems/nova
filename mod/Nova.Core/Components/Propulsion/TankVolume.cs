using System.Collections.Generic;
using System.Linq;
using Nova.Core.Persistence;
using Nova.Core.Persistence.Protos;
using Nova.Core.Resources;
using Nova.Core.Flight;
using Nova.Core.Utils;

namespace Nova.Core.Components.Propulsion;

// A volumetric tank part. One TankVolume may hold multiple resources
// in a fixed mix (e.g. kerolox = 60% LOx + 40% RP-1 by volume), with
// the part-level pipe capacity (`MaxRate`, L/s shared in/out)
// proportioned across the constituent buffers by capacity fraction:
//
//   buffer.MaxRateIn = buffer.MaxRateOut = MaxRate × (Capacity / Volume)
//
// The proportioning happens at solver-build time; pre-build, the
// individual `Tanks[i].MaxRateIn / MaxRateOut` are 0 — they're config
// holders (Resource, Capacity, optional initial Contents) only.
//
// MaxRate is required in the part config; no defaults in code. This is
// the LP-hygiene contract: every buffer that participates in the LP
// must have a finite, sensible flow cap (see docs/lp_hygiene.md).
public class TankVolume : VirtualComponent {
  public double Volume;
  public double MaxRate;
  public List<Buffer> Tanks = new();

  public TankVolume() {}

  public TankVolume(TankVolumeStructure structure) {
    Volume = structure.Volume;
    MaxRate = structure.MaxRate;
    foreach (var t in structure.Tanks) {
      Tanks.Add(new Buffer {
        Resource = Resource.Get(t.Resource),
        Capacity = t.Capacity,
        Contents = t.Capacity, // default: full
      });
    }
  }

  public override void LoadStructure(PartStructure ps) {
    if (ps.TankVolume == null) return;
    var s = ps.TankVolume;
    Volume = s.Volume;
    MaxRate = s.MaxRate;
    Tanks.Clear();
    foreach (var t in s.Tanks) {
      Tanks.Add(new Buffer {
        Resource = Resource.Get(t.Resource),
        Capacity = t.Capacity,
        Contents = t.Capacity,
      });
    }
  }

  public override void SaveStructure(PartStructure ps) {
    var s = new TankVolumeStructure { Volume = Volume, MaxRate = MaxRate };
    s.Tanks.AddRange(Tanks.Select(t => new TankStructure {
      Resource = t.Resource.Name,
      Capacity = t.Capacity,
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
    var clone = new TankVolume { Volume = Volume, MaxRate = MaxRate };
    clone.Tanks = Tanks.Select(t => new Buffer {
      Resource = t.Resource,
      Capacity = t.Capacity,
      Contents = t.Contents,
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
      // Proportion the part's pipe capacity by this tank's volume share.
      // Empty tanks (Volume = 0) shouldn't happen in valid configs;
      // guard with a zero rate to avoid division-by-zero.
      var rate = Volume > 0 ? MaxRate * (tank.Capacity / Volume) : 0;
      buf.FlowLimits(rate, rate);
      buf.Contents = tank.Contents;
      Tanks[i] = buf;
    }
  }
}
