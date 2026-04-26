<script lang="ts">
  // Resource panel — sibling to the Power view, scoped to "what the
  // vessel is carrying" rather than "how the vessel uses it". Two
  // modes share the same visual rhythm:
  //
  //   BY PART (default): each part is a collapsible node with a
  //     leading kind icon (battery / tank). Rows under it list the
  //     resources the part holds: code-tile · gauge · amount/cap·unit.
  //
  //   BY RESOURCE: each resource is a collapsible node with a leading
  //     code tile and a full-width aggregate gauge. The aggregate
  //     rate sits on the gauge line so the title row stays calm. Per-
  //     part rows beneath show kind-icon · part name · gauge · amount.
  //
  // Per-resource tinting: each resource has a curated hue (LF amber,
  // OX cyan, EC green, MP magenta, etc.) applied to the code tile and
  // the gauge's OK colour via CSS custom properties. Severity (low
  // fill) still flips to warn/alert across all resources — identity
  // hues never override safety tints.
  //
  // Per-part rate is intentionally omitted: the LP solves at node
  // level via crossfeed, so attributing a rate to any single tank is
  // misleading. Aggregate rate appears once at the resource level
  // where it physically maps to the LP solution.

  import { useNovaPartsByTag } from '../../telemetry/use-nova-parts-by-tag.svelte';
  import type { NovaTaggedPart } from '../../telemetry/use-nova-parts-by-tag.svelte';
  import type { NovaResourceFlow } from '../../telemetry/nova-topics';
  import { useStageOps } from '@dragonglass/telemetry/svelte';
  import { onDestroy } from 'svelte';
  import SegmentGauge from '../SegmentGauge.svelte';
  import ComponentIcon, { type ComponentKind } from '../ComponentIcon.svelte';
  import { resourceMeta, resourceSortKey } from './resource-codes';

  const RATE_EPSILON = 0.005;
  const isZero = (v: number): boolean => Math.abs(v) < RATE_EPSILON;

  interface Props {
    vesselId: string;
  }
  const { vesselId }: Props = $props();

  const parts = useNovaPartsByTag(() => vesselId, 'storage');

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

  const partsWithResources = $derived.by<NovaTaggedPart[]>(() =>
    parts.current.filter((p) => (p.state?.resources?.length ?? 0) > 0),
  );

  // Pick a kind icon for the part. Battery if its only resource is
  // electric charge (Z-100s, the EC slot of a probe core); tank for
  // anything else (fuel tanks, monoprop pods, command pods that hold
  // fuel as well as EC). Falls back to tank during the brief window
  // where state hasn't loaded — better than no icon, and correct for
  // the common "has fuel" case.
  function partKind(p: NovaTaggedPart): ComponentKind {
    const rs = p.state?.resources;
    if (!rs || rs.length === 0) return 'tank';
    for (const r of rs) {
      if (r.resourceId !== 'Electric Charge' && r.resourceId !== 'ElectricCharge') {
        return 'tank';
      }
    }
    return 'battery';
  }

  interface ResourceGroup {
    resourceId: string;
    entries: { part: NovaTaggedPart; flow: NovaResourceFlow }[];
    totalAmount: number;
    totalCapacity: number;
    totalRate: number;
  }
  const resourceGroups = $derived.by<ResourceGroup[]>(() => {
    const groups = new Map<string, ResourceGroup>();
    for (const p of partsWithResources) {
      for (const flow of p.state!.resources) {
        let g = groups.get(flow.resourceId);
        if (!g) {
          g = {
            resourceId: flow.resourceId,
            entries: [],
            totalAmount: 0,
            totalCapacity: 0,
            totalRate: 0,
          };
          groups.set(flow.resourceId, g);
        }
        g.entries.push({ part: p, flow });
        g.totalAmount += flow.amount;
        g.totalCapacity += flow.capacity;
        g.totalRate += flow.rate;
      }
    }
    return Array.from(groups.values()).sort((a, b) => {
      const [ka, na] = resourceSortKey(a.resourceId);
      const [kb, nb] = resourceSortKey(b.resourceId);
      return ka !== kb ? ka - kb : na.localeCompare(nb);
    });
  });

  function fmtAmount(v: number): string {
    return Math.round(v).toString();
  }

  function fmtRate(value: number): string {
    const abs = Math.abs(value);
    let mag: string;
    if (abs < 0.005) mag = '0.00';
    else if (abs >= 100) mag = abs.toFixed(0);
    else if (abs >= 10) mag = abs.toFixed(1);
    else mag = abs.toFixed(2);
    return (value < 0 ? '-' : ' ') + mag;
  }

  const fillFraction = (amount: number, capacity: number): number =>
    capacity > 0 ? amount / capacity : 0;

  // Hover highlight forwards to the same StageTopic channel Power uses.
  const stageOps = useStageOps();
  const highlightOn = (ids: readonly string[]): void =>
    stageOps.setHighlightParts(ids);
  const highlightOff = (): void => stageOps.setHighlightParts([]);
  onDestroy(() => stageOps.setHighlightParts([]));
