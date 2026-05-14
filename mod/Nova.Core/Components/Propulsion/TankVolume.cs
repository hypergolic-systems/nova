using System.Collections.Generic;
using System.Linq;
using Nova.Core.Persistence;
using Nova.Core.Persistence.Protos;
using Nova.Core.Resources;
using Nova.Core.Systems;

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
//
// Per-slice thermal model. Each slice carries an `InsulationTier`
// (parallel `Tiers` list, MLI by default). For cryogenic resources
// (`MliBoiloffFractionPerDay > 0`):
//   - Passive tiers (MLI, HeavyMLI) lose `passive × baseline` of
//     capacity per day, applied as a continuous rate written to
//     `Buffer.BackgroundDrainRate` in OnPostSolve. Lives separately
//     from engine `Rate` so DV-sim's tier-spent check isn't fooled
//     by slow background drain that never returns to zero.
//   - Active tiers (BAC, ZBO) additionally register a process-flow
//     Device — inputs EC, outputs Heat — and the net boiloff lerps
//     between passive and active fractions by the device's Activity.
//     Starve the EC bus or saturate the heat sink and Activity drops,
//     proportionally re-enabling boiloff.
public class TankVolume : VirtualComponent {
  private const double SecondsPerDay = 86400.0;

  public double Volume;
  public double MaxRate;
  public List<Buffer> Tanks = new();
  // Per-slice insulation tier. Parallel to Tanks; entries past the end
  // are treated as InsulationTier.MLI (the proto/enum zero value).
  public List<InsulationTier> Tiers = new();
  // Per-slice runtime cooler stage. Parallel to Tanks; entries past
  // the end default to 0 (off). 0 = passive insulation only; 1 = BAC-
  // class single-stage (valid on BAC + ZBO); 2 = ZBO full stage 2
  // (valid on ZBO only).
  public List<int> CoolerStages = new();

