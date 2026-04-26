<script lang="ts">
  // Resource panel — sibling to the Power view, scoped to "what the
  // vessel is carrying" rather than "how the vessel uses it". Two
  // modes share the same visual rhythm:
  //
  //   BY PART (default): each part is a collapsible node; rows show
  //     the resources the part holds with code-tile · gauge · amount.
  //     Resource codes still appear as a trailing summary on collapsed
  //     part headers ("EC · MP") so closed nodes still tell you what's
  //     inside.
  //
  //   BY RESOURCE: each resource is a collapsible node with an
  //     aggregate gauge directly under the header (mirrors Power's
  //     STORAGE node-gauge). Per-part rows beneath show name · gauge ·
  //     amount. Rate appears at the resource header — per-part rate
  //     doesn't physically exist (the LP solves at node level via
  //     crossfeed).
  //
  // The toggle at the top is a single LED-style control; off = parts,
  // on = resources. Collapse state is per-mode and per-key (in-memory,
  // resets on remount — settings persistence is a later concern).

  import { useNovaPartsByTag } from '../../telemetry/use-nova-parts-by-tag.svelte';
  import type { NovaTaggedPart } from '../../telemetry/use-nova-parts-by-tag.svelte';
  import type { NovaResourceFlow } from '../../telemetry/nova-topics';
  import { useStageOps } from '@dragonglass/telemetry/svelte';
  import { onDestroy } from 'svelte';
  import SegmentGauge from '../SegmentGauge.svelte';
  import { resourceCode, resourceSortKey } from './resource-codes';

  // Anything magnitudinally below this is "zero" for color purposes —
  // matches the threshold fmtRate floors to so the digits and tint
  // agree.
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
  // needing to seed entries on each frame. Two records (one per view)
  // so flipping the toggle doesn't carry collapse state across an
  // entirely different tree shape.
  let partCollapsed = $state<Record<string, boolean>>({});
  let resCollapsed = $state<Record<string, boolean>>({});
  function isPartExpanded(id: string): boolean {
    return !partCollapsed[id];
  }
  function isResExpanded(id: string): boolean {
    return !resCollapsed[id];
  }
  function togglePart(id: string): void {
    partCollapsed[id] = !partCollapsed[id];
  }
  function toggleRes(id: string): void {
    resCollapsed[id] = !resCollapsed[id];
  }

  // Filter parts whose state hasn't loaded yet, plus parts whose
  // resources happen to all be zero-capacity (e.g. a battery with
  // capacity 0 in some custom config). Rendering a node-head with
  // nothing under it would just be visual noise.
  const partsWithResources = $derived.by<NovaTaggedPart[]>(() =>
    parts.current.filter((p) => (p.state?.resources?.length ?? 0) > 0),
  );

  // Group resources across the whole vessel. Stable order: EC pinned
  // first, then alphabetical by canonical name. Per-resource entries
  // keep the part-iteration order (which is structure-topic order),
  // so a tank that's listed first on the vessel stays first inside
  // its resource group.
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
    // Whole-unit precision is the common readout convention for
    // resource gauges; anything finer than 1 unit is dust at panel
    // scale and the gauge already shows fill fraction.
    return Math.round(v).toString();
  }

  function fmtRate(value: number): string {
    const abs = Math.abs(value);
    let mag: string;
    if (abs < 0.005) mag = '0.00';
    else if (abs >= 100) mag = abs.toFixed(0);
    else if (abs >= 10) mag = abs.toFixed(1);
    else mag = abs.toFixed(2);
    // Reserve a fixed-width sign slot so a value flipping sign
    // doesn't shift everything to its left.
    return (value < 0 ? '-' : ' ') + mag;
  }

  function fillFraction(amount: number, capacity: number): number {
    return capacity > 0 ? amount / capacity : 0;
  }

  // Hover highlight — same channel Power uses (DG's StageTopic).
  // Always clear on leave; clear on unmount so the highlight doesn't
  // ghost on after the panel detaches.
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

