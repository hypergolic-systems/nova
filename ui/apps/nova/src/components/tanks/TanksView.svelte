<script lang="ts">
  // Tanks panel — what the vessel is physically carrying in TankVolume
  // hardware. Scoped to tank slices only; Battery EC, FuelCell I/O
  // buffers, and other part-level Buffers live on the panels that own
  // them (PWR / THM). Two modes share the same visual rhythm:
  //
  //   BY PART (default): each tank-bearing part is a collapsible node
  //     with a leading tank icon. Rows beneath aggregate the part's
  //     tank slices per resource: code-tile · stored/cap·unit · rate.
  //
  //   BY RESOURCE: each resource is a collapsible node with a leading
  //     code tile and a full-width aggregate gauge. The aggregate rate
  //     sits on the gauge line so the title row stays calm. Per-part
  //     rows beneath show tank icon · part name · gauge · stored/cap.
  //
  // Per-resource tinting: each resource has a curated hue (LF amber,
  // OX cyan, LH2 ice-blue, N2H4 magenta, etc.) applied to the code
  // tile and the gauge's OK colour via CSS custom properties. Severity
  // (low fill) flips to warn/alert across all resources — identity hues
  // never override safety tints.
  //
  // Per-part rate is the LP's per-resource result on that part — there
  // can be multiple slices of the same resource on a part (mixed
  // loadouts), but the LP solves at node level via crossfeed, so the
  // rate is attached at the resource level, not the slice. We aggregate
  // slices of the same resource within a part and show one row per
  // resource per part with the LP's rate.

  import { useNovaParts } from '../../telemetry/use-nova-parts.svelte';
  import type { NovaPartHandle } from '../../telemetry/use-nova-parts.svelte';
  import type { TankSlice } from '../../telemetry/nova-topics';
  import { NovaPartTopic } from '../../telemetry/nova-topics';
  import { useStageOps, getKsp } from '@dragonglass/telemetry/svelte';
  import { onDestroy } from 'svelte';
  import SegmentGauge from '../SegmentGauge.svelte';
  import ComponentIcon from '../ComponentIcon.svelte';
  import { resourceMeta, resourceSortKey } from '../resource/resource-codes';
  import { siPrefix, fmtMag } from '../../util/units';

  const ksp = getKsp();

  // Tier int → short label. Mirrors `InsulationTier` C# enum order;
  // matches the codes used in TankRowEditor and the THM/PWR views.
  const TIER_LABELS = ['MLI', 'HVY', 'BAC', 'ZBO'] as const;
  function tierLabel(tier: number): string {
    return TIER_LABELS[tier] ?? '?';
  }
  function tankStageLabel(stage: number, maxStage: number): string {
    if (stage === 0) return 'OFF';
    if (maxStage === 1) return 'ON';
    return 'S' + stage;
  }

  // Find the first slice on a part holding the given resource. Returns
  // the tank index too so the cycle handler can rebuild the right
  // stage vector. With multiple tanks holding the same resource (rare
  // — mixed loadouts), the first one wins; the cooling sub-line and
  // its toggle only reflect that slice.
  function partSlice(p: NovaPartHandle, resourceId: string):
      { tankIdx: number; sliceIdx: number; slice: TankSlice } | undefined {
    if (!p.state) return undefined;
    for (let t = 0; t < p.state.tank.length; t++) {
      const tank = p.state.tank[t];
      for (let i = 0; i < tank.slices.length; i++) {
        if (tank.slices[i].resource === resourceId)
          return { tankIdx: t, sliceIdx: i, slice: tank.slices[i] };
      }
    }
    return undefined;
  }

  // Cycle the cooling stage for a specific (part, resource) slice.
  // Reads the current per-slice stage vector for the slice's tank,
  // bumps the matching slot, and ships the whole vector via the topic
  // op (matches the wire contract — full vector, single slot mutated).
  function cycleCoolingFor(p: NovaPartHandle, resourceId: string): void {
    const hit = partSlice(p, resourceId);
    if (!hit || !p.state) return;
    const tank = p.state.tank[hit.tankIdx];
    const next = (hit.slice.stage + 1) % (hit.slice.maxStage + 1);
    const vector = tank.slices.map((s, i) => i === hit.sliceIdx ? next : s.stage);
    ksp.send(NovaPartTopic(p.struct.id), 'setTankCooler', vector);
  }

  const RATE_EPSILON = 0.005;
  const isZero = (v: number): boolean => Math.abs(v) < RATE_EPSILON;

  interface Props {
    vesselId: string;
  }
  const { vesselId }: Props = $props();

  const parts = useNovaParts(() => vesselId);

  let byResource = $state(false);

  // Collapse state. A key absent from the record reads as "expanded"
  // — that way new parts/resources arrive expanded by default without
  // needing to seed entries on each frame.
  let partCollapsed = $state<Record<string, boolean>>({});
  let resCollapsed = $state<Record<string, boolean>>({});
  const isPartExpanded = (id: string): boolean => !partCollapsed[id];
  const isResExpanded = (id: string): boolean => !resCollapsed[id];
  const togglePart = (id: string): void => {
    partCollapsed[id] = !partCollapsed[id];
  };
  const toggleRes = (id: string): void => {
    resCollapsed[id] = !resCollapsed[id];
  };

  const partsWithTanks = $derived.by<NovaPartHandle[]>(() =>
    parts.current.filter((p) => (p.state?.tank.length ?? 0) > 0),
  );

  // Per-part rate lookup. The wire's `resources` array carries the
  // LP's per-resource rate at part level; we cross-reference by
  // resource id rather than re-deriving the rate from slice deltas
  // (which would lose the LP's node-level crossfeed semantics).
  function partResourceRate(p: NovaPartHandle, resourceId: string): number {
    const flow = p.state?.resources.find((r) => r.resourceId === resourceId);
    return flow?.rate ?? 0;
  }

  interface PartTankResource {
    resourceId: string;
    contents: number;
    capacity: number;
    rate: number;
  }

  // Aggregate a part's tank slices by resource. A part with two LH2
  // tanks shows one LH2 row whose stored/cap sums both, and whose rate
  // is the LP's per-resource rate (one rate per resource per part).
  function partTankResources(p: NovaPartHandle): PartTankResource[] {
    const map = new Map<string, PartTankResource>();
    for (const tank of p.state?.tank ?? []) {
      for (const slice of tank.slices) {
        let agg = map.get(slice.resource);
        if (!agg) {
          agg = { resourceId: slice.resource, contents: 0, capacity: 0, rate: 0 };
          map.set(slice.resource, agg);
        }
        agg.contents += slice.contents;
        agg.capacity += slice.capacity;
      }
    }
    for (const agg of map.values()) {
      agg.rate = partResourceRate(p, agg.resourceId);
    }
    return Array.from(map.values()).sort((a, b) => {
      const [ka, na] = resourceSortKey(a.resourceId);
      const [kb, nb] = resourceSortKey(b.resourceId);
      return ka !== kb ? ka - kb : na.localeCompare(nb);
    });
  }

  interface ResourceGroupEntry {
    part: NovaPartHandle;
    contents: number;
    capacity: number;
    rate: number;
  }
  interface ResourceGroup {
    resourceId: string;
    entries: ResourceGroupEntry[];
    totalAmount: number;
    totalCapacity: number;
    totalRate: number;
  }
  const resourceGroups = $derived.by<ResourceGroup[]>(() => {
    const groups = new Map<string, ResourceGroup>();
    for (const p of partsWithTanks) {
      for (const agg of partTankResources(p)) {
        let g = groups.get(agg.resourceId);
        if (!g) {
          g = {
            resourceId: agg.resourceId,
            entries: [],
            totalAmount: 0,
            totalCapacity: 0,
            totalRate: 0,
          };
          groups.set(agg.resourceId, g);
        }
        g.entries.push({
          part: p,
          contents: agg.contents,
          capacity: agg.capacity,
          rate: agg.rate,
        });
        g.totalAmount += agg.contents;
        g.totalCapacity += agg.capacity;
        g.totalRate += agg.rate;
      }
    }
    return Array.from(groups.values()).sort((a, b) => {
      const [ka, na] = resourceSortKey(a.resourceId);
      const [kb, nb] = resourceSortKey(b.resourceId);
      return ka !== kb ? ka - kb : na.localeCompare(nb);
    });
  });

  // Stored / capacity pair, in the resource's SI-prefixed base unit.
  // Both values share the prefix selected from the larger absolute
  // value so the divisor and dividend stay commensurable
  // ("0.05/1.50 kJ" rather than "50 J/1.50 kJ").
  function fmtAmountPair(stored: number, capacity: number, baseUnit: string):
      { sMag: string; cMag: string; unit: string } {
    const p = siPrefix(Math.max(Math.abs(stored), Math.abs(capacity)));
    return {
      sMag: fmtMag(stored / p.div),
      cMag: fmtMag(capacity / p.div),
      unit: p.letter + baseUnit,
    };
  }

  // Sign-free flow-rate readout — direction is encoded by the row's
  // accent (filling) / warn (draining) tint, so the magnitude alone
  // tells the user how fast.
  function fmtRate(value: number, baseUnit: string): { mag: string; unit: string } {
    const p = siPrefix(value);
    return { mag: fmtMag(Math.abs(value) / p.div), unit: p.letter + baseUnit };
  }

  // Aggregate this part's boiloff for a resource across every tank
  // slice it owns. Returns L/d (absolute drain — capacity-weighted) and
  // %/d (capacity-weighted average fraction). The two are independent
  // observables: a small tank with bad insulation can have a high %/d
  // but low absolute L/d, and the user wants both — %/d says "how
  // quickly", L/d says "how much you'll miss".
  function partBoiloff(p: NovaPartHandle, resourceId: string):
      { litresPerDay: number; fractionPerDay: number } {
    let litresPerDay = 0;
    let weightedSum = 0;
    let totalCap = 0;
    for (const tank of p.state?.tank ?? []) {
      for (const slice of tank.slices) {
        if (slice.resource !== resourceId) continue;
        if (slice.boiloffFractionPerDay <= 0) continue;
        const drain = slice.capacity * slice.boiloffFractionPerDay;
        litresPerDay += drain;
        weightedSum += slice.boiloffFractionPerDay * slice.capacity;
        totalCap += slice.capacity;
      }
    }
    return {
      litresPerDay,
      fractionPerDay: totalCap > 0 ? weightedSum / totalCap : 0,
    };
  }

  function groupBoiloff(g: ResourceGroup): { litresPerDay: number; fractionPerDay: number } {
    let litresPerDay = 0;
    let weightedSum = 0;
    let totalCap = 0;
    for (const e of g.entries) {
      const b = partBoiloff(e.part, g.resourceId);
      litresPerDay += b.litresPerDay;
      // Re-weight by the parts' aggregated capacity so the group %/d
      // tracks the actual mass-weighted average across tanks of
      // different sizes.
      for (const tank of e.part.state?.tank ?? []) {
        for (const slice of tank.slices) {
          if (slice.resource !== g.resourceId) continue;
          if (slice.boiloffFractionPerDay <= 0) continue;
          weightedSum += slice.boiloffFractionPerDay * slice.capacity;
          totalCap += slice.capacity;
        }
      }
    }
    return {
      litresPerDay,
      fractionPerDay: totalCap > 0 ? weightedSum / totalCap : 0,
    };
  }

  // Boiloff formatting. The L/d magnitude uses the same SI-prefix
  // ladder as everything else (mL/d for slow drains, L/d for typical
  // cryo, kL/d for cartoonishly large tanks). The %/d is rounded to
  // three decimals so 3 %/d, 0.300 %/d, and 0.030 %/d sit on the same
  // visual column.
  function fmtBoiloff(litresPerDay: number, fractionPerDay: number, baseUnit: string):
      { mag: string; unit: string; pct: string } {
    const p = siPrefix(litresPerDay);
    return {
      mag: fmtMag(litresPerDay / p.div),
      unit: p.letter + baseUnit + '/d',
      pct: (fractionPerDay * 100).toFixed(3) + ' %/d',
    };
  }

  const fillFraction = (amount: number, capacity: number): number =>
    capacity > 0 ? amount / capacity : 0;

  // Severity-aware fill color. Below 10% we tint the underline alert
  // red, below 30% warn amber — matches the SegmentGauge palette so a
  // resource that's running low reads the same way regardless of which
  // visualisation we're using. Otherwise the per-resource hue carries.
  function fillColor(fraction: number, baseColor: string): string {
    if (fraction < 0.10) return 'var(--alert)';
    if (fraction < 0.30) return 'var(--warn)';
    return baseColor;
  }

  // Hover highlight forwards to the same StageTopic channel Power uses.
  const stageOps = useStageOps();
  const highlightOn = (ids: readonly string[]): void =>
    stageOps.setHighlightParts(ids);
  const highlightOff = (): void => stageOps.setHighlightParts([]);
  onDestroy(() => stageOps.setHighlightParts([]));
