<script lang="ts">
  // Expanded body of a tank-part row in the Editor Vessel window.
  // Two nested subsections under the parent row:
  //   1. CAPACITY  — stacked segmented bar where each slice is a
  //                  resource allocation, labelled inline. Drag the
  //                  right edge of a slice to grow/shrink it against
  //                  the trailing unused-space tile (`FREE`); right-
  //                  click `FREE` to add a resource via context menu.
  //                  Slices are always sorted alphabetically by
  //                  canonical resource name so the layout is stable
  //                  across edits.
  //   2. CONTENTS  — one sub-row per slice. The segmented gauge IS
  //                  the slider — drag along its length to set the
  //                  starting amount; FULL / EMPTY are quick-pegs.
  //
  // Footer carries cumulative dry+wet mass and a REVERT. Live-applies
  // on every meaningful change with a 250 ms debounce so a drag doesn't
  // flood the wire op.
  //
  // No telemetry imports here — `onApply` carries the wire payload up
  // to the parent (which owns the `ksp.send` call). Volume is fixed
  // by the part prefab and therefore read-only; the only knobs are
  // capacity slices and starting amounts.

  import { onDestroy } from 'svelte';
  import SegmentGauge from '../SegmentGauge.svelte';
  import { resourceMeta } from '../resource/resource-codes';
  import { ContextMenu, type MenuItem } from '@dragonglass/instruments';
  import { InsulationTier } from '../../telemetry/nova-topics';
  import type { TankCustomEntry, TankSlice } from '../../telemetry/nova-topics';

  interface Props {
    partId: string;
    partTitle: string;
    volume: number;
    tanks: TankSlice[];
    onApply: (entries: TankCustomEntry[]) => void;
    /** Fires after `onApply` (in the same debounce window) carrying the
     *  per-slice insulation tier vector. The parent dispatches this as
     *  `setTankInsulation` — separate op because setTankCustom resets
     *  every slice's tier to MLI on the mod side, and the tier vector
     *  has to be re-applied right after to survive the round-trip. */
    onApplyTiers: (tiers: InsulationTier[]) => void;
  }
  const {
    partId, partTitle, volume, tanks: initialTanks, onApply, onApplyTiers,
  }: Props = $props();

  // Resource densities (kg / L). Mirrors Nova.Core.Resources/Resource.cs.
  // Hard-coded here because the editor doesn't get density off the wire
  // — modded resources fall through to 1.0 (fine for the indicative
  // mass readout).
  const DENSITY: Record<string, number> = {
    'Liquid Hydrogen': 0.07,
    'Liquid Oxygen':   1.20,
    'Methane':         0.42,
    'RP-1':            0.80,
    'Hydrazine':       1.00,
    'Xenon':           2.00,
  };
  const density = (r: string): number => DENSITY[r] ?? 1.0;

  // Canonical Topological resources the user can add to a tank. Source
  // of truth is C# Resource.cs; this list mirrors it. Sorted at definition
  // so the context-menu options always read alphabetically (the slices
  // themselves keep wire order — see the seed effect — so adds/changes
  // don't shuffle existing items out from under the user).
  const ADDABLE_RESOURCES: readonly string[] = [
    'Hydrazine',
    'Liquid Hydrogen',
    'Liquid Oxygen',
    'Methane',
    'RP-1',
    'Xenon',
  ];

  // Cryogenic resources — the ones with non-zero MliBoiloffFractionPerDay
  // in Nova.Core.Resources/Resource.cs. Storables (RP-1, Hydrazine,
  // Xenon) get no thermal hardware regardless of tier setting, so the
  // tier picker is gated to slices in this set.
  const CRYO_RESOURCES = new Set<string>([
    'Liquid Hydrogen',
    'Liquid Oxygen',
    'Methane',
  ]);

  // Per-tier volume penalty — mirrors `InsulationTierTable` in
  // mod/Nova.Core/Components/Propulsion/InsulationTier.cs. Slice's
  // physical footprint = capacity × (1 + penalty); the build-time
  // invariant Σ footprint ≤ Volume is enforced both client-side (for
  // the picker's "no room" disable) and server-side (the mod rejects
  // any setTankInsulation op that would violate it).
  const TIER_VOLUME_PENALTY: Record<InsulationTier, number> = {
    [InsulationTier.MLI]:      0.00,
    [InsulationTier.HeavyMLI]: 0.05,
    [InsulationTier.BAC]:      0.05,
    [InsulationTier.ZBO]:      0.10,
  };

  // Tier display metadata — labels, hints, stage-pip counts, and a
  // compact qualitative descriptor for the readout strip. Pip count
  // matches the C# tier's cryocooler architecture: passive tiers carry
  // no stages, BAC has a single broad-area stage, ZBO adds the deeper
  // cold-finger second stage.
  interface TierMeta {
    label: string;
    name: string;
    stages: 0 | 1 | 2;
    isActive: boolean;
    /** Hover/title hint. */
    hint: string;
    /** Compact descriptor shown in the readout strip when no live
     *  flight telemetry is available (editor scope — wire fields are
     *  zero because the LP isn't running). In flight, replaced by the
     *  numeric readout from the slice frame. */
    descriptor: string;
  }
  const TIER_META: Record<InsulationTier, TierMeta> = {
    [InsulationTier.MLI]: {
      label: 'MLI', name: 'MULTI-LAYER INSULATION', stages: 0, isActive: false,
      hint: 'Multi-Layer Insulation — passive, design baseline.',
      descriptor: 'PASSIVE · BASELINE',
    },
    [InsulationTier.HeavyMLI]: {
      label: 'HVY', name: 'HEAVY MLI', stages: 0, isActive: false,
      hint: 'Heavy MLI — thicker blanket, ~10% of baseline loss. Surface-area penalty.',
      descriptor: 'PASSIVE · THICKENED',
    },
    [InsulationTier.BAC]: {
      label: 'BAC', name: 'BROAD-AREA COOLING', stages: 1, isActive: true,
      hint: 'Broad-Area Cooling — single-stage cryocooler. Draws EC, emits waste heat.',
      descriptor: 'ACTIVE · 1-STAGE',
    },
    [InsulationTier.ZBO]: {
      label: 'ZBO', name: 'ZERO BOIL-OFF', stages: 2, isActive: true,
      hint: 'Zero Boil-Off — two-stage with cold finger. Higher EC + heat cost; closed system.',
      descriptor: 'CLOSED · 2-STAGE',
    },
  };
  // Stable left-to-right order for the blade row. Visual narrative
  // climbs from passive (lowest cost, highest boiloff) to closed
  // (highest cost, zero boiloff).
  const TIER_LIST: InsulationTier[] = [
    InsulationTier.MLI,
    InsulationTier.HeavyMLI,
    InsulationTier.BAC,
    InsulationTier.ZBO,
  ];

  // Compact %/day formatter for live boiloff readouts. Three decimals
  // keeps 3.000, 0.300, 0.030, 0.003 in column alignment.
  function fmtFracPctPerDay(frac: number): string {
    return (frac * 100).toFixed(3) + ' %/d';
  }
  // Compact watt formatter — single decimal under 100 W, integer above.
  function fmtW(w: number): string {
    if (Math.abs(w) < 100) return w.toFixed(1);
    return w.toFixed(0);
  }

  // -------- Hover preview physics mirror ---------------------------
  // Mirror of `Nova.Core.Resources/Resource.cs` (per-resource cryo
  // params) and `Nova.Core.Components.Propulsion/InsulationTier.cs`
  // (per-tier passive/active fractions and Carnot efficiency). Kept
  // inline because the editor needs prospective tier numbers BEFORE
  // the user commits — wire-derived numbers are only available for
  // the currently active tier. Update in lockstep with the C# tables.
  // The mod is the authority at runtime; this mirror exists purely
  // for the "what would this tier give me" hover preview.
  const AMBIENT_K = 280.0;
  const SECONDS_PER_DAY = 86400;
  interface CryoResourceData {
    baselineFracPerDay: number;
    boilingPointK: number;
    latentHeatJPerKg: number;
    densityKgPerL: number;
  }
  const CRYO_DATA: Record<string, CryoResourceData> = {
    'Liquid Hydrogen': { baselineFracPerDay: 0.03,  boilingPointK: 20.3,  latentHeatJPerKg: 446_000, densityKgPerL: 0.07 },
    'Liquid Oxygen':   { baselineFracPerDay: 0.01,  boilingPointK: 90.2,  latentHeatJPerKg: 213_000, densityKgPerL: 1.20 },
    'Methane':         { baselineFracPerDay: 0.005, boilingPointK: 111.7, latentHeatJPerKg: 510_000, densityKgPerL: 0.42 },
  };
  interface TierPhysics {
    passive: number;
    active: number;
    carnotEfficiency: number;
  }
  const TIER_PHYSICS: Record<InsulationTier, TierPhysics> = {
    [InsulationTier.MLI]:      { passive: 1.00, active: 1.00, carnotEfficiency: 0.00 },
    [InsulationTier.HeavyMLI]: { passive: 0.10, active: 0.10, carnotEfficiency: 0.00 },
    [InsulationTier.BAC]:      { passive: 0.10, active: 0.01, carnotEfficiency: 0.20 },
    [InsulationTier.ZBO]:      { passive: 0.10, active: 0.00, carnotEfficiency: 0.10 },
  };

  // Predicted at-full-Activity numbers for (resource, tier, capacity).
  // Returns null for non-cryogenic resources. Mirrors the math in
  // TankVolume.SliceMaxEcW/SliceMaxHeatW/SliceNetBoiloffFractionPerDay
  // — but evaluated at Activity = 1 (full LP supply assumed) so the
  // hover preview shows the design rating, not a degraded value.
  function predictTier(resource: string, tier: InsulationTier, capacity: number):
      { fracPerDay: number; ecW: number; heatW: number } | null {
    const r = CRYO_DATA[resource];
    if (!r) return null;
    const t = TIER_PHYSICS[tier];
    const meta = TIER_META[tier];
    const fracPerDay = r.baselineFracPerDay * (meta.isActive ? t.active : t.passive);
    if (!meta.isActive) return { fracPerDay, ecW: 0, heatW: 0 };
    const deltaT = AMBIENT_K - r.boilingPointK;
    if (deltaT <= 0) return { fracPerDay, ecW: 0, heatW: 0 };
    const qBaseline = capacity * r.baselineFracPerDay * r.densityKgPerL
                    * r.latentHeatJPerKg / SECONDS_PER_DAY;
    const qRemove = qBaseline * (t.passive - t.active);
    const copReal = t.carnotEfficiency * r.boilingPointK / deltaT;
    if (copReal <= 0) return { fracPerDay, ecW: 0, heatW: 0 };
    const ecW = qRemove / copReal;
    return { fracPerDay, ecW, heatW: ecW * (1 + copReal) };
  }

  // Which (slice, tier) is currently being hover-previewed. One slot
  // for the whole panel — only one mouse, one preview at a time.
  let hovered = $state<{ sliceIdx: number; tier: InsulationTier } | null>(null);

  // Per-fuel volumetric LOX demand for engine-typical mass ratios
  // (against Nova's densities — LH2=0.07, LOX=1.20, RP-1=0.80 kg/L).
  // For each fuel volume, the corresponding LOX volume needed in the
  // engine ratio is `volume × LOX_PER_FUEL[fuel]`.
  //
  //   Kerolox  60% LOX + 40% RP-1 by volume → LOX = 1.5  × RP-1
  //            ⇒ 2.25:1 LOX:RP-1 by mass
  //   Hydrolox 26% LOX + 74% LH2 by volume → LOX ≈ 0.351 × LH2
  //            ⇒ ~6:1 LOX:LH2 by mass
  const LOX_PER_FUEL: Record<string, number> = {
    'RP-1':            0.60 / 0.40,   // 1.5
    'Liquid Hydrogen': 0.26 / 0.74,   // ~0.3514
  };
  const OXIDIZER_RESOURCE = 'Liquid Oxygen';

  // Whether the tank's current contents support a single global
  // "Balance" — at least one recognised fuel AND the oxidizer must be
  // present. Tanks with only fuels (no LOX) or only LOX get nothing.
  function canBalance(): boolean {
    const present = new Set(slices.map((s) => s.resource));
    if (!present.has(OXIDIZER_RESOURCE)) return false;
    return Object.keys(LOX_PER_FUEL).some((f) => present.has(f));
  }

  // Single global Balance. Redistributes the fuels and LOX in the tank
  // to engine ratios while:
  //   • preserving the total FOOTPRINT (storable + hardware) of the
  //     balance pool — so a kerolox tank's bar slices keep the same
  //     overall extent, just rebalanced internally;
  //   • preserving the relative ratio between fuels (RP-1 vs LH2)
  //     when both are present — the user's existing fuel split is
  //     kept, only the per-fuel LOX share is corrected;
  //   • leaving non-balance slices (Hydrazine, Xenon, …) untouched.
  //
  // With tier penalties, the math has to account for hardware
  // overhead on each slice. Engine ratios are over STORABLE
  // (1 unit of fuel storable wants LOX_PER_FUEL[f] units of LOX
  // storable), but the volume budget is in FOOTPRINT (storable ×
  // (1 + penalty)). So for each fuel f and the oxidizer:
  //
  //   footprint_f   = storable_f × (1 + p_f)
  //   storable_LOX  = Σ_f (storable_f × LOX_PER_FUEL[f])
  //   footprint_LOX = storable_LOX × (1 + p_LOX)
  //   Σ footprint   = Σ_f footprint_f + footprint_LOX = footprint_pool
  //
  // With storable_f = scale × α_f (normalized fuel share), solve:
  //   scale = footprint_pool /
  //           [ Σ_f α_f (1 + p_f) + (1 + p_LOX) · Σ_f α_f · LOX_PER_FUEL[f] ]
  function balance(): void {
    const oxSlice = slices.find((s) => s.resource === OXIDIZER_RESOURCE);
    const fuelSlices = slices.filter((s) => s.resource in LOX_PER_FUEL);
    if (!oxSlice || fuelSlices.length === 0) return;

    const oxPenalty = TIER_VOLUME_PENALTY[oxSlice.tier];
    const oxFootprint = oxSlice.capacity * (1 + oxPenalty);
    let footprintPool = oxFootprint;
    for (const f of fuelSlices) {
      footprintPool += f.capacity * (1 + TIER_VOLUME_PENALTY[f.tier]);
    }
    if (footprintPool <= 0) return;

    const fuelStorableSum = fuelSlices.reduce((a, s) => a + s.capacity, 0);
    if (fuelStorableSum <= 0) return; // all fuels at zero — no signal for proportions

    let denominator = 0;
    for (const f of fuelSlices) {
      const share = f.capacity / fuelStorableSum;
      denominator += share * (1 + TIER_VOLUME_PENALTY[f.tier]);
      denominator += share * LOX_PER_FUEL[f.resource] * (1 + oxPenalty);
    }
    if (denominator <= 0) return;
    const scale = footprintPool / denominator;

    // Capture each slice's fill fraction BEFORE mutating capacity, then
    // apply it to the new capacity — same fill-preservation semantics
    // as the capacity-handle drag and tier swap.
    let newOxStorable = 0;
    for (const f of fuelSlices) {
      const share = f.capacity / fuelStorableSum;
      const newCap = scale * share;
      const fillFrac = f.capacity > 0 ? f.contents / f.capacity : 0;
      f.capacity = newCap;
      f.contents = newCap * fillFrac;
      newOxStorable += newCap * LOX_PER_FUEL[f.resource];
    }
    const oxFillFrac = oxSlice.capacity > 0 ? oxSlice.contents / oxSlice.capacity : 0;
    oxSlice.capacity = newOxStorable;
    oxSlice.contents = newOxStorable * oxFillFrac;
  }

  // Local working copy of the slice list. Edits land here; the
  // live-apply effect debounce-pushes them up via onApply.
  let slices = $state<TankSlice[]>([]);
  // `propsKey` is the fingerprint the LIVE-APPLY effect compares the
  // current `slices` against to decide whether to dispatch. It's set
  //   (a) by the seed effect when the parent broadcasts a new shape, and
  //   (b) by the live-apply effect right before it dispatches upstream.
  let propsKey = $state<string>('');
  // `lastSyncedProps` tracks what the seed effect has *already*
  // applied. A non-reactive plain variable on purpose — if it were
  // $state and the seed effect both read and wrote it, Svelte's depth
  // tracker would flag the self-loop (`effect_update_depth_exceeded`).
  // Reading non-reactively means the seed effect only depends on
  // `initialTanks`, so the only thing that re-fires it is the parent
  // re-broadcasting a different shape.
  let lastSyncedProps = '';

  $effect(() => {
    const incoming = JSON.stringify(initialTanks);
    if (incoming !== lastSyncedProps) {
      lastSyncedProps = incoming;
      // Preserve wire order — same as C# Buffer order, which is the
      // proto round-trip's natural ordering. No sort here means resource
      // changes via right-click don't shuffle other slices around the
      // user mid-interaction.
      slices = structuredClone(initialTanks);
      propsKey = incoming;
      pendingApply = null;
    }
  });

  const sumMass  = $derived(slices.reduce((a, s) => a + s.contents * density(s.resource), 0));
  // Volume the slices actually occupy in the tank envelope. Storable
  // capacity plus the tier's hardware overhead, summed across slices.
  // The bar is filled proportionally to footprint (not capacity) so a
  // tier swap — which preserves a slice's footprint while shifting the
  // split between storable and hardware — never carves out FREE.
  const sumFootprint = $derived(slices.reduce(
    (a, s) => a + s.capacity * (1 + TIER_VOLUME_PENALTY[s.tier]), 0));
  const unused   = $derived(Math.max(0, volume - sumFootprint));
  // Per-slice footprint helper. Same factor the bar's pct math uses.
  function footprintOf(s: TankSlice): number {
    return s.capacity * (1 + TIER_VOLUME_PENALTY[s.tier]);
  }

  function setContents(i: number, value: number): void {
    const cap = slices[i].capacity;
    slices[i].contents = Math.min(cap, Math.max(0, value));
  }

  function removeSlice(i: number): void {
    slices = slices.filter((_, j) => j !== i);
  }

  // Tier mutator. A tier swap is footprint-invariant: the slice keeps
  // the same chunk of tank volume it had before, but the split between
  // STORABLE capacity and HARDWARE overhead changes. So upgrading
  // MLI→BAC on a 2400 L LOX slice keeps the LOX bar segment at the
  // same width and gives it 2400/1.05 = 2286 L storable + 114 L
  // hardware. No FREE is carved out; other slices are untouched.
  // Contents stay constant (clamped to the new capacity if the
  // upgrade reduced storable below the previously-set starting amount).
  function setTier(i: number, newTier: InsulationTier): void {
    if (i < 0 || i >= slices.length) return;
    if (slices[i].tier === newTier) return;
    const oldCap = slices[i].capacity;
    const oldPenalty = TIER_VOLUME_PENALTY[slices[i].tier];
    const newPenalty = TIER_VOLUME_PENALTY[newTier];
    const footprint = oldCap * (1 + oldPenalty);
    const newCapacity = footprint / (1 + newPenalty);
    // Preserve fill fraction across the swap — same convention the
    // capacity-handle drag uses. A 100%-full slice stays 100% full
    // through the tier change (storable changes, but the player's
    // "how full" reading stays anchored). Without this, an MLI→BAC→MLI
    // round-trip would leave the slice stuck at 95% full because the
    // BAC step's content-clamp ate the headroom and MLI didn't grow
    // contents back.
    const fillFrac = oldCap > 0 ? slices[i].contents / oldCap : 0;
    slices[i].tier = newTier;
    slices[i].capacity = newCapacity;
    slices[i].contents = newCapacity * fillFrac;
  }

  // Storable capacity the slice would have if its tier were swapped to
  // `candidateTier` right now. Used by the hover preview to surface
  // the storable delta before the user commits.
  function capacityAfterTier(sliceIdx: number, candidateTier: InsulationTier): number {
    const oldPenalty = TIER_VOLUME_PENALTY[slices[sliceIdx].tier];
    const newPenalty = TIER_VOLUME_PENALTY[candidateTier];
    const footprint = slices[sliceIdx].capacity * (1 + oldPenalty);
    return footprint / (1 + newPenalty);
  }

  // The set of cryo slices in wire order, paired with their original
  // index so the picker can address them back to the slice list. Drives
  // the THERMAL section's row count — non-cryo slices (RP-1, hydrazine,
  // etc.) simply don't appear.
  const cryoSlices = $derived.by<Array<{ idx: number; slice: TankSlice }>>(() => {
    const out: Array<{ idx: number; slice: TankSlice }> = [];
    for (let i = 0; i < slices.length; i++) {
      if (CRYO_RESOURCES.has(slices[i].resource)) out.push({ idx: i, slice: slices[i] });
    }
    return out;
  });

  // Append a resource at the current free-space size, full by default
  // — players adding fuel almost always want the tank to launch with
  // it; the contents slider is right there if they want partial. The
  // new slice lands at the end of the list — wire order — so existing
  // slices don't shift out from under the user. Called from the FREE-
  // tile context menu; the menu only opens when free > 0, so capacity
  // is always > 0 here.
  function addResource(name: string): void {
    if (slices.some(s => s.resource === name)) return;
    const cap = unused;
    if (cap <= 0) return;
    slices = [...slices, {
      resource: name, capacity: cap, contents: cap,
      tier: InsulationTier.MLI,
      stage: 0,
      maxStage: 0,
      boiloffFractionPerDay: 0,
      coolerEcW: 0,
      coolerHeatW: 0,
    }];
  }

  // Swap the resource of an existing slice in place. Capacity and
  // contents are preserved volumetrically — the user told us "this slot
  // holds X litres", a resource change shouldn't lose that allocation.
  // Position is preserved too: the slice stays at its current index.
  function changeSliceResource(sliceIdx: number, newResource: string): void {
    if (sliceIdx < 0 || sliceIdx >= slices.length) return;
    if (slices.some((s, j) => j !== sliceIdx && s.resource === newResource)) return;
    slices[sliceIdx].resource = newResource;
  }

  // ---------- CAPACITY-BAR DRAG ----------------------------------
  // Each slice exposes a 6-px-wide handle on its right edge. Dragging
  // grows or shrinks ONLY the dragged slice — the change is absorbed
  // by the trailing FREE pool, never by the slice's neighbours. So
  // [RP-1: 500][LOX: 500] dragged-left becomes [RP-1: 400][LOX: 500]
  // [FREE: 100], with LOX visually shifting left because RP-1 is now
  // narrower. Cannot grow past free-pool size; cannot shrink below 0.
  // Contents follow capacity downward so contents never exceeds
  // capacity post-resize.
  //
  // Listener strategy mirrors `FloatingWindow`: register pointermove/
  // pointerup on `document` only while a drag is live. KSP's CEF
  // delivers events reliably via document-level listeners — the older
  // `<svelte:window>`-bound + setPointerCapture approach silently
  // dropped pointermove there, breaking the drag in the live game even
  // though the dev-server Chromium accepted it.

  let barEl: HTMLDivElement | null = $state(null);
  type CapDrag = {
    sliceIdx: number;
    startX: number;
    /** Bar units this slice owned at drag start — the slice's footprint
     *  (storable + hardware), since the bar is rendered footprint-wise.
     *  The drag moves footprint, capacity is derived from it via the
     *  tier's penalty. */
    footprint0: number;
    /** Bar units available outside this slice (= unused at start). */
    freeAtStart: number;
    /** Volume penalty for this slice's tier — held constant during the
     *  drag (tier swap closes the menu). */
    penalty: number;
    /** Fill fraction (contents/capacity) at drag start. Held constant
     *  during the drag so an 80%-full slice stays 80%-full as it
     *  resizes — the user's notion of "how full" tracks the geometry,
     *  not an absolute litre amount. New (contents=0) slices stay at
     *  fillFrac0=0 and remain empty regardless of resize. */
    fillFrac0: number;
  };
  let capDrag: CapDrag | null = null;

  function onCapHandlePointerDown(e: PointerEvent, sliceIdx: number): void {
    if (e.button !== 0) return;
    e.preventDefault();
    e.stopPropagation();
    const cap0 = slices[sliceIdx].capacity;
    const contents0 = slices[sliceIdx].contents;
    const penalty = TIER_VOLUME_PENALTY[slices[sliceIdx].tier];
    capDrag = {
      sliceIdx,
      startX: e.clientX,
      footprint0: cap0 * (1 + penalty),
      freeAtStart: unused,
      penalty,
      fillFrac0: cap0 > 0 ? contents0 / cap0 : 0,
    };
    document.addEventListener('pointermove', onCapHandlePointerMove);
    document.addEventListener('pointerup', onCapHandlePointerUp);
    document.addEventListener('pointercancel', onCapHandlePointerUp);
  }
  function onCapHandlePointerMove(e: PointerEvent): void {
    if (!capDrag || !barEl) return;
    const w = barEl.clientWidth;
    if (w <= 0) return;
    const delta = ((e.clientX - capDrag.startX) / w) * volume;
    // Drag delta is in bar-units = L of footprint. Clamp to
    // [0, footprint0 + freeAtStart] so the slice can't eat past the
    // FREE pool or shrink below zero.
    const nextFootprint = Math.max(0,
        Math.min(capDrag.footprint0 + capDrag.freeAtStart, capDrag.footprint0 + delta));
    const nextCap = nextFootprint / (1 + capDrag.penalty);
    const li = capDrag.sliceIdx;
    slices[li].capacity = nextCap;
    slices[li].contents = nextCap * capDrag.fillFrac0;
  }
  function onCapHandlePointerUp(): void {
    if (!capDrag) return;
    capDrag = null;
    document.removeEventListener('pointermove', onCapHandlePointerMove);
    document.removeEventListener('pointerup', onCapHandlePointerUp);
    document.removeEventListener('pointercancel', onCapHandlePointerUp);
  }

  // ---------- Capacity-bar context menu ---------------------------
  // Right-click on the capacity bar opens a small two-level menu.
  // Top level shows category actions:
  //   • Change to… ▸ / Add resource… ▸ — opens a sub-menu listing the
  //     resources NOT currently in the tank. Picking one swaps the
  //     clicked slice (change mode) or appends a new slice at full
  //     FREE space (add mode).
  //   • Balance — global rebalance of fuels + LOX to engine ratios
  //     (kerolox 60/40, hydrolox 74/26 by volume), preserving the
  //     fuel-ratio split and total balance-pool capacity. Only shown
  //     when the tank holds at least one recognised fuel + LOX.
  //
  // The sub-menu replaces the top-level menu in place at the same
  // anchor coords — the Dragonglass ContextMenu is single-pane. Mode
  // (change vs add) is captured at top-level click time and threaded
  // into the sub-menu's onSelect closures.
  let menu = $state<{ items: MenuItem[]; x: number; y: number } | null>(null);

  function openBarMenu(e: MouseEvent): void {
    e.preventDefault();
    e.stopPropagation();

    // Identify the click target. closest() walks up from e.target which
    // can be any inner element (slice-code/cap span, handle, the slice
    // itself, or the FREE tile). The drag handle's parent is the slice
    // it belongs to, so right-click on a handle resolves to that slice.
    const sliceEl = (e.target as HTMLElement | null)?.closest('.tre__slice') as HTMLElement | null;
    const isUnused = !!sliceEl?.classList.contains('tre__slice--unused');
    const sliceIdxStr = sliceEl?.getAttribute('data-slice-idx');
    const sliceIdx = sliceIdxStr != null ? parseInt(sliceIdxStr, 10) : -1;
    const x = e.clientX;
    const y = e.clientY;

    const present = new Set(slices.map(s => s.resource));
    const candidateResources = ADDABLE_RESOURCES.filter(r => !present.has(r));

    const items: MenuItem[] = [];

    if (candidateResources.length > 0) {
      const isChange = !isUnused && sliceIdx >= 0;
      items.push({
        label: isChange ? 'Change to… ▸' : 'Add resource… ▸',
        onSelect: () => {
          // Replace the top-level menu with a sub-menu of resource
          // options. Anchors at the same coords so it appears in place.
          const sub = candidateResources.map((r): MenuItem => ({
            label: r,
            onSelect: () => {
              if (isChange) changeSliceResource(sliceIdx, r);
              else addResource(r);
              menu = null;
            },
          }));
          menu = { items: sub, x, y };
        },
      });
    }

    if (canBalance()) {
      items.push({
        label: 'Balance',
        onSelect: () => { balance(); menu = null; },
      });
    }

    if (items.length === 0) return;
    menu = { items, x, y };
  }

  // Portal action: relocate `node` to <body> so descendant
  // `position: fixed` styles resolve against the viewport, not the
  // FloatingWindow. The FloatingWindow uses `backdrop-filter`, which
  // (per CSS containing-block rules) turns it into the containing
  // block for any fixed descendant — `.ctx-menu`'s `left/top` would
  // otherwise be measured from the panel's edge and pinned offscreen
  // for any click on the panel's right side.
  function portal(node: HTMLElement) {
    document.body.appendChild(node);
    return { destroy() { node.remove(); } };
  }

  // ---------- CONTENTS DRAG --------------------------------------
  // Drag-anywhere on the contents-gauge sub-row sets the fraction of
  // capacity for that slice. PointerDown also seeds an immediate value
  // so a click without movement registers as "set to here".
  //
  // Same document-listener pattern as the capacity-handle drag — see
  // the comment above `onCapHandlePointerDown` for why <svelte:window>
  // / setPointerCapture didn't survive KSP's CEF.
  type FillDrag = { sliceIdx: number; el: HTMLDivElement };
  let fillDrag: FillDrag | null = null;

  function fractionFromX(el: HTMLDivElement, clientX: number): number {
    const rect = el.getBoundingClientRect();
    if (rect.width <= 0) return 0;
    return Math.max(0, Math.min(1, (clientX - rect.left) / rect.width));
  }
  function onFillPointerDown(e: PointerEvent, sliceIdx: number): void {
    if (e.button !== 0) return;
    e.preventDefault();
    const el = e.currentTarget as HTMLDivElement;
    fillDrag = { sliceIdx, el };
    setContents(sliceIdx, slices[sliceIdx].capacity * fractionFromX(el, e.clientX));
    document.addEventListener('pointermove', onFillPointerMove);
    document.addEventListener('pointerup', onFillPointerUp);
    document.addEventListener('pointercancel', onFillPointerUp);
  }
  function onFillPointerMove(e: PointerEvent): void {
    if (!fillDrag) return;
    setContents(fillDrag.sliceIdx, slices[fillDrag.sliceIdx].capacity * fractionFromX(fillDrag.el, e.clientX));
  }
  function onFillPointerUp(): void {
    if (!fillDrag) return;
    fillDrag = null;
    document.removeEventListener('pointermove', onFillPointerMove);
    document.removeEventListener('pointerup', onFillPointerUp);
    document.removeEventListener('pointercancel', onFillPointerUp);
  }

  // ---------- LIVE APPLY (debounced) -----------------------------
  // Watches the staged shape and pushes a setTankCustom payload up
  // 250 ms after the last edit. Skips the first effect run (matches
  // the props the parent already has).
  //
  // `pendingApply` is intentionally NOT $state — the live-apply effect
  // reads and writes it (clearTimeout + setTimeout), and a $state read
  // would self-trigger the effect (effect_update_depth_exceeded). It's
  // a private control-flow flag; only `saving` needs to be reactive
  // because the footer reads it for the spinner.
  let pendingApply: number | null = null;
  let saving = $state(false);

  function buildPayload(): TankCustomEntry[] {
    return slices
      .filter(s => s.capacity > 0)
      .map(s => [s.resource, s.capacity, Math.min(s.contents, s.capacity)] as TankCustomEntry);
  }

  $effect(() => {
    const fingerprint = JSON.stringify(slices);
    if (fingerprint === propsKey) return; // matches what parent already shows
    if (pendingApply != null) clearTimeout(pendingApply);
    saving = true;
    pendingApply = window.setTimeout(() => {
      propsKey = fingerprint;
      pendingApply = null;
      saving = false;
      // setTankCustom replaces the loadout AND resets every slice's
      // tier to MLI mod-side; setTankInsulation immediately re-applies
      // the desired tier vector so the round-trip is idempotent. Order
      // matters — onApplyTiers MUST land after onApply.
      onApply(buildPayload());
      onApplyTiers(slices.map((s) => s.tier));
    }, 250);
  });

  function revert(): void {
    if (pendingApply != null) clearTimeout(pendingApply);
    pendingApply = null;
    saving = false;
    const incoming = JSON.stringify(initialTanks);
    slices = structuredClone(initialTanks);
    propsKey = incoming;
    lastSyncedProps = incoming;
  }

  // Tonnes formatter: 0.00 t / 1.24 t / 12.4 t / 124 t.
  const fmtTonnes = (kg: number): string => {
    const t = kg / 1000;
    if (Math.abs(t) < 0.005) return '0.00';
    if (Math.abs(t) >= 100) return t.toFixed(0);
    if (Math.abs(t) >= 10) return t.toFixed(1);
    return t.toFixed(2);
  };
  const fmtL = (l: number): string => {
    // Thousands separators kick in at 1000 — 4,000 L reads cleanly
    // both ways. Smaller values keep one decimal for granularity.
    if (Math.abs(l) >= 1000) return Math.round(l).toLocaleString();
    if (Math.abs(l) >= 100) return l.toFixed(0);
    return l.toFixed(1);
  };

  // If the component unmounts mid-drag (row collapsed, panel torn
  // down) the document listeners would otherwise keep firing against
  // stale closures. Mirrors FloatingWindow's defensive teardown.
  onDestroy(() => {
    onCapHandlePointerUp();
    onFillPointerUp();
  });