<section class="rsv">
  <!-- Mode toggle. A single click target — the LED indicator and the
       label both flip together. "BY RESOURCE" off = parts list (the
       default); on = resources tree. -->
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
            <span class="rsv__node-title">{p.struct.title}</span>
            <span class="rsv__node-summary">
              {#each p.state!.resources as r, i (r.resourceId)}
                {#if i > 0}<span class="rsv__node-summary-sep">·</span>{/if}
                <span class="rsv__node-summary-code">
                  {resourceCode(r.resourceId)}
                </span>
              {/each}
            </span>
          </button>
          {#if open}
            <ul class="rsv__rows">
              {#each p.state!.resources as r (r.resourceId)}
                {@const frac = fillFraction(r.amount, r.capacity)}
                <li class="rsv__row">
                  <span class="rsv__code">{resourceCode(r.resourceId)}</span>
                  <div class="rsv__row-gauge">
                    <SegmentGauge fraction={frac} />
                  </div>
                  <span class="rsv__amount">
                    <span class="rsv__amount-val">{fmtAmount(r.amount)}</span>
                    <span class="rsv__amount-cap">/{fmtAmount(r.capacity)}</span>
                  </span>
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
            <span class="rsv__code rsv__code--lead">
              {resourceCode(g.resourceId)}
            </span>
            <span class="rsv__node-title">{g.resourceId}</span>
            <span class="rsv__total"
                  class:rsv__total--neg={g.totalRate < -RATE_EPSILON}
                  class:rsv__total--zero={isZero(g.totalRate)}>
              {fmtAmount(g.totalAmount)}<em>/{fmtAmount(g.totalCapacity)}</em>
              <span class="rsv__total-sep">·</span>{fmtRate(g.totalRate)}<em>/s</em>
            </span>
          </button>
          <!-- Aggregate gauge stays visible through collapse — same
               policy as Power's STORAGE node, where the at-a-glance
               vessel-level fill shouldn't disappear behind a closed
               header. -->
          <div class="rsv__node-gauge">
            <SegmentGauge fraction={frac} />
          </div>
          {#if open}
            <ul class="rsv__rows rsv__rows--nested">
              {#each g.entries as e (e.part.struct.id)}
                {@const efrac = fillFraction(e.flow.amount, e.flow.capacity)}
                <li class="rsv__row rsv__row--nested"
                    onmouseenter={() => highlightOn([e.part.struct.id])}
                    onmouseleave={highlightOff}>
                  <span class="rsv__row-name">{e.part.struct.title}</span>
                  <div class="rsv__row-gauge rsv__row-gauge--narrow">
                    <SegmentGauge fraction={efrac} />
                  </div>
                  <span class="rsv__amount">
                    <span class="rsv__amount-val">{fmtAmount(e.flow.amount)}</span>
                    <span class="rsv__amount-cap">/{fmtAmount(e.flow.capacity)}</span>
                  </span>
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
  /* The LED control reads as a single physical switch on a console:
     a recessed dark cell, a glass dome that lights up when on. The
     entire row is the click target — mouse-over brightens the rim of
     the LED and lifts the label tint, so it's clear that *both* the
     glyph and the text are "the button". */
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
  .rsv__opt:hover .rsv__opt-led {
    border-color: var(--accent-dim);
  }
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
  /* Borrowed wholesale from PowerView's pwr__node-head — the dual
     border + accent indicator + chevron rhythm is the recognisable
     subsystem-tree pattern in the panel, and Resources should feel
     like a sibling, not a fresh design. */
  .rsv__node {
    margin-top: 12px;
  }
  .rsv__node:first-of-type {
    margin-top: 0;
  }

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
  /* The "by part" head shows part titles in mono, since they're file-
     style identifiers; the "by resource" head shows resource names in
     display caps, since those are categories. Two minor tweaks for
     the same head element. */
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
  .rsv__node-head:focus-visible {
    outline: none;
  }
  .rsv__node-head:hover {
    border-bottom-color: var(--accent-dim);
  }
  .rsv__node-head:hover .rsv__node-title,
  .rsv__node-head:focus-visible .rsv__node-title {
    color: var(--accent-soft);
    text-shadow: 0 0 6px var(--accent-glow);
  }

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

  /* Trailing summary on BY-PART heads: the part's resource codes,
     comma-flavored, so a collapsed row still tells you what's inside.
     Sits as a faint annotation — definitely metadata, not a primary
     readout. */
  .rsv__node-summary {
    flex: 0 0 auto;
    display: inline-flex;
    align-items: center;
    gap: 0;
    color: var(--fg-mute);
    font-family: var(--font-mono);
    font-size: 9px;
    letter-spacing: 0.08em;
    transition: color 220ms cubic-bezier(0.4, 0, 0.2, 1);
  }
  .rsv__node-head:hover .rsv__node-summary {
    color: var(--fg-dim);
  }
  .rsv__node-summary-code {
    padding: 0 2px;
  }
  .rsv__node-summary-sep {
    color: var(--line);
    margin: 0 1px;
  }

  /* The chevron mirrors Power's — same rotation, same hover scale,
     same drop-shadow on hover. Consistency makes the navigation feel
     like one panel. */
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
  .rsv__chev--open {
    transform: rotate(90deg);
  }
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

  /* Total readout on BY-RESOURCE heads. Three semantically distinct
     atoms — stored value (primary accent), capacity (dim), rate
     (signed) — split so each carries its own colour. The em-tagged
     fragments stay at 9 px metadata size. */
  .rsv__total {
    flex: 0 0 auto;
    color: var(--accent);
    font-family: var(--font-mono);
    font-size: 11px;
    font-variant-numeric: tabular-nums;
  }
  .rsv__total--neg { color: var(--warn); }
  .rsv__total--zero { color: var(--fg-dim); }
  .rsv__total em {
    font-style: normal;
    font-size: 9px;
    color: var(--fg-dim);
    margin-left: 1px;
    letter-spacing: 0.10em;
  }
  .rsv__total-sep {
    color: var(--fg-dim);
    margin: 0 4px;
  }

  /* Aggregate gauge under BY-RESOURCE node-head. Same 12 px treatment
     Power's vessel-power-health bar uses, so the resource-fill
     gestures align across the two panels. */
  .rsv__node-gauge {
    margin: 4px 0 8px;
    padding: 0 2px;
  }
  .rsv__node-gauge :global(.sg) {
    height: 12px;
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
  .rsv__row:last-child {
    border-bottom: 0;
  }
  /* Match Power's leading-edge accent bar on hover — it's the
     repeated motif that says "you're hovering on a real, vessel-
     attached thing". */
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
  .rsv__row:hover {
    background: rgba(126, 245, 184, 0.04);
  }
  .rsv__row:hover::before {
    opacity: 0.7;
    transform: scaleY(1);
  }

  /* Code tile. A fixed-width, tracked-out monogram — reads as a
     stamped or screen-printed label on a panel section. Bordered
     with the line colour and slightly recessed via inset shadow so
     it looks etched into the row. The tile is the row's identity:
     big enough to read at a glance, small enough not to dominate. */
  .rsv__code {
    flex: 0 0 auto;
    min-width: 28px;
    padding: 1px 4px;
    text-align: center;
    color: var(--accent);
    font-family: var(--font-display);
    font-size: 9px;
    letter-spacing: 0.14em;
    line-height: 12px;
    border: 1px solid var(--line);
    background: rgba(126, 245, 184, 0.04);
    box-shadow: inset 0 1px 0 rgba(0, 0, 0, 0.35);
    border-radius: 1px;
    font-variant-numeric: tabular-nums;
    transition:
      color 220ms cubic-bezier(0.4, 0, 0.2, 1),
      border-color 220ms cubic-bezier(0.4, 0, 0.2, 1),
      background 220ms cubic-bezier(0.4, 0, 0.2, 1);
  }
  .rsv__row:hover .rsv__code {
    border-color: var(--accent-dim);
    background: rgba(126, 245, 184, 0.08);
  }
  /* Lead variant: appears in BY-RESOURCE node-heads, where it sits
     immediately after the chevron. No row-hover context to inherit
     from, so a touch more presence — accent border at rest. */
  .rsv__code--lead {
    border-color: var(--accent-dim);
    background: rgba(126, 245, 184, 0.06);
  }

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
  /* In BY-RESOURCE rows the part name takes the flex column, so the
     gauge gets a fixed narrower width — tight enough to leave room
     for the name, wide enough that you can still read fill at a
     glance. */
  .rsv__row-gauge--narrow {
    flex: 0 0 80px;
  }

  /* Amount column. Right-aligned, tabular-nums, two parts —
     primary (accent) and capacity (dim). The slash sits with the
     capacity since visually it belongs to "/ N". */
  .rsv__amount {
    flex: 0 0 auto;
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

  /* Nested rows under BY-RESOURCE node-heads. Same indentation +
     left-rule as Power's solar sub-group nested rows, so the tree
     hierarchy reads identically across the two panels. */
  .rsv__rows--nested {
    padding-left: 16px;
    border-left: 1px solid rgba(126, 245, 184, 0.10);
    margin: 0 0 2px 7px;
  }
  .rsv__row--nested {
    padding-left: 4px;
  }
  .rsv__row--nested .rsv__row-name {
    color: var(--fg-dim);
  }

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
