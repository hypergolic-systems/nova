<script lang="ts">
  // Power tree: Generation / Consumption / Storage as collapsible
  // top-level nodes, each with totals on the right. Solar collapses
  // into its own sub-group inside Generation when more than one
  // panel is present. Storage shows an aggregate gauge between its
  // header and its expanded children, and per-battery rows go
  // double-height so the per-cell gauge gets full row width.
  //
  // Expand/collapse state is in-memory: it resets when the panel is
  // remounted (vessel switch, hud reload). Persisting later is a
  // settings concern, not a view concern.

  import { useNovaPartsByTag } from '../../telemetry/use-nova-parts-by-tag.svelte';
  import type { NovaTaggedPart } from '../../telemetry/use-nova-parts-by-tag.svelte';
  import { NovaPartTopic } from '../../telemetry/nova-topics';
  import type { SolarState } from '../../telemetry/nova-topics';
  import { getKsp, useStageOps } from '@dragonglass/telemetry/svelte';
  import { onDestroy } from 'svelte';
  import ComponentIcon, { type ComponentKind } from '../ComponentIcon.svelte';
  import SegmentGauge from '../SegmentGauge.svelte';

  // Anything magnitudinally below this is "zero" for color purposes.
  // Same threshold fmtRate uses to floor display to "0.00" so the
  // color and the digits agree.
  const RATE_EPSILON = 0.005;
  const isZero = (v: number): boolean => Math.abs(v) < RATE_EPSILON;

  interface Props {
    vesselId: string;
  }
  const { vesselId }: Props = $props();

  const generators = useNovaPartsByTag(() => vesselId, 'power-gen');
  const consumers  = useNovaPartsByTag(() => vesselId, 'power-consume');
  const storage    = useNovaPartsByTag(() => vesselId, 'power-store');

  type NodeKey = 'gen' | 'gen_solar' | 'consume' | 'store';
  let expanded = $state<Record<NodeKey, boolean>>({
    gen: true,
    gen_solar: true,
    consume: true,
    store: true,
  });
  function toggle(k: NodeKey): void {
    expanded[k] = !expanded[k];
  }

  function generationRate(p: NovaTaggedPart): number {
    if (!p.state) return 0;
    let total = 0;
    for (const s of p.state.solar) total += s.rate;
    for (const e of p.state.engine) total += e.alternatorRate;
    return total;
  }

  function consumptionRate(p: NovaTaggedPart): number {
    if (!p.state) return 0;
    let total = 0;
    for (const w of p.state.wheel) total += w.maxEcRate * w.activity;
    for (const l of p.state.light) total += l.maxEcRate * l.activity;
    return total;
  }

  function batteryStored(p: NovaTaggedPart): number {
    if (!p.state) return 0;
    let total = 0;
    for (const b of p.state.battery) total += b.soc * b.capacity;
    return total;
  }

  function batteryCapacity(p: NovaTaggedPart): number {
    if (!p.state) return 0;
    let total = 0;
    for (const b of p.state.battery) total += b.capacity;
    return total;
  }

  function batteryRate(p: NovaTaggedPart): number {
    if (!p.state) return 0;
    let total = 0;
    for (const b of p.state.battery) total += b.rate;
    return total;
  }

  // Per-row icon choice. State-driven when loaded; falls back to the
  // section's dominant kind so first-frame rows aren't iconless.
  function genKind(p: NovaTaggedPart): ComponentKind {
    if (p.state && p.state.engine.length > 0 && p.state.solar.length === 0) return 'engine';
    return 'solar';
  }
  function consumeKind(p: NovaTaggedPart): ComponentKind {
    if (p.state && p.state.light.length > 0 && p.state.wheel.length === 0) return 'light';
    return 'wheel';
  }

  // A part is "solar" for grouping purposes when it carries solar
  // components and no engine alternator. Engine-with-alternator parts
  // stay top-level so they don't get hidden inside a SOLAR header.
  function isSolarPart(p: NovaTaggedPart): boolean {
    return !!p.state && p.state.solar.length > 0 && p.state.engine.length === 0;
  }

  const genGroups = $derived.by(() => {
    const solar: NovaTaggedPart[] = [];
    const other: NovaTaggedPart[] = [];
    for (const p of generators.current) {
      (isSolarPart(p) ? solar : other).push(p);
    }
    const groupSolar = solar.length > 1;
    return {
      solar,
      other,
      groupSolar,
      // When solar isn't grouped, fold it back into the inline list
      // so render order matches the original (solar parts first).
      inline: groupSolar ? other : [...solar, ...other],
      solarTotal: solar.reduce((a, p) => a + generationRate(p), 0),
    };
  });

  const totals = $derived({
    gen: generators.current.reduce((a, p) => a + generationRate(p), 0),
    consume: consumers.current.reduce((a, p) => a + consumptionRate(p), 0),
    stored: storage.current.reduce((a, p) => a + batteryStored(p), 0),
    capacity: storage.current.reduce((a, p) => a + batteryCapacity(p), 0),
    netStorage: storage.current.reduce((a, p) => a + batteryRate(p), 0),
  });

  function fmtRate(value: number): string {
    const abs = Math.abs(value);
    let mag: string;
    if (abs < 0.005) mag = '0.00';
    else if (abs >= 100) mag = abs.toFixed(0);
    else if (abs >= 10) mag = abs.toFixed(1);
    else mag = abs.toFixed(2);
    // Reserve a fixed-width sign slot (NBSP when non-negative) so a
    // value flipping sign doesn't shift everything to its left.
    return (value < 0 ? '-' : ' ') + mag;
  }

  function fmtCapacity(value: number): string {
    return Math.round(value).toString();
  }

  function batteryFraction(stored: number, capacity: number): number {
    return capacity > 0 ? stored / capacity : 0;
  }

  // Hover highlight — reuses Dragonglass's StageTopic.setHighlightParts
  // (same channel the stock staging stack uses) so the 3-D part lights
  // up in-game when the cursor lands on its row. Clear on leave; clear
  // on unmount so the highlight doesn't ghost on after the panel
  // detaches.
  const stageOps = useStageOps();
  function highlightOn(ids: readonly string[]): void {
    stageOps.setHighlightParts(ids);
  }
  function highlightOff(): void {
    stageOps.setHighlightParts([]);
  }
  onDestroy(() => stageOps.setHighlightParts([]));

  // Solar deploy controls. Per-part NovaPartTopic exposes
  // setSolarDeployed(bool) — the mod side resolves the right
  // NovaDeployableSolar module and walks symmetry cousins itself.
  const ksp = getKsp();
  function solarOf(p: NovaTaggedPart): SolarState | undefined {
    return p.state?.solar?.[0];
  }
  function setSolarDeployed(partId: string, deployed: boolean): void {
    ksp.send(NovaPartTopic(partId), 'setSolarDeployed', deployed);
  }