</script>

{#snippet chev(open: boolean)}
  <svg class="tnk__chev" class:tnk__chev--open={open}
       viewBox="0 0 8 8" aria-hidden="true">
    <path d="M2.2 1.4 L5.8 4 L2.2 6.6" fill="none" stroke="currentColor"
          stroke-width="1.25" stroke-linecap="round" stroke-linejoin="round" />
  </svg>
{/snippet}

<!-- Cooling-system sub-line. Renders only when the slice's tier is
     above the MLI baseline (i.e. there's real cooling hardware
     installed worth telling the player about). For BAC/ZBO it
     additionally exposes the stage cycle button so the player can
     toggle from this view without flipping to PWR or THM. -->
{#snippet coolingSubLine(p: NovaPartHandle, resourceId: string, nested: boolean)}
  {@const hit = partSlice(p, resourceId)}
  {#if hit && hit.slice.tier > 0}
    {@const slice = hit.slice}
    <li class="tnk__sub" class:tnk__sub--nested={nested}
        aria-label="cryocooler stage">
      <span class="tnk__sub-label">cooling</span>
      <span class="tnk__sub-sep">·</span>
      <span class="tnk__cool-tier">{tierLabel(slice.tier)}</span>
      {#if slice.maxStage > 0}
        <button type="button" class="tnk__cool-btn"
                class:tnk__cool-btn--on={slice.stage > 0}
                aria-label="Cycle cryocooler stage"
                title={slice.maxStage === 1
                  ? 'Toggle cryocooler (OFF / ON)'
                  : 'Cycle cryocooler (OFF / S1 / S2)'}
                onclick={(e) => { e.stopPropagation(); cycleCoolingFor(p, resourceId); }}>
          <span>{tankStageLabel(slice.stage, slice.maxStage)}</span>
        </button>
      {/if}
    </li>
  {/if}
{/snippet}

{#snippet emptyMsg(text: string)}
  <p class="tnk__empty">
    <span class="tnk__empty-rule"></span>
    <span class="tnk__empty-text">{text}</span>
    <span class="tnk__empty-rule"></span>
  </p>
{/snippet}

{#snippet codeTile(name: string, lead: boolean = false)}
  {@const m = resourceMeta(name)}
  <span
    class="tnk__code"
    class:tnk__code--lead={lead}
    style:--tnk-tile-color={m.color}
    style:--tnk-tile-tint={m.tint}
  >{m.code}</span>
{/snippet}

{#snippet amountReadout(amount: number, capacity: number, baseUnit: string)}
  {@const ap = fmtAmountPair(amount, capacity, baseUnit)}
  <span class="tnk__amount">
    <span class="tnk__amount-val">{ap.sMag}</span><span
      class="tnk__amount-cap">/{ap.cMag}</span><span
      class="tnk__amount-unit">{ap.unit}</span>
  </span>
{/snippet}

<section class="tnk">
  <!-- Mode toggle. A single click target — the LED indicator and the
       label both flip together. -->
  <button
    type="button"
    class="tnk__opt"
    class:tnk__opt--on={byResource}
    aria-pressed={byResource}
    onclick={() => (byResource = !byResource)}
  >
    <span class="tnk__opt-led"></span>
    <span class="tnk__opt-label">BY RESOURCE</span>
  </button>

  {#if !byResource}
    <!-- ===== BY PART ============================================== -->
    {#if partsWithTanks.length === 0}
      {@render emptyMsg('NO TANKS')}
    {:else}
      {#each partsWithTanks as p (p.struct.id)}
        {@const open = isPartExpanded(p.struct.id)}
        {@const aggs = partTankResources(p)}
        <div class="tnk__node">
          <button
            type="button"
            class="tnk__node-head"
            aria-expanded={open}
            onclick={() => togglePart(p.struct.id)}
            onmouseenter={() => highlightOn([p.struct.id])}
            onmouseleave={highlightOff}
          >
            {@render chev(open)}
            <span class="tnk__node-icon">
              <ComponentIcon kind="tank" />
            </span>
            <span class="tnk__node-title">{p.struct.title}</span>
            <span class="tnk__node-summary">
              {#each aggs as a, i (a.resourceId)}
                {#if i > 0}<span class="tnk__node-summary-sep">·</span>{/if}
                <span
                  class="tnk__node-summary-code"
                  style:color={resourceMeta(a.resourceId).color}
                >{resourceMeta(a.resourceId).code}</span>
              {/each}
            </span>
          </button>
          {#if open}
            <ul class="tnk__rows">
              {#each aggs as a (a.resourceId)}
                {@const m = resourceMeta(a.resourceId)}
                {@const frac = fillFraction(a.contents, a.capacity)}
                {@const ap = fmtAmountPair(a.contents, a.capacity, m.unit)}
                {@const rp = fmtRate(a.rate, m.rateUnit)}
                {@const bo = partBoiloff(p, a.resourceId)}
                <li class="tnk__row"
                    style:--tnk-fill={frac}
                    style:--tnk-fill-color={fillColor(frac, m.color)}>
                  {@render codeTile(a.resourceId)}
                  <span class="tnk__row-spacer"></span>
                  <!-- Self-contained readout: each BY-PART row is its
                       own resource, so units stay attached to amount
                       and rate. -->
                  <span class="tnk__row-readout">
                    <span class="tnk__row-readout-val">{ap.sMag}</span><span
                      class="tnk__row-readout-cap">/{ap.cMag}</span><span
                      class="tnk__row-readout-unit">{ap.unit}</span>
                    <span class="tnk__row-readout-rate"
                          class:tnk__row-readout-rate--neg={a.rate < -RATE_EPSILON}
                          class:tnk__row-readout-rate--zero={isZero(a.rate)}>
                      <span class="tnk__row-readout-sep">·</span>{rp.mag}<em
                        class="tnk__row-readout-rate-unit">{rp.unit}</em>
                    </span>
                  </span>
                </li>
                {@render coolingSubLine(p, a.resourceId, false)}
                {#if bo.litresPerDay > 0}
                  {@const bp = fmtBoiloff(bo.litresPerDay, bo.fractionPerDay, m.unit)}
                  <li class="tnk__sub" aria-label="boil-off rate">
                    <span class="tnk__sub-label">boil-off</span>
                    <span class="tnk__sub-sep">·</span>
                    <span class="tnk__sub-val">{bp.mag}<em class="tnk__sub-unit">{bp.unit}</em></span>
                    <span class="tnk__sub-sep">·</span>
                    <span class="tnk__sub-pct">{bp.pct}</span>
                  </li>
                {/if}
              {/each}
            </ul>
          {/if}
        </div>
      {/each}
    {/if}
  {:else}
    <!-- ===== BY RESOURCE ========================================== -->
    {#if resourceGroups.length === 0}
      {@render emptyMsg('NO TANKS')}
    {:else}
      {#each resourceGroups as g (g.resourceId)}
        {@const open = isResExpanded(g.resourceId)}
        {@const m = resourceMeta(g.resourceId)}
        {@const frac = fillFraction(g.totalAmount, g.totalCapacity)}
        {@const groupIds = g.entries.map((e) => e.part.struct.id)}
        {@const grp = fmtRate(g.totalRate, m.rateUnit)}
        {@const gbo = groupBoiloff(g)}
        <div class="tnk__node">
          <button
            type="button"
            class="tnk__node-head tnk__node-head--res"
            aria-expanded={open}
            onclick={() => toggleRes(g.resourceId)}
            onmouseenter={() => highlightOn(groupIds)}
            onmouseleave={highlightOff}
          >
            {@render chev(open)}
            {@render codeTile(g.resourceId, true)}
            <span class="tnk__node-title">{g.resourceId}</span>
            {@render amountReadout(g.totalAmount, g.totalCapacity, m.unit)}
          </button>
          <!-- Aggregate gauge gets its own line with the rate readout
               beside it. Keeps the head row's title from competing
               with the rate; pairs the gauge (which IS the live-flow
               visualisation) directly with the rate it shows. -->
          <div class="tnk__node-gauge-line">
            <div
              class="tnk__node-gauge"
              style:--sg-color-tint={m.color}
              style:--sg-glow-tint={m.glow}
            >
              <SegmentGauge fraction={frac} />
            </div>
            <span class="tnk__rate"
                  class:tnk__rate--neg={g.totalRate < -RATE_EPSILON}
                  class:tnk__rate--zero={isZero(g.totalRate)}>
              {grp.mag}<em>{grp.unit}</em>
            </span>
          </div>
          {#if gbo.litresPerDay > 0}
            {@const gbp = fmtBoiloff(gbo.litresPerDay, gbo.fractionPerDay, m.unit)}
            <div class="tnk__sub tnk__sub--group" aria-label="boil-off rate">
              <span class="tnk__sub-label">boil-off</span>
              <span class="tnk__sub-sep">·</span>
              <span class="tnk__sub-val">{gbp.mag}<em class="tnk__sub-unit">{gbp.unit}</em></span>
              <span class="tnk__sub-sep">·</span>
              <span class="tnk__sub-pct">{gbp.pct}</span>
            </div>
          {/if}
          {#if open}
            <ul class="tnk__rows tnk__rows--nested">
              {#each g.entries as e (e.part.struct.id)}
                {@const efrac = fillFraction(e.contents, e.capacity)}
                {@const eap = fmtAmountPair(e.contents, e.capacity, m.unit)}
                {@const erp = fmtRate(e.rate, m.rateUnit)}
                {@const ebo = partBoiloff(e.part, g.resourceId)}
                <li class="tnk__row tnk__row--nested"
                    style:--tnk-fill={efrac}
                    style:--tnk-fill-color={fillColor(efrac, m.color)}
                    onmouseenter={() => highlightOn([e.part.struct.id])}
                    onmouseleave={highlightOff}>
                  <span class="tnk__row-icon">
                    <ComponentIcon kind="tank" />
                  </span>
                  <span class="tnk__row-name">{e.part.struct.title}</span>
                  <!-- Unit is implicit from the parent resource header
                       (already shows "LIQUID HYDROGEN 250/250 L · 0.00 L/s")
                       so per-part rows omit it. -->
                  <span class="tnk__row-readout">
                    <span class="tnk__row-readout-val">{eap.sMag}</span><span
                      class="tnk__row-readout-cap">/{eap.cMag}</span>
                    <span class="tnk__row-readout-rate"
                          class:tnk__row-readout-rate--neg={e.rate < -RATE_EPSILON}
                          class:tnk__row-readout-rate--zero={isZero(e.rate)}>
                      <span class="tnk__row-readout-sep">·</span>{erp.mag}
                    </span>
                  </span>
                </li>
                {@render coolingSubLine(e.part, g.resourceId, true)}
                {#if ebo.litresPerDay > 0}
                  {@const ebp = fmtBoiloff(ebo.litresPerDay, ebo.fractionPerDay, m.unit)}
                  <li class="tnk__sub tnk__sub--nested" aria-label="boil-off rate">
                    <span class="tnk__sub-label">boil-off</span>
                    <span class="tnk__sub-sep">·</span>
                    <span class="tnk__sub-val">{ebp.mag}<em class="tnk__sub-unit">{ebp.unit}</em></span>
                    <span class="tnk__sub-sep">·</span>
                    <span class="tnk__sub-pct">{ebp.pct}</span>
                  </li>
                {/if}
              {/each}
            </ul>
          {/if}
        </div>
      {/each}
    {/if}
  {/if}
</section>

<style>
  .tnk {
    display: flex;
    flex-direction: column;
    gap: 0;
    padding-left: 4px;
    margin-left: -4px;
  }

  /* ----- Toggle ---------------------------------------------------- */
  .tnk__opt {
    appearance: none;
    background: transparent;
    border: none;
    padding: 4px 6px;
    margin: 0 -6px 6px;
    width: 100%;
    cursor: pointer;
    display: flex;
    align-items: center;
    gap: 10px;
    color: var(--fg-dim);
    font: inherit;
    font-family: var(--font-display);
    font-size: 10px;
    letter-spacing: 0.20em;
    border-bottom: 1px solid var(--line);
    transition:
      color 220ms cubic-bezier(0.4, 0, 0.2, 1),
      border-color 220ms cubic-bezier(0.4, 0, 0.2, 1);
  }
  .tnk__opt:hover {
    color: var(--accent);
    border-bottom-color: var(--accent-dim);
  }
  .tnk__opt:focus-visible {
    outline: none;
    border-bottom-color: var(--accent);
  }
  .tnk__opt--on {
    color: var(--accent);
    text-shadow: 0 0 6px var(--accent-glow);
  }
  .tnk__opt-led {
    flex: 0 0 8px;
    width: 8px;
    height: 8px;
    border-radius: 50%;
    background: rgba(0, 0, 0, 0.55);
    border: 1px solid var(--line);
    box-shadow:
      inset 0 1px 0 rgba(0, 0, 0, 0.5),
      inset 0 -1px 0 rgba(255, 255, 255, 0.02);
    transition:
      background 240ms cubic-bezier(0.4, 0, 0.2, 1),
      border-color 240ms cubic-bezier(0.4, 0, 0.2, 1),
      box-shadow 240ms cubic-bezier(0.4, 0, 0.2, 1);
  }
  .tnk__opt:hover .tnk__opt-led { border-color: var(--accent-dim); }
  .tnk__opt--on .tnk__opt-led {
    background:
      radial-gradient(circle at 35% 30%,
        color-mix(in srgb, var(--accent) 40%, white 60%) 0%,
        var(--accent) 60%,
        color-mix(in srgb, var(--accent) 70%, black 30%) 100%);
    border-color: color-mix(in srgb, var(--accent) 70%, white 30%);
    box-shadow:
      0 0 8px var(--accent-glow),
      inset 0 0 0 1px rgba(255, 255, 255, 0.15);
  }
  .tnk__opt-label {
    flex: 1 1 auto;
    text-align: left;
  }

  /* ----- Tree node ------------------------------------------------- */
  .tnk__node {
    margin-top: 12px;
  }
  .tnk__node:first-of-type { margin-top: 0; }

  .tnk__node-head {
    appearance: none;
    background: transparent;
    border: none;
    padding: 2px 4px 4px 4px;
    margin: 0 0 4px;
    width: 100%;
    color: inherit;
    font: inherit;
    text-align: left;
    cursor: pointer;
    display: flex;
    align-items: center;
    gap: 8px;
    position: relative;
    font-family: var(--font-display);
    letter-spacing: 0.18em;
    border-bottom: 1px solid var(--line);
    transition: border-color 220ms cubic-bezier(0.4, 0, 0.2, 1);
  }
  .tnk__node-head:not(.tnk__node-head--res) .tnk__node-title {
    font-family: var(--font-mono);
    letter-spacing: 0.04em;
    text-transform: none;
  }
  .tnk__node-head--res .tnk__node-title {
    text-transform: uppercase;
  }

  .tnk__node-head::after {
    content: '';
    position: absolute;
    left: 0;
    right: 0;
    bottom: -3px;
    height: 1px;
    background: linear-gradient(90deg,
      transparent 0%,
      rgba(126, 245, 184, 0.06) 18%,
      rgba(126, 245, 184, 0.06) 82%,
      transparent 100%);
    transition: background 220ms cubic-bezier(0.4, 0, 0.2, 1);
  }
  .tnk__node-head:hover::after,
  .tnk__node-head:focus-visible::after {
    background: linear-gradient(90deg,
      transparent 0%,
      rgba(126, 245, 184, 0.22) 18%,
      rgba(126, 245, 184, 0.22) 82%,
      transparent 100%);
  }

  .tnk__node-head::before {
    content: '';
    position: absolute;
    left: -4px;
    top: 50%;
    height: 70%;
    width: 2px;
    background: var(--accent);
    opacity: 0;
    transform: translateY(-50%) scaleY(0.4);
    transform-origin: center;
    transition:
      opacity 220ms cubic-bezier(0.4, 0, 0.2, 1),
      transform 320ms cubic-bezier(0.16, 1, 0.3, 1),
      box-shadow 220ms cubic-bezier(0.4, 0, 0.2, 1);
  }
  .tnk__node-head[aria-expanded='true']::before {
    opacity: 0.45;
    transform: translateY(-50%) scaleY(1);
    background: var(--accent-dim);
  }
  .tnk__node-head:hover::before,
  .tnk__node-head:focus-visible::before {
    opacity: 1;
    transform: translateY(-50%) scaleY(1);
    background: var(--accent);
    box-shadow: 0 0 6px var(--accent-glow);
  }
  .tnk__node-head:focus-visible { outline: none; }
  .tnk__node-head:hover { border-bottom-color: var(--accent-dim); }
  .tnk__node-head:hover .tnk__node-title,
  .tnk__node-head:focus-visible .tnk__node-title {
    color: var(--accent-soft);
    text-shadow: 0 0 6px var(--accent-glow);
  }

  /* Leading kind-icon column on BY-PART node-heads. */
  .tnk__node-icon {
    flex: 0 0 12px;
    display: inline-flex;
    align-items: center;
    color: var(--fg-mute);
    transition: color 220ms cubic-bezier(0.4, 0, 0.2, 1);
  }
  .tnk__node-head:hover .tnk__node-icon { color: var(--fg-dim); }

  .tnk__node-title {
    flex: 1 1 auto;
    min-width: 0;
    font-size: 11px;
    color: var(--fg-dim);
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
    transition:
      color 220ms cubic-bezier(0.4, 0, 0.2, 1),
      text-shadow 220ms cubic-bezier(0.4, 0, 0.2, 1);
  }

  /* Trailing summary on BY-PART heads — the part's resource codes,
     each tinted with its resource hue. Reads as a faint metadata
     stamp so collapsed parts still tell you what's inside. */
  .tnk__node-summary {
    flex: 0 0 auto;
    display: inline-flex;
    align-items: center;
    color: var(--fg-mute);
    font-family: var(--font-mono);
    font-size: 9px;
    letter-spacing: 0.10em;
  }
  .tnk__node-summary-code {
    padding: 0 3px;
    opacity: 0.85;
  }
  .tnk__node-summary-sep {
    color: var(--line);
    margin: 0 1px;
  }

  .tnk__chev {
    flex: 0 0 8px;
    width: 8px;
    height: 8px;
    color: var(--fg-mute);
    transition:
      transform 280ms cubic-bezier(0.16, 1, 0.3, 1),
      color 220ms cubic-bezier(0.4, 0, 0.2, 1),
      filter 220ms cubic-bezier(0.4, 0, 0.2, 1);
  }
  .tnk__chev--open { transform: rotate(90deg); }
  .tnk__node-head:hover .tnk__chev {
    color: var(--accent);
    filter: drop-shadow(0 0 3px var(--accent-glow));
  }
  .tnk__node-head:hover .tnk__chev--open {
    transform: rotate(90deg) scale(1.18);
  }
  .tnk__node-head:hover .tnk__chev:not(.tnk__chev--open) {
    transform: scale(1.18);
  }

  /* ----- Aggregate gauge line (BY-RESOURCE) ----------------------- */
  /* Flow-rate readout sits right of the gauge. The gauge stretches to
     fill remaining width; the rate column has a fixed slot so the
     gauge ends at the same x across resources. */
  .tnk__node-gauge-line {
    display: flex;
    align-items: center;
    gap: 8px;
    margin: 4px 0 8px;
    padding: 0 2px;
  }
  .tnk__node-gauge {
    flex: 1 1 auto;
    min-width: 0;
  }
  .tnk__node-gauge :global(.sg) {
    height: 12px;
  }
  .tnk__rate {
    flex: 0 0 76px;
    text-align: right;
    color: var(--accent);
    font-family: var(--font-mono);
    font-size: 11px;
    font-variant-numeric: tabular-nums;
    white-space: nowrap;
  }
  .tnk__rate--neg  { color: var(--warn); }
  .tnk__rate--zero { color: var(--fg-dim); }
  .tnk__rate em {
    font-style: normal;
    font-size: 9px;
    color: var(--fg-dim);
    margin-left: 3px;
    letter-spacing: 0.10em;
  }

  /* ----- Rows ------------------------------------------------------ */
  .tnk__rows {
    list-style: none;
    margin: 0;
    padding: 0;
    display: flex;
    flex-direction: column;
  }
  .tnk__row {
    display: flex;
    align-items: center;
    gap: 8px;
    padding: 4px 6px;
    margin: 0 -6px;
    position: relative;
    border-bottom: 1px solid rgba(26, 35, 53, 0.55);
    transition: background 200ms cubic-bezier(0.4, 0, 0.2, 1);
  }
  .tnk__row:last-child { border-bottom: 0; }
  .tnk__row::before {
    content: '';
    position: absolute;
    left: 0;
    top: 2px;
    bottom: 2px;
    width: 2px;
    background: var(--accent);
    opacity: 0;
    transform: scaleY(0);
    transform-origin: top;
    transition:
      opacity 220ms cubic-bezier(0.4, 0, 0.2, 1),
      transform 280ms cubic-bezier(0.16, 1, 0.3, 1);
  }
  .tnk__row:hover { background: rgba(126, 245, 184, 0.04); }
  .tnk__row:hover::before { opacity: 0.7; transform: scaleY(1); }

  /* Fill underline: a 2 px bar at the row's bottom edge whose width
     scales with `--tnk-fill` (0..1) and whose colour comes from
     `--tnk-fill-color` (per-resource hue, with severity overrides for
     low-fill rows). Replaces the per-row segment gauge — the row IS
     the gauge now. Width transitions smoothly so a draining tank
     animates rather than steps. */
  .tnk__row::after {
    content: '';
    position: absolute;
    left: 0;
    bottom: -1px;
    height: 2px;
    width: calc(var(--tnk-fill, 0) * 100%);
    max-width: 100%;
    background: var(--tnk-fill-color, var(--accent));
    opacity: 0.85;
    pointer-events: none;
    transition:
      width 360ms cubic-bezier(0.16, 1, 0.3, 1),
      background 220ms cubic-bezier(0.4, 0, 0.2, 1);
  }
  .tnk__row:hover::after { opacity: 1; }

  /* Pushes the readout to the right edge of BY-PART rows (no name
     column, just code + readout). */
  .tnk__row-spacer {
    flex: 1 1 auto;
    min-width: 0;
  }

  /* Code tile. Locked to a uniform 36 px width so 2-char (EC) and
     4-char (N2H4) codes align across rows. The fill colour and
     subtle background tint come from --tnk-tile-color /
     --tnk-tile-tint custom props the parent sets per resource. */
  .tnk__code {
    flex: 0 0 36px;
    width: 36px;
    padding: 1px 0;
    text-align: center;
    color: var(--tnk-tile-color, var(--accent));
    background: var(--tnk-tile-tint, rgba(126, 245, 184, 0.04));
    font-family: var(--font-display);
    font-size: 9px;
    letter-spacing: 0.12em;
    line-height: 12px;
    border: 1px solid var(--line);
    box-shadow: inset 0 1px 0 rgba(0, 0, 0, 0.35);
    border-radius: 1px;
    font-variant-numeric: tabular-nums;
    transition: border-color 220ms cubic-bezier(0.4, 0, 0.2, 1);
  }
  .tnk__row:hover .tnk__code,
  .tnk__node-head:hover .tnk__code {
    border-color: color-mix(in srgb, var(--tnk-tile-color, var(--accent)) 40%, transparent);
  }
  /* Lead variant: a touch more presence at rest, since it's headlining
     a section rather than riding inside a row. */
  .tnk__code--lead {
    border-color: color-mix(in srgb, var(--tnk-tile-color, var(--accent)) 35%, transparent);
  }

  /* Leading icon column for BY-RESOURCE part rows. */
  .tnk__row-icon {
    flex: 0 0 12px;
    display: inline-flex;
    align-items: center;
    color: var(--fg-mute);
    transition: color 220ms cubic-bezier(0.4, 0, 0.2, 1);
  }
  .tnk__row:hover .tnk__row-icon { color: var(--fg-dim); }

  .tnk__row-name {
    flex: 1 1 auto;
    min-width: 0;
    color: var(--fg);
    font-family: var(--font-mono);
    font-size: 11px;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  /* Amount readout (used by the BY-RESOURCE node-head aggregate row).
     Locked to 96 px right-aligned so totals across resources line up
     to the same edge. Per-part rows use `.tnk__row-readout` instead,
     which can split rate from amount. */
  .tnk__amount {
    flex: 0 0 96px;
    text-align: right;
    font-family: var(--font-mono);
    font-size: 11px;
    font-variant-numeric: tabular-nums;
    white-space: nowrap;
  }
  .tnk__amount-val {
    color: var(--accent);
  }
  .tnk__amount-cap {
    color: var(--fg-dim);
  }
  .tnk__amount-unit {
    margin-left: 4px;
    font-size: 9px;
    color: var(--fg-dim);
    letter-spacing: 0.10em;
  }

  /* Nested rows under BY-RESOURCE node-heads. Same indentation as
     Power's solar sub-group, so the tree hierarchy reads identically
     across panels. */
  .tnk__rows--nested {
    padding-left: 14px;
    border-left: 1px solid rgba(126, 245, 184, 0.10);
    margin: 0 0 2px 7px;
  }
  .tnk__row--nested {
    padding-left: 4px;
  }
  .tnk__row--nested .tnk__row-name {
    color: var(--fg-dim);
  }

  /* Per-part readout (stored / cap · rate). Three semantic atoms,
     each carrying its own colour: stored value (accent), capacity
     (dim), rate (signed — accent when filling, warn when draining,
     dim at zero). Mirrors Power's storage-row rate splits. */
  .tnk__row-readout {
    flex: 0 0 auto;
    color: var(--accent);
    font-family: var(--font-mono);
    font-size: 11px;
    font-variant-numeric: tabular-nums;
    white-space: nowrap;
  }
  .tnk__row-readout-val { color: var(--accent); }
  .tnk__row-readout-cap { color: var(--fg-dim); }
  .tnk__row-readout-sep {
    color: var(--fg-dim);
    margin: 0 4px 0 6px;
    letter-spacing: 0;
  }
  .tnk__row-readout-rate { color: var(--accent); }
  .tnk__row-readout-rate--neg { color: var(--warn); }
  .tnk__row-readout-rate--zero { color: var(--fg-dim); }
  /* Unit subordinates inside the readout. Used by BY-PART rows where
     the row stands alone; BY-RESOURCE per-part rows omit unit because
     the parent header already declares it. */
  .tnk__row-readout-unit {
    margin-left: 4px;
    font-size: 9px;
    color: var(--fg-dim);
    letter-spacing: 0.10em;
  }
  .tnk__row-readout-rate-unit {
    font-style: normal;
    margin-left: 3px;
    font-size: 9px;
    color: var(--fg-dim);
    letter-spacing: 0.10em;
  }

  /* Boil-off sub-line. Tucked under the relevant resource row (or
     group head); intentionally calm — small, dim, no fill underline,
     no hover affordance — so it reads as a diagnostic annotation
     rather than a tappable row. Only renders when the slice has a
     non-zero boiloffFractionPerDay. */
  .tnk__sub {
    list-style: none;
    display: flex;
    align-items: baseline;
    gap: 4px;
    padding: 1px 6px 3px 44px;       /* 44 px = code-tile (36) + gap (8) — lines the label up under the readout column */
    margin: 0 -6px;
    font-family: var(--font-mono);
    font-size: 10px;
    color: var(--fg-dim);
    font-variant-numeric: tabular-nums;
  }
  .tnk__sub--nested {
    padding-left: 26px;              /* matches `.tnk__row-icon` indent under the BY-RESOURCE nested rows */
  }
  .tnk__sub--group {
    padding: 0 6px 4px 50px;         /* under the chevron + code-tile head, just below the gauge line */
  }
  .tnk__sub-label {
    font-family: var(--font-display);
    font-size: 9px;
    letter-spacing: 0.16em;
    text-transform: uppercase;
    color: var(--warn);
    opacity: 0.85;
  }
  .tnk__sub-val { color: var(--fg); opacity: 0.78; }
  .tnk__sub-unit {
    font-style: normal;
    margin-left: 3px;
    font-size: 9px;
    color: var(--fg-dim);
    letter-spacing: 0.10em;
  }
  .tnk__sub-sep { color: var(--fg-dim); opacity: 0.5; margin: 0 2px; }
  .tnk__sub-pct { color: var(--fg-dim); font-size: 10px; }

  /* Cooling-tier label — installed hardware identifier (HVY/BAC/ZBO).
     Always-on (not a hot signal), so neutral fg + display font. */
  .tnk__cool-tier {
    font-family: var(--font-display);
    font-size: 10px;
    letter-spacing: 0.14em;
    color: var(--fg);
  }
  /* Inline stage toggle — same chip language as PWR/THM cooler-btn,
     dropped into a sub-line so it sits flush with the cooling label.
     Fixed width keeps OFF / ON / S1 / S2 the same footprint. */
  .tnk__cool-btn {
    margin-left: 6px;
    padding: 0 6px;
    width: 38px;
    text-align: center;
    background: transparent;
    border: 1px solid var(--line);
    color: var(--fg-mute);
    font-family: var(--font-display);
    font-size: 9px;
    letter-spacing: 0.12em;
    cursor: pointer;
    transition: color 160ms ease, border-color 160ms ease, background 160ms ease;
  }
  .tnk__cool-btn:hover {
    color: var(--accent);
    border-color: var(--accent-dim);
    background: rgba(126, 245, 184, 0.06);
  }
  .tnk__cool-btn--on {
    color: var(--accent);
    border-color: var(--accent-dim);
    background: rgba(126, 245, 184, 0.08);
  }
  .tnk__cool-btn--on:hover {
    background: rgba(126, 245, 184, 0.16);
    border-color: var(--accent);
    box-shadow: 0 0 4px var(--accent-glow);
  }

  /* Empty-state line — verbatim Power's pattern for visual rhyme. */
  .tnk__empty {
    display: flex;
    align-items: center;
    gap: 10px;
    margin: 6px 0;
    padding: 0 4px;
  }
  .tnk__empty-rule {
    flex: 1 1 auto;
    height: 1px;
    background: linear-gradient(90deg,
      transparent 0%,
      rgba(126, 245, 184, 0.10) 50%,
      transparent 100%);
  }
  .tnk__empty-text {
    flex: 0 0 auto;
    font-family: var(--font-display);
    font-size: 9px;
    color: var(--fg-dim);
    letter-spacing: 0.24em;
  }
</style>