</script>

<div class="tre" data-part-id={partId} aria-label={`Tank editor for ${partTitle}`}>
  <!-- CAPACITY stacked bar ---------------------------------------- -->
  <div class="tre__section">
    <div class="tre__section-head">
      <span class="tre__rule"></span>
      <span class="tre__section-title">CAPACITY</span>
      <!-- Total committed footprint vs gross volume. The numerator is
           sumFootprint (storable + hardware), which is invariant under
           tier swaps — that's the player's promise: "the tank's gross
           volume is fixed; you can rearrange how it's used, but the
           total commitment number doesn't move when you flip an
           insulation tier." Propellant-vs-hardware breakdown lives per
           slice in the THERMAL sub-line. -->
      <span class="tre__section-meta">
        {fmtL(sumFootprint)} / {fmtL(volume)}<em>L</em>
      </span>
    </div>

    <div class="tre__bar"
         bind:this={barEl}
         role="presentation"
         oncontextmenu={openBarMenu}>
      {#each slices as s, i (i)}
        {@const meta = resourceMeta(s.resource)}
        {@const fp = footprintOf(s)}
        {@const pct = volume > 0 ? (fp / volume) * 100 : 0}
        <div class="tre__slice"
             class:tre__slice--narrow={pct < 8}
             style:width="{pct}%"
             style:--slice-color={meta.color}
             style:--slice-tint={meta.tint}
             data-slice-idx={i}
             title="Right-click to change resource">
          <span class="tre__slice-code">{meta.code}</span>
          <span class="tre__slice-cap">{fmtL(s.capacity)}<em>L</em></span>
          <button type="button"
                  class="tre__slice-x"
                  aria-label="Remove {meta.code}"
                  title="Remove {meta.code}"
                  onclick={() => removeSlice(i)}>×</button>
          <span class="tre__handle"
                role="separator"
                aria-orientation="vertical"
                aria-label="Drag to resize {meta.code}"
                onpointerdown={(e) => onCapHandlePointerDown(e, i)}></span>
        </div>
      {/each}
      {#if unused > 0.5}
        <div class="tre__slice tre__slice--unused"
             style:width="{(unused / volume) * 100}%"
             title="Right-click to add a resource">
          <span class="tre__slice-code">FREE</span>
          <span class="tre__slice-cap">{fmtL(unused)}<em>L</em></span>
        </div>
      {/if}
    </div>
  </div>

  <!-- CONTENTS sliders --------------------------------------------- -->
  {#if slices.length > 0}
    <div class="tre__section">
      <div class="tre__section-head">
        <span class="tre__rule"></span>
        <span class="tre__section-title">STARTING AMOUNT</span>
      </div>

      <ul class="tre__contents">
        {#each slices as s, i (i)}
          {@const meta = resourceMeta(s.resource)}
          {@const frac = s.capacity > 0 ? s.contents / s.capacity : 0}
          {@const massT = (s.contents * density(s.resource)) / 1000}
          <li class="tre__row">
            <span class="tre__row-code"
                  style:--slice-color={meta.color}
                  style:--slice-tint={meta.tint}>{meta.code}</span>
            <div class="tre__row-gauge"
                 style:--sg-color-tint={meta.color}
                 style:--sg-glow-tint={meta.glow}
                 style:--sg-tint-tint={meta.tint}
                 onpointerdown={(e) => onFillPointerDown(e, i)}
                 role="slider"
                 tabindex="0"
                 aria-label={`${meta.code} starting amount`}
                 aria-valuemin={0}
                 aria-valuemax={s.capacity}
                 aria-valuenow={s.contents}>
              <SegmentGauge fraction={frac} segments={20} />
            </div>
            <button type="button" class="tre__peg"
                    aria-label="Empty"
                    title="Empty"
                    onclick={() => setContents(i, 0)}>0</button>
            <button type="button" class="tre__peg"
                    aria-label="Full"
                    title="Full"
                    onclick={() => setContents(i, s.capacity)}>F</button>
            <span class="tre__row-amount">{fmtL(s.contents)}<em>L</em></span>
            <span class="tre__row-mass" class:tre__row-mass--zero={massT < 0.005}>
              {fmtTonnes(s.contents * density(s.resource))}<em>t</em>
            </span>
          </li>
        {/each}
      </ul>
    </div>
  {/if}

  <!-- THERMAL section: one row per cryogenic slice. Lets the player
       commit a slice to a passive or active insulation tier; mod side
       enforces the volume-penalty invariant and the cooler LP runs in
       flight. Hidden entirely on tanks with no cryo slices so the
       editor stays compact for storables-only parts. -->
  {#if cryoSlices.length > 0}
    <div class="tre__section">
      <div class="tre__section-head">
        <span class="tre__rule"></span>
        <span class="tre__section-title">THERMAL</span>
        <span class="tre__section-sub">insulation · cryocooling</span>
      </div>

      <ul class="tre__therms">
        {#each cryoSlices as { idx, slice } (idx)}
          {@const meta = resourceMeta(slice.resource)}
          <!-- "live" means the LP is currently solving this cooler — true
                only when the wire reports a non-zero EC or heat draw.
                Boiloff isn't a "live" signal because passive tiers (and
                active tiers in editor scope) always emit a non-zero
                boiloff fraction; gating on it falsely treats the editor
                as live and ends up displaying 0 W for EC/heat where
                the mirror's design-rating values would be correct. -->
          {@const live = slice.coolerEcW > 0 || slice.coolerHeatW > 0}
          {@const hov = hovered?.sliceIdx === idx ? hovered.tier : null}
          {@const previewTier = hov !== null ? hov : slice.tier}
          {@const previewMeta = TIER_META[previewTier]}
          {@const isPreview = hov !== null && hov !== slice.tier}
          {@const previewCap = isPreview ? capacityAfterTier(idx, previewTier) : slice.capacity}
          {@const previewStats = isPreview ? predictTier(slice.resource, previewTier, previewCap) : null}
          {@const activeStats = predictTier(slice.resource, slice.tier, slice.capacity)}
          {@const liveStats = (!isPreview && live)
              ? { fracPerDay: slice.boiloffFractionPerDay, ecW: slice.coolerEcW, heatW: slice.coolerHeatW }
              : null}
          {@const stats = isPreview ? previewStats : (liveStats ?? activeStats)}
          {@const previewHwL = previewCap * TIER_VOLUME_PENALTY[previewTier]}
          <li class="tre__therm-row"
              class:tre__therm-row--previewing={isPreview}>
            <!-- Primary line: code chip + segmented tier track. -->
            <div class="tre__therm-head">
              <span class="tre__row-code"
                    style:--slice-color={meta.color}
                    style:--slice-tint={meta.tint}>{meta.code}</span>

              <div class="tre__tier-track"
                   role="radiogroup"
                   aria-label={`${meta.code} insulation tier`}
                   style:--slice-color={meta.color}
                   style:--slice-glow={meta.glow}
                   style:--slice-tint={meta.tint}>
                {#each TIER_LIST as tier (tier)}
                  {@const tm = TIER_META[tier]}
                  {@const active = slice.tier === tier}
                  <button type="button"
                          class="tre__tier"
                          class:tre__tier--active={active}
                          class:tre__tier--passive={!tm.isActive}
                          role="radio"
                          aria-checked={active}
                          aria-label={tm.name}
                          onpointerenter={() => { hovered = { sliceIdx: idx, tier }; }}
                          onpointerleave={() => { if (hovered?.sliceIdx === idx && hovered.tier === tier) hovered = null; }}
                          onfocus={() => { hovered = { sliceIdx: idx, tier }; }}
                          onblur={() => { if (hovered?.sliceIdx === idx && hovered.tier === tier) hovered = null; }}
                          onclick={() => setTier(idx, tier)}>
                    <span class="tre__tier-led" aria-hidden="true"></span>
                    <span class="tre__tier-label">{tm.label}</span>
                    <span class="tre__tier-stage" aria-hidden="true">
                      {#each Array(tm.stages) as _, i (i)}
                        <i class="tre__tier-pip"></i>
                      {/each}
                    </span>
                  </button>
                {/each}
              </div>
            </div>

            <!-- Stats sub-line. Sits under the track, indented to line
                 up with the start of the track. Shows the inspection
                 target's numerical state: committed tier when nothing's
                 hovered, hovered tier when previewing. Mirrors the
                 ResourceView ".rsv__sub" pattern for visual consistency.
                 Stats include:
                   • tier name (slice color when committed, accent when
                     previewing — flags "not yet committed")
                   • boiloff %/day
                   • EC draw and waste heat (active tiers only)
                   • volume cost (HW for committed; storable + delta
                     vs current for hover preview)
                 All numbers come from the wire when live, otherwise
                 from the local physics mirror. -->
            <!-- Sub-line uses a 2-column CSS grid with named areas so
                 every state lays out identically — only the cell
                 contents change between tier hovers. Row 1 is the
                 full-width tier name; rows 2-3 hold the four stat
                 cells (boiloff, EC, heat, hardware-cost). Grid-area
                 pinning makes the layout bit-for-bit identical for
                 MLI / HVY / BAC / ZBO whether committed or hover-
                 previewed — only the numbers and the tier name change.
                 Numbers are always positive; direction is encoded by
                 colour alone (warn-amber on the hardware cost when
                 non-zero). -->
            <div class="tre__therm-sub"
                 class:tre__therm-sub--preview={isPreview}>
              <span class="tre__sub-tier"
                    style:--slice-color={meta.color}>{previewMeta.name}</span>
              <span class="tre__sub-field tre__sub-field--boil">
                <span class="tre__sub-label">boil</span>
                <span class="tre__sub-pct">{fmtFracPctPerDay(stats?.fracPerDay ?? 0)}</span>
              </span>
              <span class="tre__sub-field tre__sub-field--ec">
                <span class="tre__sub-label">EC</span>
                <span class="tre__sub-ec">{fmtW(stats?.ecW ?? 0)}<em>W</em></span>
              </span>
              <span class="tre__sub-field tre__sub-field--heat">
                <span class="tre__sub-label">heat</span>
                <span class="tre__sub-heat">{fmtW(stats?.heatW ?? 0)}<em>W</em></span>
              </span>
              <span class="tre__sub-field tre__sub-field--hw">
                <span class="tre__sub-label">hw</span>
                <span class="tre__sub-vol"
                      class:tre__sub-vol--warn={previewHwL > 0.5}>{fmtL(previewHwL)}<em>L</em></span>
              </span>
            </div>
          </li>
        {/each}
      </ul>
    </div>
  {/if}

  <!-- FOOTER ------------------------------------------------------- -->
  <div class="tre__footer">
    <span class="tre__footer-mass">
      <span class="tre__label tre__label--dim">Σ MASS</span>
      <span class="tre__footer-mass-value">{fmtTonnes(sumMass)}<em>t</em></span>
      {#if saving}<span class="tre__saving" aria-label="Saving">∙∙∙</span>{/if}
    </span>
    <button type="button" class="tre__revert"
            disabled={JSON.stringify(slices) === propsKey}
            onclick={revert}>REVERT</button>
  </div>
</div>

{#if menu}
  <div use:portal class="tre__menu-portal">
    <ContextMenu items={menu.items} x={menu.x} y={menu.y} onDismiss={() => (menu = null)} />
  </div>
{/if}

<style>
  /* Single-component scope. Indentation depth from the parent panel
     comes from the consuming list's nesting; this body sits flush at
     its parent's content edge with its own internal rhythm. */

  .tre {
    display: flex;
    flex-direction: column;
    gap: 12px;
    padding: 8px 4px 10px 16px;
    border-left: 1px solid var(--line);
    margin-left: 6px;
    font-family: var(--font-mono);
    font-size: 11px;
    color: var(--fg);
  }

  /* Section heads carry a leading rule + small caps title + meta. */
  .tre__section {
    display: flex;
    flex-direction: column;
    gap: 6px;
  }
  .tre__section-head {
    display: flex;
    align-items: center;
    gap: 8px;
    font-family: var(--font-display);
    font-size: 10px;
    letter-spacing: 0.22em;
    color: var(--fg-dim);
  }
  .tre__rule {
    flex: 0 0 12px;
    height: 1px;
    background: var(--line-bright);
  }
  .tre__section-title { flex: 0 0 auto; }
  .tre__section-meta {
    margin-left: auto;
    font-family: var(--font-mono);
    font-size: 11px;
    letter-spacing: 0;
    color: var(--accent);
    font-variant-numeric: tabular-nums;
  }
  .tre__section-meta em {
    font-style: normal; font-size: 9px;
    color: var(--fg-dim); margin-left: 3px; letter-spacing: 0.14em;
  }

  /* Stacked capacity bar. Slices are positioned via flex-basis on
     percentage widths; each slice paints its own background ramp from
     its --slice-color so they read as discrete materials side-by-side
     rather than a single tinted strip. The trailing FREE tile uses a
     diagonal hatch so unused volume reads visually different from any
     allocated resource — even at narrow widths. */
  .tre__bar {
    position: relative;
    display: flex;
    align-items: stretch;
    height: 30px;
    border: 1px solid var(--line-bright);
    background:
      linear-gradient(180deg, rgba(0, 0, 0, 0.50) 0%, rgba(0, 0, 0, 0.25) 100%);
    box-shadow: inset 0 1px 0 rgba(0, 0, 0, 0.55);
    user-select: none;
    overflow: hidden;
  }
  .tre__slice {
    position: relative;
    min-width: 0;
    display: flex;
    flex-direction: column;
    justify-content: center;
    align-items: flex-start;
    padding: 0 6px;
    color: var(--fg);
    /* No `overflow: hidden` here. The drag handle is positioned at
       `right: -3px; width: 6px` so half of it extends past the slice
       boundary — and `overflow: hidden` on a parent makes the
       overflowing half non-hit-testable in older Chromium (e.g. the
       CEF version KSP ships). The bar's own overflow: hidden still
       clips at the bar's outer edge, so handles can't escape into the
       panel chrome. Slice-internal text overflow is mitigated by
       `tre__slice--narrow` hiding the L value at small widths. */
    cursor: context-menu;
    background:
      linear-gradient(180deg,
        color-mix(in srgb, var(--slice-color) 28%, transparent) 0%,
        color-mix(in srgb, var(--slice-color) 14%, transparent) 60%,
        color-mix(in srgb, var(--slice-color) 8%,  transparent) 100%),
      var(--slice-tint, transparent);
    box-shadow: inset 1px 0 0 rgba(255, 255, 255, 0.04);
  }
  .tre__slice:first-child { box-shadow: none; }
  .tre__slice--narrow .tre__slice-cap { display: none; }
  .tre__slice--narrow .tre__slice-code { font-size: 9px; }
  .tre__slice-code {
    font-family: var(--font-display);
    font-size: 10px;
    letter-spacing: 0.16em;
    color: var(--slice-color);
    text-shadow: 0 0 6px color-mix(in srgb, var(--slice-color) 35%, transparent);
    line-height: 1.1;
  }
  .tre__slice-cap {
    font-size: 10px;
    color: var(--fg);
    font-variant-numeric: tabular-nums;
    line-height: 1.1;
  }
  .tre__slice-cap em {
    font-style: normal;
    font-size: 8px;
    color: var(--fg-dim);
    margin-left: 2px;
    letter-spacing: 0.14em;
  }
  .tre__slice-x {
    position: absolute;
    top: 2px;
    right: 8px;
    width: 12px;
    height: 12px;
    line-height: 11px;
    text-align: center;
    color: var(--fg-mute);
    font-size: 12px;
    background: rgba(0, 0, 0, 0.35);
    border: 1px solid transparent;
    opacity: 0;
    transition: opacity 160ms ease, color 160ms ease, border-color 160ms ease;
    cursor: pointer;
  }
  .tre__slice:hover .tre__slice-x { opacity: 0.85; }
  .tre__slice-x:hover { color: var(--alert); border-color: var(--alert); opacity: 1; }

  /* Drag handle: 12 px-wide hit zone straddling the slice's right edge
     (-6 left, +6 right of the boundary). Generous because the boundary
     line itself is only 2 px and KSP's CEF hit-test needs forgiveness.
     Two pieces visualize the affordance ALWAYS so the user can see
     where to grab without hovering first:
       • a 2 px-wide vertical line (accent-dim) sitting on the boundary
       • a small grip pip — 4 px-wide notch in the middle — visible
         against the dim line, snaps to bright accent on hover.
     Hover/active state lifts both pieces to bright accent + glow. */
  .tre__handle {
    position: absolute;
    top: 0;
    bottom: 0;
    right: -6px;
    width: 12px;
    cursor: col-resize;
    z-index: 2;
  }
  .tre__handle::before {
    content: '';
    position: absolute;
    top: 3px;
    bottom: 3px;
    left: 50%;
    margin-left: -1px;
    width: 2px;
    background: var(--accent-dim);
    opacity: 0.55;
    transition: background 160ms ease, opacity 160ms ease, box-shadow 160ms ease;
  }
  .tre__handle::after {
    content: '';
    position: absolute;
    top: 50%;
    left: 50%;
    margin-left: -2px;
    margin-top: -3px;
    width: 4px;
    height: 6px;
    border-left: 1px solid var(--bg-panel-strong);
    border-right: 1px solid var(--bg-panel-strong);
    pointer-events: none;
  }
  .tre__handle:hover::before,
  .tre__handle:active::before {
    background: var(--accent);
    opacity: 1;
    box-shadow: 0 0 6px var(--accent-glow);
  }

  /* Unused-volume tile: diagonal hatch over a darker background. */
  /* Unused-volume tile: diagonal hatch + cursor hint that this is
     interactive (right-click to add a resource). Hover lifts the tile
     into accent territory so it reads as a target. */
  .tre__slice--unused {
    background:
      repeating-linear-gradient(
        45deg,
        rgba(255, 255, 255, 0.025) 0 4px,
        transparent 4px 8px),
      rgba(0, 0, 0, 0.40);
    color: var(--fg-mute);
    cursor: context-menu;
    transition: background 200ms ease, color 200ms ease;
  }
  .tre__slice--unused:hover {
    background:
      repeating-linear-gradient(
        45deg,
        rgba(126, 245, 184, 0.10) 0 4px,
        transparent 4px 8px),
      rgba(126, 245, 184, 0.05);
    color: var(--accent);
  }
  .tre__slice--unused:hover .tre__slice-code {
    color: var(--accent);
    text-shadow: 0 0 5px var(--accent-glow);
  }
  .tre__slice--unused:hover .tre__slice-cap { color: var(--fg); }
  .tre__slice--unused .tre__slice-code {
    color: var(--fg-mute);
    text-shadow: none;
  }
  .tre__slice--unused .tre__slice-cap { color: var(--fg-dim); }
  .tre__slice--unused:focus-visible { outline: 1px solid var(--accent); outline-offset: -2px; }

  /* Contents rows. The gauge is the slider — pointerdown anywhere on
     the row's gauge area sets the value to that x-fraction. The row's
     own background nudges on hover so the row-as-control reads. */
  .tre__contents {
    list-style: none;
    margin: 0; padding: 0;
    display: flex;
    flex-direction: column;
    gap: 0;
  }
  .tre__row {
    display: flex;
    align-items: center;
    gap: 8px;
    padding: 4px 4px;
    border-bottom: 1px solid rgba(26, 35, 53, 0.55);
  }
  .tre__row:last-child { border-bottom: 0; }
  .tre__row-code {
    flex: 0 0 36px;
    font-family: var(--font-display);
    font-size: 10px;
    letter-spacing: 0.14em;
    color: var(--slice-color);
    text-shadow: 0 0 5px color-mix(in srgb, var(--slice-color) 30%, transparent);
    padding: 1px 4px;
    border-left: 2px solid var(--slice-color);
    background: var(--slice-tint, transparent);
  }
  /* Gauge wrapper clips its own glow halo at the wrapper edge so a
     fully-lit rightmost segment doesn't bleed onto the adjacent
     [0] / [F] peg buttons. The padding gives a few pixels of vertical
     room for the glow inside the wrapper before the clip. */
  .tre__row-gauge {
    flex: 1 1 auto;
    min-width: 0;
    cursor: ew-resize;
    padding: 4px 0;
    overflow: hidden;
  }
  .tre__row-gauge:hover :global(.sg) { box-shadow: inset 0 0 0 1px var(--accent-dim); }

  /* Quick-pegs and amount/mass readouts: tabular numerals + tight padding. */
  .tre__peg {
    flex: 0 0 18px;
    height: 18px;
    background: transparent;
    border: 1px solid var(--line);
    color: var(--fg-dim);
    font-family: var(--font-display);
    font-size: 10px;
    line-height: 1;
    letter-spacing: 0.06em;
    cursor: pointer;
    transition: color 160ms ease, border-color 160ms ease;
  }
  .tre__peg:hover {
    color: var(--accent);
    border-color: var(--accent-dim);
  }
  .tre__row-amount {
    flex: 0 0 auto;
    min-width: 56px;
    text-align: right;
    color: var(--fg);
    font-variant-numeric: tabular-nums;
  }
  .tre__row-amount em {
    font-style: normal; font-size: 9px;
    color: var(--fg-dim); margin-left: 3px; letter-spacing: 0.14em;
  }
  .tre__row-mass {
    flex: 0 0 auto;
    min-width: 54px;
    text-align: right;
    color: var(--accent);
    font-variant-numeric: tabular-nums;
  }
  .tre__row-mass--zero { color: var(--fg-mute); }
  .tre__row-mass em {
    font-style: normal; font-size: 9px;
    color: var(--fg-dim); margin-left: 2px; letter-spacing: 0.14em;
  }

  /* ----- THERMAL section ------------------------------------------
     Per-cryo-slice row: code-tile · segmented-tier-track · readout.
     The track reads as one rigid panel-mount control, not four buttons.
     Active blade lights up in the slice's resource colour; inactive
     blades are dim outlines; over-volume tiers carry a hatched alert
     so the gating is visible at a glance without having to click. */
  .tre__section-sub {
    margin-left: auto;
    font-family: var(--font-display);
    font-size: 9px;
    letter-spacing: 0.18em;
    color: var(--fg-mute);
    text-transform: lowercase;
  }
  .tre__therms {
    list-style: none;
    margin: 0; padding: 0;
    display: flex;
    flex-direction: column;
    gap: 4px;
  }
  /* Each cryo slice's thermal row is a flex column: primary line on
     top (code + track), stats sub-line below. The sub-line carries the
     numerical readout for the committed or hovered tier — moved out of
     a right-side column so the numbers have room to label themselves
     and reads with the visual grammar of ResourceView's boiloff-sub. */
  .tre__therm-row {
    display: flex;
    flex-direction: column;
    gap: 4px;
    padding: 2px 0 4px;
  }
  .tre__therm-head {
    display: flex;
    align-items: center;
    gap: 10px;
    min-height: 36px;
  }

  /* The segmented track frame. Single inset bevel, fixed height; the
     blades inside share a 1 px-line divider to read as one control
     panel rather than discrete buttons. Minimum width gives the four
     blades enough breathing room that "BAC" / "HVY" / "MLI" / "ZBO"
     don't crowd their LEDs or stage pips. */
  .tre__tier-track {
    flex: 1 1 auto;
    min-width: 240px;
    display: flex;
    align-items: stretch;
    height: 34px;
    border: 1px solid var(--line-bright);
    background:
      linear-gradient(180deg, rgba(0, 0, 0, 0.55) 0%, rgba(0, 0, 0, 0.32) 100%);
    box-shadow:
      inset 0 1px 0 rgba(0, 0, 0, 0.55),
      inset 0 -1px 0 rgba(255, 255, 255, 0.025);
    overflow: hidden;
    user-select: none;
  }

  /* Individual tier blade. Each carries a top LED telltale (off / dim
     / lit), the tier label centred, and a stage-pip row at the bottom
     edge. The LED is positioned absolutely from the top inside the
     blade's column so its glow doesn't push surrounding layout.
     Font: var(--font-mono) (Azeret Mono — same font part names use).
     The display font (Unica One) at this size in CEF renders muddy;
     mono + uppercase + tracking gives the same caps treatment with
     much crisper antialiasing. */
  .tre__tier {
    position: relative;
    appearance: none;
    background: transparent;
    border: none;
    border-left: 1px solid var(--line);
    padding: 4px 4px 3px;
    flex: 1 1 0;
    min-width: 56px;
    color: var(--fg-dim);
    font-family: var(--font-mono);
    font-weight: 500;
    font-size: 11px;
    line-height: 1;
    letter-spacing: 0.10em;
    text-transform: uppercase;
    text-align: center;
    -webkit-font-smoothing: antialiased;
    -moz-osx-font-smoothing: grayscale;
    cursor: pointer;
    display: flex;
    flex-direction: column;
    align-items: center;
    justify-content: flex-end;
    gap: 0;
    transition:
      color 200ms cubic-bezier(0.4, 0, 0.2, 1),
      background 200ms cubic-bezier(0.4, 0, 0.2, 1);
    overflow: hidden;
  }
  .tre__tier:first-of-type { border-left: none; }
  .tre__tier:focus-visible {
    outline: 1px solid var(--accent);
    outline-offset: -2px;
  }

  /* LED telltale: a small dot at the very top of the blade. Off-state
     is a dim hollow ring; hover lifts to dim-fill; active fills in the
     slice colour with a glow. */
  .tre__tier-led {
    position: absolute;
    top: 3px;
    left: 50%;
    transform: translateX(-50%);
    width: 6px;
    height: 6px;
    border-radius: 50%;
    background: rgba(0, 0, 0, 0.45);
    border: 1px solid var(--line-bright);
    box-shadow: inset 0 1px 0 rgba(0, 0, 0, 0.45);
    transition:
      background 220ms cubic-bezier(0.4, 0, 0.2, 1),
      border-color 220ms cubic-bezier(0.4, 0, 0.2, 1),
      box-shadow 220ms cubic-bezier(0.4, 0, 0.2, 1);
  }
  .tre__tier:hover:not(:disabled) .tre__tier-led {
    border-color: var(--accent-dim);
  }
  .tre__tier-label {
    /* Pin font defensively — flex-column children in CEF have been
       observed inheriting the parent button's font shorthand in
       surprising ways. */
    font-family: var(--font-mono);
    font-weight: 500;
    letter-spacing: inherit;
    text-transform: uppercase;
    margin-top: 9px;
    line-height: 1;
    color: inherit;
    transition: text-shadow 220ms cubic-bezier(0.4, 0, 0.2, 1);
  }
  /* Stage pips: a tight row at the bottom-right corner. One pip per
     cryocooler stage — empty for passive tiers, single for BAC, double
     for ZBO. Inactive pips are dim outlines, active pips light up in
     the slice colour. */
  .tre__tier-stage {
    position: absolute;
    right: 3px;
    bottom: 2px;
    display: inline-flex;
    gap: 2px;
  }
  .tre__tier-pip {
    width: 3px;
    height: 3px;
    border-radius: 50%;
    background: var(--line-bright);
    transition: background 220ms cubic-bezier(0.4, 0, 0.2, 1),
                box-shadow 220ms cubic-bezier(0.4, 0, 0.2, 1);
  }

  /* Hover (available, not active): wash the blade with a faint
     accent-dim tint and brighten the label. Reads as "this is
     tappable" without competing with the active highlight. */
  .tre__tier:hover:not(:disabled):not(.tre__tier--active) {
    color: var(--fg);
    background: rgba(126, 245, 184, 0.04);
  }

  /* Active blade. Fills in the slice's resource colour at low alpha;
     the LED becomes a lit pip glowing in the same colour. Label
     intensifies and picks up the resource hue with a soft text-shadow.
     A 2-px accent rail at the bottom underlines the choice, anchoring
     it as the committed value (mirrors the contents-row fill-bar
     convention from ResourceView). */
  .tre__tier--active {
    color: var(--slice-color, var(--accent));
    background:
      linear-gradient(180deg,
        color-mix(in srgb, var(--slice-color, var(--accent)) 18%, transparent) 0%,
        color-mix(in srgb, var(--slice-color, var(--accent)) 6%,  transparent) 100%),
      var(--slice-tint, transparent);
    cursor: default;
  }
  .tre__tier--active .tre__tier-label {
    text-shadow: 0 0 6px color-mix(in srgb, var(--slice-color, var(--accent)) 55%, transparent);
  }
  .tre__tier--active .tre__tier-led {
    background:
      radial-gradient(circle at 32% 30%,
        color-mix(in srgb, var(--slice-color, var(--accent)) 30%, white 70%) 0%,
        var(--slice-color, var(--accent)) 60%,
        color-mix(in srgb, var(--slice-color, var(--accent)) 60%, black 40%) 100%);
    border-color: color-mix(in srgb, var(--slice-color, var(--accent)) 60%, white 40%);
    box-shadow:
      0 0 6px color-mix(in srgb, var(--slice-color, var(--accent)) 65%, transparent),
      inset 0 0 0 1px rgba(255, 255, 255, 0.18);
  }
  .tre__tier--active .tre__tier-pip {
    background: var(--slice-color, var(--accent));
    box-shadow: 0 0 4px color-mix(in srgb, var(--slice-color, var(--accent)) 60%, transparent);
  }
  /* Bottom-edge rail anchors the active blade to the track frame —
     subtle horizontal underline in the slice colour. */
  .tre__tier--active::after {
    content: '';
    position: absolute;
    left: 0; right: 0; bottom: 0;
    height: 2px;
    background: var(--slice-color, var(--accent));
    box-shadow: 0 0 6px color-mix(in srgb, var(--slice-color, var(--accent)) 55%, transparent);
    pointer-events: none;
  }

  /* Stats sub-line under each thermal row. CSS grid with named areas
     so geometry is structural (not dependent on browser wrap
     heuristics or font metrics). Row 1 holds the full-width tier
     name, rows 2-3 hold the four stat cells (boil, EC, heat, hw)
     in a 2-column layout. Cell positions are pinned by `grid-area`
     so hover-swapping content can never reflow the layout — only
     the text inside each cell changes. */
  .tre__therm-sub {
    display: grid;
    grid-template-columns: 1fr 1fr;
    grid-template-areas:
      "tier tier"
      "boil ec"
      "heat hw";
    column-gap: 14px;
    row-gap: 3px;
    padding-left: 46px;
    padding-right: 4px;
    font-family: var(--font-mono);
    font-variant-numeric: tabular-nums;
    font-size: 10px;
    line-height: 1.15;
    color: var(--fg-dim);
    -webkit-font-smoothing: antialiased;
    -moz-osx-font-smoothing: grayscale;
  }
  .tre__sub-tier {
    grid-area: tier;
    font-family: var(--font-mono);
    font-weight: 500;
    font-size: 10px;
    letter-spacing: 0.10em;
    text-transform: uppercase;
    color: var(--slice-color, var(--fg-dim));
    text-shadow: 0 0 5px color-mix(in srgb, var(--slice-color, var(--accent)) 22%, transparent);
    transition: color 180ms cubic-bezier(0.4, 0, 0.2, 1),
                text-shadow 180ms cubic-bezier(0.4, 0, 0.2, 1);
  }
  .tre__sub-field--boil { grid-area: boil; }
  .tre__sub-field--ec   { grid-area: ec; }
  .tre__sub-field--heat { grid-area: heat; }
  .tre__sub-field--hw   { grid-area: hw; }
  /* Preview state: tier name swings accent so the player sees at-a-
     glance that this isn't the committed state. */
  .tre__therm-sub--preview .tre__sub-tier {
    color: var(--accent);
    text-shadow: 0 0 6px var(--accent-glow);
  }
  /* Each stat is one field: "LABEL value <unit>". Inline-block + a
     min-width keeps the field's slot constant regardless of which
     number lives there this tick. Pads accommodate the widest value
     each field carries:
       boiloff %/d: "3.000 %/d"     ≈ 60 px
       EC W      : "150 W"          ≈ 42 px
       heat W    : "180 W"          ≈ 42 px
       stores L  : "2,400 L"        ≈ 50 px
       delta L   : "▲ 219 L"        ≈ 60 px (incl. arrow glyph)
     Each value spans (.tre__sub-pct etc.) are inline-block too so
     their internal min-width applies inside the parent field. */
  /* Each stat cell: "LABEL value <unit>". Cells use display: flex so
     the label and value sit on the same baseline. Labels are right-
     aligned in a min-width slot so a hover-swap can't shift the value
     even if the next tier's label is a different length. Values are
     right-aligned so digits column-align across the grid's two cells
     per row. */
  .tre__sub-field {
    display: flex;
    align-items: baseline;
    gap: 6px;
    min-width: 0;
  }
  .tre__sub-label {
    flex: 0 0 32px;
    text-align: right;
    font-family: var(--font-mono);
    font-weight: 500;
    font-size: 9px;
    letter-spacing: 0.08em;
    color: var(--fg-mute);
    text-transform: uppercase;
  }
  .tre__sub-pct,
  .tre__sub-ec,
  .tre__sub-heat,
  .tre__sub-vol {
    flex: 1 1 auto;
    text-align: left;
    min-width: 0;
    white-space: nowrap;
  }
  .tre__sub-pct  { color: var(--warn); }
  .tre__sub-ec   { color: var(--accent); }
  .tre__sub-heat { color: color-mix(in srgb, var(--alert) 70%, var(--warn) 30%); }
  .tre__sub-vol  { color: var(--fg); }
  /* Warn-tint the hardware-cost value when the tier carries a
     non-zero penalty, so it reads as a cost at a glance. */
  .tre__sub-vol--warn { color: var(--warn); }
  .tre__therm-sub em {
    font-style: normal; font-size: 8px;
    color: var(--fg-mute); margin-left: 1px; letter-spacing: 0.12em;
  }

  /* Footer: cumulative mass on the left, REVERT on the right.
     Accentuated divider above so the section reads as a tray. */
  .tre__footer {
    display: flex;
    align-items: center;
    gap: 12px;
    padding-top: 6px;
    border-top: 1px solid var(--line);
  }
  .tre__footer-mass {
    display: inline-flex;
    align-items: baseline;
    gap: 6px;
    flex: 1 1 auto;
  }
  .tre__footer-mass-value {
    font-size: 14px;
    color: var(--accent);
    font-variant-numeric: tabular-nums;
    text-shadow: 0 0 6px var(--accent-glow);
  }
  .tre__footer-mass-value em {
    font-style: normal; font-size: 10px;
    color: var(--fg-dim); margin-left: 3px; letter-spacing: 0.14em;
    text-shadow: none;
  }
  .tre__saving {
    color: var(--accent-dim);
    font-size: 12px;
    letter-spacing: 0.4em;
    animation: tre-saving 1.2s ease-in-out infinite;
  }
  @keyframes tre-saving {
    0%, 100% { opacity: 0.35; }
    50%      { opacity: 1; }
  }
  .tre__revert {
    background: transparent;
    border: 1px solid var(--line);
    color: var(--fg-dim);
    font-family: var(--font-display);
    font-size: 10px;
    letter-spacing: 0.18em;
    padding: 3px 8px;
    cursor: pointer;
    transition: color 160ms ease, border-color 160ms ease, background 160ms ease;
  }
  .tre__revert:hover:not(:disabled) {
    color: var(--warn);
    border-color: var(--warn);
    background: rgba(240, 180, 41, 0.06);
  }
  .tre__revert:disabled { opacity: 0.35; cursor: not-allowed; }

  /* Portal host for the add-resource ContextMenu. Sits at body level
     (via `use:portal`) so the menu's `position: fixed` resolves against
     the viewport. `position: relative` + a high `z-index` creates a new
     stacking context that paints above the FloatingWindow (whose own
     z-index climbs as the user raises it). */
  .tre__menu-portal {
    position: relative;
    z-index: 9999;
  }
</style>