  // Per-slice cryocooler handles, populated by OnBuildSystems for
  // active tiers on cryo resources. Parallel to Tanks; null for
  // passive tiers, non-cryogenic resources, stage-0 slices, or pre-
  // build state.
  internal List<Device> CoolerDevices = new();

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
      var tier = (InsulationTier)t.Insulation;
      Tiers.Add(tier);
      // New tanks launch with the cooler at its max stage — the player
      // picked BAC/ZBO because they want the cooling, not so they can
      // manually flip it on every launch. They can still toggle to a
      // lower stage (or off) from the flight UI.
      CoolerStages.Add(InsulationTierTable.MaxStage(tier));
    }
  }

  public override void LoadStructure(PartStructure ps) {
    if (ps.TankVolume == null) return;
    var s = ps.TankVolume;
    Volume = s.Volume;
    MaxRate = s.MaxRate;
    Tanks.Clear();
    Tiers.Clear();
    CoolerStages.Clear();
    foreach (var t in s.Tanks) {
      Tanks.Add(new Buffer {
        Resource = Resource.Get(t.Resource),
        Capacity = t.Capacity,
        Contents = t.Capacity,
      });
      var tier = (InsulationTier)t.Insulation;
      Tiers.Add(tier);
      CoolerStages.Add(InsulationTierTable.MaxStage(tier));
    }
  }

  public override void SaveStructure(PartStructure ps) {
    var s = new TankVolumeStructure { Volume = Volume, MaxRate = MaxRate };
    for (int i = 0; i < Tanks.Count; i++) {
      s.Tanks.Add(new TankStructure {
        Resource = Tanks[i].Resource.Name,
        Capacity = Tanks[i].Capacity,
        Insulation = (Nova.Core.Persistence.Protos.InsulationTier)SliceTier(i),
      });
    }
    ps.TankVolume = s;
  }

  public override void Save(PartState state) {
    var s = new TankVolumeState();
    for (int i = 0; i < Tanks.Count; i++) {
      s.Tanks.Add(new Nova.Core.Persistence.Protos.TankState {
        Amount = Tanks[i].Contents,
        CoolerStage = SliceStage(i),
      });
    }
    state.TankVolume = s;
  }

  public override void Load(PartState state) {
    if (state.TankVolume == null) return;
    EnsureCoolerStagesSized();
    var saved = state.TankVolume.Tanks;
    for (int i = 0; i < saved.Count && i < Tanks.Count; i++) {
      Tanks[i].Contents = saved[i].Amount;
      CoolerStages[i] = saved[i].CoolerStage;
    }
  }

  public override VirtualComponent Clone() {
    var clone = new TankVolume { Volume = Volume, MaxRate = MaxRate };
    clone.Tanks = Tanks.Select(t => new Buffer {
      Resource = t.Resource,
      Capacity = t.Capacity,
      Contents = t.Contents,
    }).ToList();
    clone.Tiers = new List<InsulationTier>(Tiers);
    clone.CoolerStages = new List<int>(CoolerStages);
    // CoolerDevices intentionally not cloned — they're rebuilt by
    // OnBuildSystems against the new vessel's solver topology.
    return clone;
  }

  private void EnsureCoolerStagesSized() {
    while (CoolerStages.Count < Tanks.Count) CoolerStages.Add(0);
    if (CoolerStages.Count > Tanks.Count) CoolerStages.RemoveRange(Tanks.Count, CoolerStages.Count - Tanks.Count);
  }

  // Replace Tanks in place. The list reference stays the same so any
  // cached references (NovaTankModule.tankVolume, NovaPartModule.Components)
  // see the new shape without re-plumbing. Used by the editor "Set Tank
  // Config" path; not safe to call mid-flight (the solver topology is
  // built from the buffer list at vessel-modify time).
  //
  // Tiers are reset to MLI on every entry — setTankCustom doesn't carry
  // tier info. The UI invokes the separate setTankInsulation op to
  // change tiers after a custom loadout. Stages reset to 0 alongside.
  public void Reconfigure(List<Buffer> newTanks) {
    Tanks.Clear();
    Tiers.Clear();
    CoolerStages.Clear();
    foreach (var t in newTanks) {
      Tanks.Add(t);
      Tiers.Add(InsulationTier.MLI);
      CoolerStages.Add(0);
    }
  }

  // Replace just the per-slice tiers. Editor-only mutator for the
  // setTankInsulation op. Length must match Tanks; caller validates the
  // volume-penalty invariant before calling. Resets per-slice stage to
  // the new tier's MaxStage — picking BAC/ZBO is a "turn the cooler
  // on" gesture; the previous stage may not be valid for the new tier
  // anyway (e.g. stage=2 doesn't exist on BAC).
  public void SetTiers(IReadOnlyList<InsulationTier> newTiers) {
    Tiers.Clear();
    foreach (var t in newTiers) Tiers.Add(t);
    EnsureCoolerStagesSized();
    for (int i = 0; i < CoolerStages.Count; i++)
      CoolerStages[i] = InsulationTierTable.MaxStage(Tiers[i]);
  }

  // Replace the per-slice cooler-stage vector. Caller validates length
  // and per-slice stage range (0..MaxStage(tier)) before calling.
  // Returns true iff any stage actually changed — the caller uses this
  // to decide whether to Invalidate the vessel (device max-rates are
  // baked at OnBuildSystems time, so a stage change has to rebuild).
  public bool SetCoolerStages(IReadOnlyList<int> stages) {
    EnsureCoolerStagesSized();
    bool changed = false;
    for (int i = 0; i < CoolerStages.Count && i < stages.Count; i++) {
      if (CoolerStages[i] != stages[i]) {
        CoolerStages[i] = stages[i];
        changed = true;
      }
    }
    return changed;
  }

  public InsulationTier SliceTier(int i) =>
      i < Tiers.Count ? Tiers[i] : InsulationTier.MLI;

  public int SliceStage(int i) =>
      i < CoolerStages.Count ? CoolerStages[i] : 0;

  // Physical cryocooler model. Cooling power = MLI-baseline heat leak
  // the cooler must remove; EC draw = cooling / COP. Resource matters
  // because COP collapses at low cold-tap temperatures (LH₂ at 20 K
  // costs ~2-3× more EC per watt than LOX at 90 K under the same tier).
  // Picks the effective profile for (tier, stage); returns 0 when no
  // cooler is running (passive tier, non-cryo, or stage=0).
  public double SliceMaxEcW(int i) {
    if (i >= Tanks.Count) return 0;
    var res = Tanks[i].Resource;
    if (res == null) return 0;
    if (res.MliBoiloffFractionPerDay <= 0
        || res.LatentHeatJPerKg <= 0
        || res.BoilingPointK <= 0) return 0;
    var profile = InsulationTierTable.ActiveProfile(SliceTier(i), SliceStage(i));
    if (profile == null) return 0;
    var deltaT = InsulationTierTable.AmbientK - res.BoilingPointK;
    if (deltaT <= 0) return 0;
    var data = profile.Value;
    // MLI-baseline heat leak (W) for this slice. Lv × density turns
    // "fraction of capacity lost per day" into watts of incoming heat.
    var qBaseline = Tanks[i].Capacity * res.MliBoiloffFractionPerDay
                  * res.Density * res.LatentHeatJPerKg / SecondsPerDay;
    // What the cooler actually removes — the gap between the tier's
    // passive (insulation-only) leak and its target residual leak.
    var qRemove = qBaseline * (data.PassiveFraction - data.ActiveFraction);
    var cop = data.CarnotEfficiency * res.BoilingPointK / deltaT;
    return cop > 0 ? qRemove / cop : 0;
  }

  // Heat output at full activity: ec × (1 + COP_real). Q_hot = Q_cold
  // + W_in by energy conservation; Q_cold = ec × cop, so Q_hot =
  // ec × (1 + cop). 0 when no cooler is running.
  public double SliceMaxHeatW(int i) {
    var ec = SliceMaxEcW(i);
    if (ec <= 0) return 0;
    var res = Tanks[i].Resource;
    var profile = InsulationTierTable.ActiveProfile(SliceTier(i), SliceStage(i));
    if (profile == null) return 0;
    var data = profile.Value;
    var cop = data.CarnotEfficiency * res.BoilingPointK
            / (InsulationTierTable.AmbientK - res.BoilingPointK);
    return ec * (1.0 + cop);
  }

  // Net boiloff fraction per day at the current cooler activity.
  // When stage=0 (cooler off), uses the tier's passive fraction
  // (HeavyMLI-equivalent on BAC/ZBO — the hardware is installed but
  // not pumping). When stage>0, lerps from passive to the effective
  // active fraction by the LP device's Activity.
  public double SliceNetBoiloffFractionPerDay(int i) {
    if (i >= Tanks.Count) return 0;
    var baseline = Tanks[i].Resource?.MliBoiloffFractionPerDay ?? 0;
    if (baseline <= 0) return 0;
    var tier = SliceTier(i);
    var tierData = InsulationTierTable.Get(tier);
    var profile = InsulationTierTable.ActiveProfile(tier, SliceStage(i));
    if (profile == null) return baseline * tierData.PassiveFraction;
    var device = i < CoolerDevices.Count ? CoolerDevices[i] : null;
    var activity = device?.Activity ?? 0;
    var data = profile.Value;
    var fraction = tierData.PassiveFraction
                 + (data.ActiveFraction - tierData.PassiveFraction) * activity;
    return baseline * fraction;
  }

  // Realised cooler EC draw and heat output (max × activity). These
  // are what the wire frame publishes — no Activity leakage.
  public double SliceCurrentEcW(int i) {
    var device = i < CoolerDevices.Count ? CoolerDevices[i] : null;
    return SliceMaxEcW(i) * (device?.Activity ?? 0);
  }

  public double SliceCurrentHeatW(int i) {
    var device = i < CoolerDevices.Count ? CoolerDevices[i] : null;
    return SliceMaxHeatW(i) * (device?.Activity ?? 0);
  }

  public override void OnBuildSystems(VesselSystems systems, StagingFlowSystem.Node node) {
    CoolerDevices.Clear();
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

      var maxEcW = SliceMaxEcW(i);
      if (maxEcW > 0) {
        var maxHeatW = SliceMaxHeatW(i);
        var device = systems.AddDevice(node,
            inputs: new[] { (Resource.ElectricCharge, maxEcW) },
            outputs: new[] { (Resource.Heat, maxHeatW) },
            priority: ProcessFlowSystem.Priority.High);
        device.Demand = 1.0;
        CoolerDevices.Add(device);
      } else {
        CoolerDevices.Add(null);
      }
    }
  }

  public override void OnPreSolve() {
    for (int i = 0; i < CoolerDevices.Count; i++) {
      var d = CoolerDevices[i];
      if (d != null) d.Demand = 1.0;
    }
  }

  // Apply per-slice boiloff to each cryogenic Buffer's
  // BackgroundDrainRate. The lerp model integrates Rate +
  // BackgroundDrainRate together, so displayed Contents and the
  // staging system's empty-time forecasts stay correct; meanwhile
  // engine-rate-only consumers (DeltaVSimulation.AllTiersSpent) read
  // Rate directly and aren't fooled into thinking a tier is still
  // burning when only background boiloff is draining.
  public override void OnPostSolve() {
    for (int i = 0; i < Tanks.Count; i++) {
      var fracPerDay = SliceNetBoiloffFractionPerDay(i);
      if (fracPerDay <= 0) {
        if (Tanks[i].BackgroundDrainRate != 0) Tanks[i].BackgroundDrainRate = 0;
        continue;
      }
      Tanks[i].BackgroundDrainRate = -Tanks[i].Capacity * fracPerDay / SecondsPerDay;
    }
  }
}