</script>

{#snippet chev(open: boolean)}
  <svg class="pwr__chev" class:pwr__chev--open={open}
       viewBox="0 0 8 8" aria-hidden="true">
    <path d="M2.2 1.4 L5.8 4 L2.2 6.6" fill="none" stroke="currentColor"
          stroke-width="1.25" stroke-linecap="round" stroke-linejoin="round" />
  </svg>
{/snippet}

{#snippet emptyMsg(text: string)}
  <p class="pwr__empty">
    <span class="pwr__empty-rule"></span>
    <span class="pwr__empty-text">{text}</span>
    <span class="pwr__empty-rule"></span>
  </p>
{/snippet}

<!-- Per-row solar deploy control. Renders nothing until the part's
     solar state lands. Once it does:
       deployed=false              → "open" button (chevron-up)
       deployed=true, retractable  → "close" button (chevron-down)
       deployed=true, !retractable → no control (locked open)
     Click stops propagation so the row's hover-highlight handlers
     stay coherent. -->
{#snippet solarControl(p: NovaTaggedPart)}
  {@const s = solarOf(p)}
  {#if s && !s.deployed}
    <button type="button" class="pwr__row-action pwr__row-action--open"
            aria-label="Extend solar panel"
            title="Extend"
            onclick={(e) => { e.stopPropagation(); setSolarDeployed(p.struct.id, true); }}>
      <svg viewBox="0 0 8 8" aria-hidden="true">
        <path d="M1.6 5.2 L4 2.6 L6.4 5.2" fill="none" stroke="currentColor"
              stroke-width="1.25" stroke-linecap="round" stroke-linejoin="round" />
      </svg>
    </button>
  {:else if s && s.deployed && s.retractable}
    <button type="button" class="pwr__row-action pwr__row-action--close"
            aria-label="Retract solar panel"
            title="Retract"
            onclick={(e) => { e.stopPropagation(); setSolarDeployed(p.struct.id, false); }}>
      <svg viewBox="0 0 8 8" aria-hidden="true">
        <path d="M1.6 2.8 L4 5.4 L6.4 2.8" fill="none" stroke="currentColor"
              stroke-width="1.25" stroke-linecap="round" stroke-linejoin="round" />
      </svg>
    </button>
  {/if}
{/snippet}

<section class="pwr">
  <!-- Generation -------------------------------------------------- -->
  <div class="pwr__node">
    <button type="button" class="pwr__node-head"
            aria-expanded={expanded.gen}
            onclick={() => toggle('gen')}>
      {@render chev(expanded.gen)}
      <span class="pwr__node-title">GENERATION</span>
      <span class="pwr__total"
            class:pwr__total--zero={totals.gen <= 0}
            class:pwr__total--hot={totals.gen > 0}>
        {fmtRate(totals.gen)}<em>EC/s</em>
      </span>
    </button>
    {#if expanded.gen}
      {#if generators.current.length === 0}
        {@render emptyMsg('NO GENERATORS')}
      {:else}
        <ul class="pwr__rows">
          {#if genGroups.groupSolar}
            <li class="pwr__subgroup">
              <button type="button" class="pwr__subgroup-head"
                      aria-expanded={expanded.gen_solar}
                      onclick={() => toggle('gen_solar')}
                      onmouseenter={() => highlightOn(genGroups.solar.map(p => p.struct.id))}
                      onmouseleave={highlightOff}>
                {@render chev(expanded.gen_solar)}
                <span class="pwr__row-icon pwr__row-icon--accent">
                  <ComponentIcon kind="solar" />
                </span>
                <span class="pwr__subgroup-title">
                  SOLAR <em>· {genGroups.solar.length}</em>
                </span>
                <span class="pwr__row-rate"
                      class:pwr__row-rate--zero={isZero(genGroups.solarTotal)}>
                  {fmtRate(genGroups.solarTotal)}<em>EC/s</em>
                </span>
              </button>
              {#if expanded.gen_solar}
                <ul class="pwr__rows pwr__rows--nested">
                  {#each genGroups.solar as p (p.struct.id)}
                    {@const s = solarOf(p)}
                    <li class="pwr__row pwr__row--nested"
                        class:pwr__row--closed={s && !s.deployed}
                        onmouseenter={() => highlightOn([p.struct.id])}
                        onmouseleave={highlightOff}>
                      <span class="pwr__row-icon">
                        <ComponentIcon kind="solar" />
                      </span>
                      <span class="pwr__row-name">{p.struct.title}</span>
                      {@render solarControl(p)}
                      <span class="pwr__row-rate"
                            class:pwr__row-rate--zero={isZero(generationRate(p))}>
                        {fmtRate(generationRate(p))}<em>EC/s</em>
                      </span>
                    </li>
                  {/each}
                </ul>
              {/if}
            </li>
          {/if}
          {#each genGroups.inline as p (p.struct.id)}
            {@const isSolar = isSolarPart(p)}
            {@const s = isSolar ? solarOf(p) : undefined}
            <li class="pwr__row"
                class:pwr__row--closed={s && !s.deployed}
                onmouseenter={() => highlightOn([p.struct.id])}
                onmouseleave={highlightOff}>
              <span class="pwr__row-icon">
                <ComponentIcon kind={genKind(p)} />
              </span>
              <span class="pwr__row-name">{p.struct.title}</span>
              {#if isSolar}{@render solarControl(p)}{/if}
              <span class="pwr__row-rate"
                    class:pwr__row-rate--zero={isZero(generationRate(p))}>
                {fmtRate(generationRate(p))}<em>EC/s</em>
              </span>
            </li>
          {/each}
        </ul>
      {/if}
    {/if}
  </div>

  <!-- Consumption ------------------------------------------------- -->
  <div class="pwr__node">
    <button type="button" class="pwr__node-head"
            aria-expanded={expanded.consume}
            onclick={() => toggle('consume')}>
      {@render chev(expanded.consume)}
      <span class="pwr__node-title">CONSUMPTION</span>
      <span class="pwr__total"
            class:pwr__total--zero={totals.consume <= 0}
            class:pwr__total--neg={totals.consume > 0}>
        {fmtRate(totals.consume)}<em>EC/s</em>
      </span>
    </button>
    {#if expanded.consume}
      {#if consumers.current.length === 0}
        {@render emptyMsg('NO CONSUMERS')}
      {:else}
        <ul class="pwr__rows">
          {#each consumers.current as p (p.struct.id)}
            <li class="pwr__row"
                onmouseenter={() => highlightOn([p.struct.id])}
                onmouseleave={highlightOff}>
              <span class="pwr__row-icon">
                <ComponentIcon kind={consumeKind(p)} />
              </span>
              <span class="pwr__row-name">{p.struct.title}</span>
              <span class="pwr__row-rate"
                    class:pwr__row-rate--neg={!isZero(consumptionRate(p))}
                    class:pwr__row-rate--zero={isZero(consumptionRate(p))}>
                {fmtRate(consumptionRate(p))}<em>EC/s</em>
              </span>
            </li>
          {/each}
        </ul>
      {/if}
    {/if}
  </div>

  <!-- Storage ----------------------------------------------------- -->
  <div class="pwr__node">
    <button type="button" class="pwr__node-head"
            aria-expanded={expanded.store}
            onclick={() => toggle('store')}>
      {@render chev(expanded.store)}
      <span class="pwr__node-title">STORAGE</span>
      <span class="pwr__total"
            class:pwr__total--zero={totals.capacity <= 0}
            class:pwr__total--hot={totals.capacity > 0}>
        {fmtCapacity(totals.stored)}/{fmtCapacity(totals.capacity)}
        <em>EC · {fmtRate(totals.netStorage)} EC/s</em>
      </span>
    </button>
    <!-- Aggregate gauge stays visible whether or not the children
         are expanded — it's the at-a-glance "vessel power health"
         line and shouldn't disappear behind a collapsed node. -->
    {#if storage.current.length > 0}
      <div class="pwr__node-gauge">
        <SegmentGauge fraction={batteryFraction(totals.stored, totals.capacity)} />
      </div>
    {/if}
    {#if expanded.store}
      {#if storage.current.length === 0}
        {@render emptyMsg('NO BATTERIES')}
      {:else}
        <ul class="pwr__rows">
          {#each storage.current as p (p.struct.id)}
            <li class="pwr__row pwr__row--storage"
                onmouseenter={() => highlightOn([p.struct.id])}
                onmouseleave={highlightOff}>
              <span class="pwr__row-icon">
                <ComponentIcon kind="battery" />
              </span>
              <div class="pwr__row-stack">
                <div class="pwr__row-line">
                  <span class="pwr__row-name">{p.struct.title}</span>
                  <span class="pwr__row-rate">
                    <span class="pwr__row-rate-stored">{fmtCapacity(batteryStored(p))}</span>
                    <span class="pwr__row-rate-cap">/{fmtCapacity(batteryCapacity(p))}</span>
                    <span class="pwr__row-rate-net"
                          class:pwr__row-rate-net--neg={batteryRate(p) < -RATE_EPSILON}
                          class:pwr__row-rate-net--zero={isZero(batteryRate(p))}>
                      <span class="pwr__row-rate-sep">·</span>{fmtRate(batteryRate(p))}
                    </span>
                  </span>
                </div>
                <div class="pwr__row-line pwr__row-line--gauge">
                  <SegmentGauge
                    fraction={batteryFraction(batteryStored(p), batteryCapacity(p))}
                  />
                </div>
              </div>
            </li>
          {/each}
        </ul>
      {/if}
    {/if}
  </div>
</section>

<style>
  .pwr {
    display: flex;
    flex-direction: column;
    gap: 0;
    /* Reserve room on the left so the section indicator bars (the
       ::before tab markers on each node head) sit just inside the
       panel padding without clipping. */
    padding-left: 4px;
    margin-left: -4px;
  }

  .pwr__node {
    margin-top: 12px;
  }
  .pwr__node:first-child {
    margin-top: 0;
  }

  /* The clickable header strips button chrome and re-adopts the
     section-head visual rhythm, with the chevron leading. The
     ::before pseudo is a left-edge indicator bar — dim while the
     node is expanded (passive "this is open"), bright on hover or
     focus (active "click me"). The :hover state also lifts a
     right-trailing accent wash across the underline so the section
     reads as a tab being entered, not just a button being pressed. */
  .pwr__node-head {
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
    letter-spacing: 0.22em;
    border-bottom: 1px solid var(--line);
    transition: border-color 220ms cubic-bezier(0.4, 0, 0.2, 1);
  }
  /* The double-rule effect — a hairline a couple of pixels below the
     primary border-bottom — recalls the etched lines on real flight
     instruments. Stays at line color so it doesn't compete with
     content; brightens slightly on hover. */
  .pwr__node-head::after {
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
  .pwr__node-head:hover::after,
  .pwr__node-head:focus-visible::after {
    background: linear-gradient(90deg,
      transparent 0%,
      rgba(126, 245, 184, 0.22) 18%,
      rgba(126, 245, 184, 0.22) 82%,
      transparent 100%);
  }

  /* Left-edge indicator bar. */
  .pwr__node-head::before {
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
  .pwr__node-head[aria-expanded='true']::before {
    opacity: 0.45;
    transform: translateY(-50%) scaleY(1);
    background: var(--accent-dim);
  }
  .pwr__node-head:hover::before,
  .pwr__node-head:focus-visible::before {
    opacity: 1;
    transform: translateY(-50%) scaleY(1);
    background: var(--accent);
    box-shadow: 0 0 6px var(--accent-glow);
  }

  .pwr__node-head:hover .pwr__node-title,
  .pwr__node-head:focus-visible .pwr__node-title {
    color: var(--accent-soft);
    text-shadow: 0 0 6px var(--accent-glow);
  }
  .pwr__node-head:focus-visible {
    outline: none;
  }
  .pwr__node-head:hover {
    border-bottom-color: var(--accent-dim);
  }
  .pwr__node-title {
    flex: 1 1 auto;
    font-size: 11px;
    color: var(--fg-dim);
    transition:
      color 220ms cubic-bezier(0.4, 0, 0.2, 1),
      text-shadow 220ms cubic-bezier(0.4, 0, 0.2, 1);
  }

  /* Mechanical chevron — quart-out easing on rotation feels snappier
     than the default ease, and a hint of scale on hover makes it
     read as an actual control rather than passive decoration. */
  .pwr__chev {
    flex: 0 0 8px;
    width: 8px;
    height: 8px;
    color: var(--fg-mute);
    transition:
      transform 280ms cubic-bezier(0.16, 1, 0.3, 1),
      color 220ms cubic-bezier(0.4, 0, 0.2, 1),
      filter 220ms cubic-bezier(0.4, 0, 0.2, 1);
  }
  .pwr__chev--open {
    transform: rotate(90deg);
  }
  .pwr__node-head:hover .pwr__chev,
  .pwr__subgroup-head:hover .pwr__chev {
    color: var(--accent);
    filter: drop-shadow(0 0 3px var(--accent-glow));
  }
  .pwr__node-head:hover .pwr__chev--open,
  .pwr__subgroup-head:hover .pwr__chev--open {
    transform: rotate(90deg) scale(1.18);
  }
  .pwr__node-head:hover .pwr__chev:not(.pwr__chev--open),
  .pwr__subgroup-head:hover .pwr__chev:not(.pwr__chev--open) {
    transform: scale(1.18);
  }

  .pwr__node-gauge {
    margin: 4px 0 8px;
    padding: 0 2px;
  }
  /* The aggregate gauge takes a touch more vertical weight — it's
     the at-a-glance "vessel power health" line and benefits from
     standing slightly taller than the per-row gauges underneath. */
  .pwr__node-gauge :global(.sg) {
    height: 12px;
  }

  .pwr__total {
    flex: 0 0 auto;
    font-size: 11px;
    color: var(--accent);
    font-variant-numeric: tabular-nums;
    transition: color 220ms cubic-bezier(0.4, 0, 0.2, 1),
                text-shadow 220ms cubic-bezier(0.4, 0, 0.2, 1);
  }
  .pwr__total--hot {
    color: var(--accent-soft);
    text-shadow: 0 0 6px var(--accent-glow);
  }
  .pwr__total--neg {
    color: var(--warn);
    text-shadow: 0 0 6px var(--warn-glow);
  }
  .pwr__total--zero {
    color: var(--fg-dim);
    text-shadow: none;
  }
  .pwr__total em {
    font-style: normal;
    font-size: 9px;
    color: var(--fg-dim);
    margin-left: 3px;
    letter-spacing: 0.14em;
    text-shadow: none;
  }

  .pwr__rows {
    list-style: none;
    margin: 0;
    padding: 0;
    display: flex;
    flex-direction: column;
  }
  .pwr__row {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 8px;
    padding: 4px 6px;
    margin: 0 -6px;
    position: relative;
    border-bottom: 1px solid rgba(26, 35, 53, 0.55);
    transition: background 200ms cubic-bezier(0.4, 0, 0.2, 1);
  }
  .pwr__row:last-child {
    border-bottom: 0;
  }
  /* Row-level hover: a faint accent wash, plus a leading 2px accent
     bar that grows in. The bar uses a transform-origin top so it
     wipes downward — small detail but it gives each hover a sense
     of direction rather than just "fade in". */
  .pwr__row::before {
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
  .pwr__row:hover {
    background: rgba(126, 245, 184, 0.04);
  }
  .pwr__row:hover::before {
    opacity: 0.7;
    transform: scaleY(1);
  }

  .pwr__row-icon {
    flex: 0 0 12px;
    display: inline-flex;
    align-items: center;
    color: var(--fg-mute);
    transition: color 220ms cubic-bezier(0.4, 0, 0.2, 1);
  }
  .pwr__row-icon--accent {
    color: var(--accent);
  }
  .pwr__row:hover .pwr__row-icon:not(.pwr__row-icon--accent) {
    color: var(--fg-dim);
  }
  .pwr__row-name {
    flex: 1 1 auto;
    min-width: 0;
    color: var(--fg);
    font-family: var(--font-mono);
    font-size: 11px;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }
  .pwr__row-rate {
    flex: 0 0 auto;
    color: var(--accent);
    font-family: var(--font-mono);
    font-size: 11px;
    font-variant-numeric: tabular-nums;
  }
  .pwr__row-rate--neg {
    color: var(--warn);
  }
  /* Zero rates render in the same dim tone we use for secondary
     metadata — readable, but visibly "off" against the active green
     and warn-orange siblings. */
  .pwr__row-rate--zero {
    color: var(--fg-dim);
  }
  .pwr__row-rate em {
    font-style: normal;
    font-size: 8px;
    color: var(--fg-dim);
    margin-left: 3px;
    letter-spacing: 0.14em;
  }

  /* Storage row rate is composed of three semantically different
     atoms — stored value (primary), capacity divisor (secondary), net
     rate (signed). Splitting them lets each carry its own colour
     instead of inheriting a single tint that misrepresents one of the
     three. The capacity divisor stays at the primary font size — the
     previous `<em>` rendering at 8 px was unreadable next to the
     11 px stored value. */
  .pwr__row-rate-stored {
    color: var(--accent);
  }
  .pwr__row-rate-cap {
    color: var(--fg-dim);
  }
  .pwr__row-rate-sep {
    color: var(--fg-dim);
    margin: 0 4px 0 6px;
    letter-spacing: 0;
  }
  .pwr__row-rate-net {
    color: var(--accent);
  }
  .pwr__row-rate-net--neg {
    color: var(--warn);
  }
  .pwr__row-rate-net--zero {
    color: var(--fg-dim);
  }

  /* Storage rows: double-height so the gauge gets the full row width
     instead of being squeezed beside the name. Icon stays top-aligned
     with the title line. */
  .pwr__row--storage {
    align-items: flex-start;
    padding: 6px 6px;
  }
  .pwr__row--storage .pwr__row-icon {
    margin-top: 1px;
  }
  .pwr__row-stack {
    flex: 1 1 auto;
    min-width: 0;
    display: flex;
    flex-direction: column;
    gap: 5px;
  }
  .pwr__row-line {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 6px;
    min-width: 0;
  }
  .pwr__row-line--gauge {
    /* Gauge fills the line; SegmentGauge is `width: 100%` so it
       stretches to whatever space the stack column gives it. */
    padding: 0 1px;
  }

  /* Solar sub-group: a soft header inside Generation, then panels
     listed beneath at the same row rhythm but indented to advertise
     the hierarchy. The L-bracket left-rule reinforces the tree
     metaphor without needing an explicit connector glyph. */
  .pwr__subgroup {
    list-style: none;
    margin: 0;
    padding: 0;
    border-bottom: 1px solid rgba(26, 35, 53, 0.55);
  }
  .pwr__subgroup-head {
    appearance: none;
    background: transparent;
    border: none;
    color: inherit;
    font: inherit;
    text-align: left;
    cursor: pointer;
    width: 100%;
    display: flex;
    align-items: center;
    gap: 8px;
    padding: 4px 6px;
    margin: 0 -6px;
    position: relative;
    transition: background 200ms cubic-bezier(0.4, 0, 0.2, 1);
  }
  .pwr__subgroup-head::before {
    content: '';
    position: absolute;
    left: 0;
    top: 3px;
    bottom: 3px;
    width: 2px;
    background: var(--accent);
    opacity: 0;
    transform: scaleY(0);
    transform-origin: top;
    transition:
      opacity 220ms cubic-bezier(0.4, 0, 0.2, 1),
      transform 280ms cubic-bezier(0.16, 1, 0.3, 1);
  }
  .pwr__subgroup-head:hover {
    background: rgba(126, 245, 184, 0.04);
  }
  .pwr__subgroup-head:hover::before {
    opacity: 1;
    transform: scaleY(1);
  }
  .pwr__subgroup-head:focus-visible {
    outline: none;
    background: rgba(126, 245, 184, 0.05);
  }
  .pwr__subgroup-title {
    flex: 1 1 auto;
    font-family: var(--font-display);
    font-size: 10px;
    letter-spacing: 0.20em;
    color: var(--fg-dim);
    transition: color 220ms cubic-bezier(0.4, 0, 0.2, 1);
  }
  .pwr__subgroup-head:hover .pwr__subgroup-title {
    color: var(--accent);
  }
  .pwr__subgroup-title em {
    font-style: normal;
    color: var(--fg-dim);
    letter-spacing: 0.14em;
    margin-left: 3px;
  }
  .pwr__rows--nested {
    padding-left: 16px;
    border-left: 1px solid rgba(126, 245, 184, 0.10);
    margin: 0 0 2px 7px;
  }
  .pwr__row--nested {
    padding-left: 4px;
  }
  .pwr__row--nested .pwr__row-name {
    color: var(--fg-dim);
  }

  /* Empty-state line: a tracked-out instrument annotation flanked by
     hairline rules that fade to transparent at the edges. Reads as a
     status callout, not a sentence. */
  .pwr__empty {
    display: flex;
    align-items: center;
    gap: 10px;
    margin: 6px 0;
    padding: 0 4px;
  }
  .pwr__empty-rule {
    flex: 1 1 auto;
    height: 1px;
    background: linear-gradient(90deg,
      transparent 0%,
      rgba(126, 245, 184, 0.10) 50%,
      transparent 100%);
  }
  .pwr__empty-text {
    flex: 0 0 auto;
    font-family: var(--font-display);
    font-size: 9px;
    color: var(--fg-dim);
    letter-spacing: 0.24em;
  }

  /* Per-row solar deploy action. A small bordered cell with the
     chevron glyph; hover lifts to full accent. The row's hover
     wash is still visible behind it. */
  .pwr__row-action {
    appearance: none;
    flex: 0 0 18px;
    width: 18px;
    height: 14px;
    padding: 0;
    margin: 0;
    background: transparent;
    border: 1px solid var(--accent-dim);
    color: var(--fg-dim);
    cursor: pointer;
    display: inline-flex;
    align-items: center;
    justify-content: center;
    border-radius: 1px;
    transition:
      color 200ms cubic-bezier(0.4, 0, 0.2, 1),
      border-color 200ms cubic-bezier(0.4, 0, 0.2, 1),
      background 200ms cubic-bezier(0.4, 0, 0.2, 1),
      box-shadow 200ms cubic-bezier(0.4, 0, 0.2, 1);
  }
  .pwr__row-action svg {
    width: 8px;
    height: 8px;
    display: block;
  }
  .pwr__row-action:hover {
    color: var(--accent);
    border-color: var(--accent);
    background: rgba(126, 245, 184, 0.10);
    box-shadow: 0 0 4px var(--accent-glow);
  }
  .pwr__row-action:focus-visible {
    outline: none;
    border-color: var(--accent);
    color: var(--accent);
  }
  /* The "open" variant calls a little more loudly when its row is
     in the closed (dimmed) state — the user landed on a row that's
     advertising "I'm offline, click here". */
  .pwr__row--closed .pwr__row-action--open {
    color: var(--accent);
    border-color: var(--accent);
  }

  /* Closed solar panel row: dim the text/icon/rate per-element so
     the action button can stay at full strength (it's the only
     interactive surface in a non-functional row). The hover wash
     and indicator bar are still active, since the part is real
     and worth highlighting in the 3-D scene. */
  .pwr__row--closed .pwr__row-icon {
    color: var(--fg-mute);
  }
  .pwr__row--closed .pwr__row-name {
    color: var(--fg-dim);
    font-style: italic;
  }
  .pwr__row--closed .pwr__row-rate {
    color: var(--fg-dim);
  }
  .pwr__row--closed .pwr__row-rate em {
    color: var(--fg-mute);
  }
</style>
