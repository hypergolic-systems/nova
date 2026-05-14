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

  // Per-slice cryocooler handles. Outer list is parallel to Tanks;
  // inner list holds one Device per *stage* of cooling hardware
  // installed on that slice (BAC tier → 1 entry, ZBO → 2, passive
  // tiers and non-cryo resources → empty). Each device represents
  // the INCREMENTAL hardware added at that stage — stage 1 is the
  // single-stage compressor, stage 2 is the deep cold-finger that
  // stacks on top. Cooler current is the sum of (Activity × max) over
  // every entry, so the BAC↔ZBO heat:EC nonlinearity falls out
  // naturally from each stage's own profile.
  //
  // Stage toggle is purely a per-tick Demand mutation (OnPreSolve):
  // stage k's device gets Demand=1 when (k <= currentStage) else 0.
  // No topology rebuild required.
  internal List<List<Device>> CoolerDevices = new();

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

  // Cumulative EC draw at a given stage (= what the cooler would pull
  // if every stage up through `stage` were running flat out). Returns
  // 0 when the slice can't run a cooler (passive tier, non-cryo).
  // Resource matters because COP collapses at low cold-tap temps
  // (LH₂ at 20 K costs ~2-3× more EC per watt than LOX at 90 K under
  // the same tier).
  private double SliceCumulativeEcWForStage(int i, int stage) {
    if (i >= Tanks.Count) return 0;
    var res = Tanks[i].Resource;
    if (res == null) return 0;
    if (res.MliBoiloffFractionPerDay <= 0
        || res.LatentHeatJPerKg <= 0
        || res.BoilingPointK <= 0) return 0;
    var profile = InsulationTierTable.ActiveProfile(SliceTier(i), stage);
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

  // Cumulative heat output at a given stage. Q_hot = Q_cold + W_in.
  private double SliceCumulativeHeatWForStage(int i, int stage) {
    var ec = SliceCumulativeEcWForStage(i, stage);
    if (ec <= 0) return 0;
    var res = Tanks[i].Resource;
    var profile = InsulationTierTable.ActiveProfile(SliceTier(i), stage);
    if (profile == null) return 0;
    var data = profile.Value;
    var cop = data.CarnotEfficiency * res.BoilingPointK
            / (InsulationTierTable.AmbientK - res.BoilingPointK);
    return ec * (1.0 + cop);
  }

  // Incremental EC drawn by the hardware that stage `s` adds on top
  // of stage `s-1`. Each stage is its own physical compressor /
  // cold-finger unit with its own COP, so the deltas correctly
  // reflect the BAC↔ZBO heat:EC nonlinearity — the LP sees one
  // device per stage and sums the realised draws.
  internal double StageIncrementalEcW(int i, int stage) =>
      SliceCumulativeEcWForStage(i, stage) - SliceCumulativeEcWForStage(i, stage - 1);

  internal double StageIncrementalHeatW(int i, int stage) =>
      SliceCumulativeHeatWForStage(i, stage) - SliceCumulativeHeatWForStage(i, stage - 1);

  // Fractional baseline boiloff this stage removes when fully active —
  // the gap between this stage's residual leak and the previous
  // stage's residual leak. Stage 1 on either BAC or ZBO removes
  // (passive − BAC.active) = 0.09; stage 2 on ZBO removes the
  // remaining (BAC.active − ZBO.active) = 0.01.
  internal double StageRemovalFraction(int i, int stage) {
    if (stage <= 0) return 0;
    var tier = SliceTier(i);
    var here = InsulationTierTable.ActiveProfile(tier, stage);
    if (here == null) return 0;
    var prevActive = stage == 1
        ? InsulationTierTable.Get(tier).PassiveFraction
        : (InsulationTierTable.ActiveProfile(tier, stage - 1)?.ActiveFraction
            ?? InsulationTierTable.Get(tier).PassiveFraction);
    return prevActive - here.Value.ActiveFraction;
  }

  // Operating profile values at the slice's CURRENT stage. Cumulative
  // (i.e. the total EC / heat the cooler is asking for when every
  // stage up through `current` is on). Useful as a "design rated"
  // readout — the realised draws live on SliceCurrent{Ec,Heat}W.
  public double SliceMaxEcW(int i) => SliceCumulativeEcWForStage(i, SliceStage(i));
  public double SliceMaxHeatW(int i) => SliceCumulativeHeatWForStage(i, SliceStage(i));

  // Net boiloff fraction per day. Sum each stage device's Activity ×
  // that stage's removal fraction; subtract from passive. The LP can
  // partially-supply each stage independently (heat-saturated bus →
  // stage 2 throttles before stage 1), so this captures graceful
  // degradation without any per-stage if-ladder.
  public double SliceNetBoiloffFractionPerDay(int i) {
    if (i >= Tanks.Count) return 0;
    var baseline = Tanks[i].Resource?.MliBoiloffFractionPerDay ?? 0;
    if (baseline <= 0) return 0;
    var tier = SliceTier(i);
    var tierData = InsulationTierTable.Get(tier);
    double removed = 0;
    if (i < CoolerDevices.Count) {
      var devices = CoolerDevices[i];
      for (int s = 0; s < devices.Count; s++) {
        removed += devices[s].Activity * StageRemovalFraction(i, s + 1);
      }
    }
    return baseline * (tierData.PassiveFraction - removed);
  }

  // Realised cooler EC draw and heat output — the LP-throttled
  // physical-observable rates the wire publishes. Sum across per-stage
  // devices for this slice; each contributes its own incremental
  // (Activity × max). Stage 0 → all devices have Demand=0 → all
  // Activities=0 → sum is 0.
  public double SliceCurrentEcW(int i) {
    if (i >= CoolerDevices.Count) return 0;
    var devices = CoolerDevices[i];
    double sum = 0;
    for (int s = 0; s < devices.Count; s++)
      sum += devices[s].Activity * StageIncrementalEcW(i, s + 1);
    return sum;
  }

  public double SliceCurrentHeatW(int i) {
    if (i >= CoolerDevices.Count) return 0;
    var devices = CoolerDevices[i];
    double sum = 0;
    for (int s = 0; s < devices.Count; s++)
      sum += devices[s].Activity * StageIncrementalHeatW(i, s + 1);
    return sum;
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

      // Register one cooler device per installed stage (BAC tier = 1,
      // ZBO tier = 2). Each device carries its own incremental
      // EC/heat — stage 1 is BAC-class params; stage 2 (ZBO only) is
      // the deep cold-finger that stacks on top, with its own COP. The
      // player's toggle just sets each device's Demand at OnPreSolve;
      // no LP rebuild ever needed.
      var stageDevices = new List<Device>();
      var maxStage = InsulationTierTable.MaxStage(SliceTier(i));
      var currentStage = SliceStage(i);
      for (int stage = 1; stage <= maxStage; stage++) {
        var ec = StageIncrementalEcW(i, stage);
        if (ec <= 0) continue;
        var heat = StageIncrementalHeatW(i, stage);
        var device = systems.AddDevice(node,
            inputs: new[] { (Resource.ElectricCharge, ec) },
            outputs: new[] { (Resource.Heat, heat) },
            priority: ProcessFlowSystem.Priority.High);
        device.Demand = stage <= currentStage ? 1.0 : 0.0;
        stageDevices.Add(device);
      }
      CoolerDevices.Add(stageDevices);
    }
  }

  public override void OnPreSolve() {
    for (int i = 0; i < CoolerDevices.Count; i++) {
      var devices = CoolerDevices[i];
      var currentStage = SliceStage(i);
      // Each device represents one stage; turn on those whose stage
      // index ≤ current stage. LP picks up Demand changes on the next
      // Solve without any topology rebuild.
      for (int s = 0; s < devices.Count; s++)
        devices[s].Demand = (s + 1) <= currentStage ? 1.0 : 0.0;
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