</script>

{#snippet chev(open: boolean)}
  <svg class="rsv__chev" class:rsv__chev--open={open}
       viewBox="0 0 8 8" aria-hidden="true">
    <path d="M2.2 1.4 L5.8 4 L2.2 6.6" fill="none" stroke="currentColor"
          stroke-width="1.25" stroke-linecap="round" stroke-linejoin="round" />
  </svg>
{/snippet}

{#snippet emptyMsg(text: string)}
  <p class="rsv__empty">
    <span class="rsv__empty-rule"></span>
    <span class="rsv__empty-text">{text}</span>
    <span class="rsv__empty-rule"></span>
  </p>
{/snippet}

{#snippet codeTile(name: string, lead: boolean = false)}
  {@const m = resourceMeta(name)}
  <span
    class="rsv__code"
    class:rsv__code--lead={lead}
    style:--rsv-tile-color={m.color}
    style:--rsv-tile-tint={m.tint}
  >{m.code}</span>
{/snippet}

{#snippet amountReadout(amount: number, capacity: number, unit: string)}
  <span class="rsv__amount">
    <span class="rsv__amount-val">{fmtAmount(amount)}</span><span
      class="rsv__amount-cap">/{fmtAmount(capacity)}</span><span
      class="rsv__amount-unit">{unit}</span>
  </span>
{/snippet}

<section class="rsv">
  <!-- Mode toggle. A single click target — the LED indicator and the
       label both flip together. -->
  <button
    type="button"
    class="rsv__opt"
    class:rsv__opt--on={byResource}
    aria-pressed={byResource}
    onclick={() => (byResource = !byResource)}
  >
    <span class="rsv__opt-led"></span>
    <span class="rsv__opt-label">BY RESOURCE</span>
  </button>

  {#if !byResource}
    <!-- ===== BY PART ============================================== -->
    {#if partsWithResources.length === 0}
      {@render emptyMsg('NO STORAGE')}
    {:else}
      {#each partsWithResources as p (p.struct.id)}
        {@const open = isPartExpanded(p.struct.id)}
        <div class="rsv__node">
          <button
            type="button"
            class="rsv__node-head"
            aria-expanded={open}
            onclick={() => togglePart(p.struct.id)}
            onmouseenter={() => highlightOn([p.struct.id])}
            onmouseleave={highlightOff}
          >
            {@render chev(open)}
            <span class="rsv__node-icon">
              <ComponentIcon kind={partKind(p)} />
            </span>
            <span class="rsv__node-title">{p.struct.title}</span>
            <span class="rsv__node-summary">
              {#each p.state!.resources as r, i (r.resourceId)}
                {#if i > 0}<span class="rsv__node-summary-sep">·</span>{/if}
                <span
                  class="rsv__node-summary-code"
                  style:color={resourceMeta(r.resourceId).color}
                >{resourceMeta(r.resourceId).code}</span>
              {/each}
            </span>
          </button>
          {#if open}
            <ul class="rsv__rows">
              {#each p.state!.resources as r (r.resourceId)}
                {@const m = resourceMeta(r.resourceId)}
                {@const frac = fillFraction(r.amount, r.capacity)}
                <li class="rsv__row">
                  {@render codeTile(r.resourceId)}
                  <div
                    class="rsv__row-gauge"
                    style:--sg-color-tint={m.color}
                    style:--sg-glow-tint={m.glow}
                  >
                    <SegmentGauge fraction={frac} />
                  </div>
                  {@render amountReadout(r.amount, r.capacity, m.unit)}
                </li>
              {/each}
            </ul>
          {/if}
        </div>
      {/each}
    {/if}
  {:else}
    <!-- ===== BY RESOURCE ========================================== -->
    {#if resourceGroups.length === 0}
      {@render emptyMsg('NO RESOURCES')}
    {:else}
      {#each resourceGroups as g (g.resourceId)}
        {@const open = isResExpanded(g.resourceId)}
        {@const m = resourceMeta(g.resourceId)}
        {@const frac = fillFraction(g.totalAmount, g.totalCapacity)}
        {@const groupIds = g.entries.map((e) => e.part.struct.id)}
        <div class="rsv__node">
          <button
            type="button"
            class="rsv__node-head rsv__node-head--res"
            aria-expanded={open}
            onclick={() => toggleRes(g.resourceId)}
            onmouseenter={() => highlightOn(groupIds)}
            onmouseleave={highlightOff}
          >
            {@render chev(open)}
            {@render codeTile(g.resourceId, true)}
            <span class="rsv__node-title">{g.resourceId}</span>
            {@render amountReadout(g.totalAmount, g.totalCapacity, m.unit)}
          </button>
          <!-- Aggregate gauge gets its own line with the rate readout
               beside it. Keeps the head row's title from competing
               with the rate; pairs the gauge (which IS the live-flow
               visualisation) directly with the rate it shows. -->
          <div class="rsv__node-gauge-line">
            <div
              class="rsv__node-gauge"
              style:--sg-color-tint={m.color}
              style:--sg-glow-tint={m.glow}
            >
              <SegmentGauge fraction={frac} />
            </div>
            <span class="rsv__rate"
                  class:rsv__rate--neg={g.totalRate < -RATE_EPSILON}
                  class:rsv__rate--zero={isZero(g.totalRate)}>
              {fmtRate(g.totalRate)}<em>{m.rateUnit}</em>
            </span>
          </div>
          {#if open}
            <ul class="rsv__rows rsv__rows--nested">
              {#each g.entries as e (e.part.struct.id)}
                {@const efrac = fillFraction(e.flow.amount, e.flow.capacity)}
                <li class="rsv__row rsv__row--nested rsv__row--stacked"
                    onmouseenter={() => highlightOn([e.part.struct.id])}
                    onmouseleave={highlightOff}>
                  <span class="rsv__row-icon">
                    <ComponentIcon kind={partKind(e.part)} />
                  </span>
                  <div class="rsv__row-stack">
                    <!-- Per-part readout: stored / capacity · rate.
                         Unit is implicit from the parent resource
                         header (e.g. "ELECTRIC CHARGE 250/250 J ·
                         0.00 W") so it isn't repeated here — matches
                         PowerView's storage rows for visual rhyme. -->
                    <div class="rsv__row-line">
                      <span class="rsv__row-name">{e.part.struct.title}</span>
                      <span class="rsv__row-readout">
                        <span class="rsv__row-readout-val">{fmtAmount(e.flow.amount)}</span><span
                          class="rsv__row-readout-cap">/{fmtAmount(e.flow.capacity)}</span>
                        <span
                          class="rsv__row-readout-rate"
                          class:rsv__row-readout-rate--neg={e.flow.rate < -RATE_EPSILON}
                          class:rsv__row-readout-rate--zero={isZero(e.flow.rate)}
                        ><span class="rsv__row-readout-sep">·</span>{fmtRate(e.flow.rate)}</span>
                      </span>
                    </div>
                    <div
                      class="rsv__row-line rsv__row-line--gauge"
                      style:--sg-color-tint={m.color}
                      style:--sg-glow-tint={m.glow}
                    >
                      <SegmentGauge fraction={efrac} />
                    </div>
                  </div>
                </li>
              {/each}
            </ul>
          {/if}
        </div>
      {/each}
    {/if}
  {/if}
</section>

<style>
  .rsv {
    display: flex;
    flex-direction: column;
    gap: 0;
    padding-left: 4px;
    margin-left: -4px;
  }

  /* ----- Toggle ---------------------------------------------------- */
  .rsv__opt {
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
  .rsv__opt:hover {
    color: var(--accent);
    border-bottom-color: var(--accent-dim);
  }
  .rsv__opt:focus-visible {
    outline: none;
    border-bottom-color: var(--accent);
  }
  .rsv__opt--on {
    color: var(--accent);
    text-shadow: 0 0 6px var(--accent-glow);
  }
  .rsv__opt-led {
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
  .rsv__opt:hover .rsv__opt-led { border-color: var(--accent-dim); }
  .rsv__opt--on .rsv__opt-led {
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
  .rsv__opt-label {
    flex: 1 1 auto;
    text-align: left;
  }

  /* ----- Tree node ------------------------------------------------- */
  .rsv__node {
    margin-top: 12px;
  }
  .rsv__node:first-of-type { margin-top: 0; }

  .rsv__node-head {
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
  .rsv__node-head:not(.rsv__node-head--res) .rsv__node-title {
    font-family: var(--font-mono);
    letter-spacing: 0.04em;
    text-transform: none;
  }
  .rsv__node-head--res .rsv__node-title {
    text-transform: uppercase;
  }

  .rsv__node-head::after {
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
  .rsv__node-head:hover::after,
  .rsv__node-head:focus-visible::after {
    background: linear-gradient(90deg,
      transparent 0%,
      rgba(126, 245, 184, 0.22) 18%,
      rgba(126, 245, 184, 0.22) 82%,
      transparent 100%);
  }

  .rsv__node-head::before {
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
  .rsv__node-head[aria-expanded='true']::before {
    opacity: 0.45;
    transform: translateY(-50%) scaleY(1);
    background: var(--accent-dim);
  }
  .rsv__node-head:hover::before,
  .rsv__node-head:focus-visible::before {
    opacity: 1;
    transform: translateY(-50%) scaleY(1);
    background: var(--accent);
    box-shadow: 0 0 6px var(--accent-glow);
  }
  .rsv__node-head:focus-visible { outline: none; }
  .rsv__node-head:hover { border-bottom-color: var(--accent-dim); }
  .rsv__node-head:hover .rsv__node-title,
  .rsv__node-head:focus-visible .rsv__node-title {
    color: var(--accent-soft);
    text-shadow: 0 0 6px var(--accent-glow);
  }

  /* Leading kind-icon column on BY-PART node-heads. */
  .rsv__node-icon {
    flex: 0 0 12px;
    display: inline-flex;
    align-items: center;
    color: var(--fg-mute);
    transition: color 220ms cubic-bezier(0.4, 0, 0.2, 1);
  }
  .rsv__node-head:hover .rsv__node-icon { color: var(--fg-dim); }

  .rsv__node-title {
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
  .rsv__node-summary {
    flex: 0 0 auto;
    display: inline-flex;
    align-items: center;
    color: var(--fg-mute);
    font-family: var(--font-mono);
    font-size: 9px;
    letter-spacing: 0.10em;
  }
  .rsv__node-summary-code {
    padding: 0 3px;
    opacity: 0.85;
  }
  .rsv__node-summary-sep {
    color: var(--line);
    margin: 0 1px;
  }

  .rsv__chev {
    flex: 0 0 8px;
    width: 8px;
    height: 8px;
    color: var(--fg-mute);
    transition:
      transform 280ms cubic-bezier(0.16, 1, 0.3, 1),
      color 220ms cubic-bezier(0.4, 0, 0.2, 1),
      filter 220ms cubic-bezier(0.4, 0, 0.2, 1);
  }
  .rsv__chev--open { transform: rotate(90deg); }
  .rsv__node-head:hover .rsv__chev {
    color: var(--accent);
    filter: drop-shadow(0 0 3px var(--accent-glow));
  }
  .rsv__node-head:hover .rsv__chev--open {
    transform: rotate(90deg) scale(1.18);
  }
  .rsv__node-head:hover .rsv__chev:not(.rsv__chev--open) {
    transform: scale(1.18);
  }

  /* ----- Aggregate gauge line (BY-RESOURCE) ----------------------- */
  /* Flow-rate readout sits right of the gauge. The gauge stretches to
     fill remaining width; the rate column has a fixed slot so the
     gauge ends at the same x across resources. */
  .rsv__node-gauge-line {
    display: flex;
    align-items: center;
    gap: 8px;
    margin: 4px 0 8px;
    padding: 0 2px;
  }
  .rsv__node-gauge {
    flex: 1 1 auto;
    min-width: 0;
  }
  .rsv__node-gauge :global(.sg) {
    height: 12px;
  }
  .rsv__rate {
    flex: 0 0 76px;
    text-align: right;
    color: var(--accent);
    font-family: var(--font-mono);
    font-size: 11px;
    font-variant-numeric: tabular-nums;
    white-space: nowrap;
  }
  .rsv__rate--neg  { color: var(--warn); }
  .rsv__rate--zero { color: var(--fg-dim); }
  .rsv__rate em {
    font-style: normal;
    font-size: 9px;
    color: var(--fg-dim);
    margin-left: 3px;
    letter-spacing: 0.10em;
  }

  /* ----- Rows ------------------------------------------------------ */
  .rsv__rows {
    list-style: none;
    margin: 0;
    padding: 0;
    display: flex;
    flex-direction: column;
  }
  .rsv__row {
    display: flex;
    align-items: center;
    gap: 8px;
    padding: 4px 6px;
    margin: 0 -6px;
    position: relative;
    border-bottom: 1px solid rgba(26, 35, 53, 0.55);
    transition: background 200ms cubic-bezier(0.4, 0, 0.2, 1);
  }
  .rsv__row:last-child { border-bottom: 0; }
  .rsv__row::before {
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
  .rsv__row:hover { background: rgba(126, 245, 184, 0.04); }
  .rsv__row:hover::before { opacity: 0.7; transform: scaleY(1); }

  /* Code tile. Locked to a uniform 36 px width so 2-char (EC) and
     4-char (N2H4) codes align across rows. The fill colour and
     subtle background tint come from --rsv-tile-color /
     --rsv-tile-tint custom props the parent sets per resource. */
  .rsv__code {
    flex: 0 0 36px;
    width: 36px;
    padding: 1px 0;
    text-align: center;
    color: var(--rsv-tile-color, var(--accent));
    background: var(--rsv-tile-tint, rgba(126, 245, 184, 0.04));
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
  .rsv__row:hover .rsv__code,
  .rsv__node-head:hover .rsv__code {
    border-color: color-mix(in srgb, var(--rsv-tile-color, var(--accent)) 40%, transparent);
  }
  /* Lead variant: a touch more presence at rest, since it's headlining
     a section rather than riding inside a row. */
  .rsv__code--lead {
    border-color: color-mix(in srgb, var(--rsv-tile-color, var(--accent)) 35%, transparent);
  }

  /* Leading icon column for BY-RESOURCE part rows. */
  .rsv__row-icon {
    flex: 0 0 12px;
    display: inline-flex;
    align-items: center;
    color: var(--fg-mute);
    transition: color 220ms cubic-bezier(0.4, 0, 0.2, 1);
  }
  .rsv__row:hover .rsv__row-icon { color: var(--fg-dim); }

  .rsv__row-name {
    flex: 1 1 auto;
    min-width: 0;
    color: var(--fg);
    font-family: var(--font-mono);
    font-size: 11px;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .rsv__row-gauge {
    flex: 1 1 auto;
    min-width: 0;
  }
  .rsv__row-gauge--narrow {
    flex: 0 0 80px;
  }

  /* Amount column. Locked to 96 px so 50/50 EC and 1560/1560 L row up
     to the same right edge — the gauges immediately to their left
     therefore end at the same x across siblings. Text inside is
     right-anchored, with the value, capacity, and unit each holding
     their own tint. */
  .rsv__amount {
    flex: 0 0 96px;
    text-align: right;
    font-family: var(--font-mono);
    font-size: 11px;
    font-variant-numeric: tabular-nums;
    white-space: nowrap;
  }
  .rsv__amount-val {
    color: var(--accent);
  }
  .rsv__amount-cap {
    color: var(--fg-dim);
  }
  .rsv__amount-unit {
    margin-left: 4px;
    font-size: 9px;
    color: var(--fg-dim);
    letter-spacing: 0.10em;
  }

  /* Nested rows under BY-RESOURCE node-heads. Same indentation as
     Power's solar sub-group, so the tree hierarchy reads identically
     across panels. */
  .rsv__rows--nested {
    padding-left: 14px;
    border-left: 1px solid rgba(126, 245, 184, 0.10);
    margin: 0 0 2px 7px;
  }
  .rsv__row--nested {
    padding-left: 4px;
  }
  .rsv__row--nested .rsv__row-name {
    color: var(--fg-dim);
  }

  /* Stacked rows: name+amount on top, full-width gauge below. Mirrors
     Power's storage row pattern so part names get full row width
     instead of fighting an inline gauge for space. Icon stays top-
     aligned with the title line. */
  .rsv__row--stacked {
    align-items: flex-start;
    padding: 6px 6px;
  }
  .rsv__row--stacked.rsv__row--nested {
    padding-left: 4px;
  }
  .rsv__row--stacked .rsv__row-icon {
    margin-top: 1px;
  }
  .rsv__row-stack {
    flex: 1 1 auto;
    min-width: 0;
    display: flex;
    flex-direction: column;
    gap: 5px;
  }
  .rsv__row-line {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 6px;
    min-width: 0;
  }
  .rsv__row-line--gauge {
    /* SegmentGauge is `width: 100%`, so it stretches to whatever the
       stack column gives it. */
    padding: 0 1px;
  }

  /* Per-part readout (stored / cap · rate). Three semantic atoms,
     each carrying its own colour: stored value (accent), capacity
     (dim), rate (signed — accent when filling, warn when draining,
     dim at zero). Mirrors Power's storage-row rate splits. */
  .rsv__row-readout {
    flex: 0 0 auto;
    color: var(--accent);
    font-family: var(--font-mono);
    font-size: 11px;
    font-variant-numeric: tabular-nums;
    white-space: nowrap;
  }
  .rsv__row-readout-val { color: var(--accent); }
  .rsv__row-readout-cap { color: var(--fg-dim); }
  .rsv__row-readout-sep {
    color: var(--fg-dim);
    margin: 0 4px 0 6px;
    letter-spacing: 0;
  }
  .rsv__row-readout-rate { color: var(--accent); }
  .rsv__row-readout-rate--neg { color: var(--warn); }
  .rsv__row-readout-rate--zero { color: var(--fg-dim); }

  /* Empty-state line — verbatim Power's pattern for visual rhyme. */
  .rsv__empty {
    display: flex;
    align-items: center;
    gap: 10px;
    margin: 6px 0;
    padding: 0 4px;
  }
  .rsv__empty-rule {
    flex: 1 1 auto;
    height: 1px;
    background: linear-gradient(90deg,
      transparent 0%,
      rgba(126, 245, 184, 0.10) 50%,
      transparent 100%);
  }
  .rsv__empty-text {
    flex: 0 0 auto;
    font-family: var(--font-display);
    font-size: 9px;
    color: var(--fg-dim);
    letter-spacing: 0.24em;
  }
</style>
