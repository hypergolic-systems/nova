<script lang="ts">
  // Thermal tree: PRODUCERS (RTGs etc.) and RADIATORS as collapsible
  // top-level nodes. Single-number-per-row convention: every rate is
  // displayed unsigned, color encodes direction (amber = puts load on
  // the cooling system / temperature rising; green = relieves load /
  // cooling). RTG row is multi-line: name + load (line 1); gauge +
  // T / Tmax + dT/dt (line 2); passive rejection as a dim secondary
  // (line 3). Radiator rows are flat — no buffer, deploy toggle for
  // deployable rads.
  //
  // Deploy state for deployable radiators is a logical toggle (no
  // animation yet) — sends `setRadiatorDeployed [bool]` op to
  // NovaPartTopic. Effect is immediate; no pending state needed.

  import { useNovaPartsByTag } from '../../telemetry/use-nova-parts-by-tag.svelte';
  import type { NovaTaggedPart } from '../../telemetry/use-nova-parts-by-tag.svelte';
  import { NovaPartTopic } from '../../telemetry/nova-topics';
  import { getKsp, useStageOps } from '@dragonglass/telemetry/svelte';
  import { onDestroy } from 'svelte';
  import ComponentIcon from '../ComponentIcon.svelte';
  import SegmentGauge from '../SegmentGauge.svelte';
  import { siPrefix, fmtMag } from '../../util/units';

  const RATE_EPSILON = 0.005;
  const TEMP_RATE_EPSILON = 0.001;
  const isZero = (v: number): boolean => Math.abs(v) < RATE_EPSILON;
  const isTempRateZero = (v: number): boolean => Math.abs(v) < TEMP_RATE_EPSILON;

  interface Props {
    vesselId: string;
  }
  const { vesselId }: Props = $props();

  const thermalParts = useNovaPartsByTag(() => vesselId, 'thermal');
  const ksp = getKsp();

  type NodeKey = 'producers' | 'radiators';
  let expanded = $state<Record<NodeKey, boolean>>({
    producers: true,
    radiators: true,
  });
  function toggle(k: NodeKey): void {
    expanded[k] = !expanded[k];
  }

  function isRtgPart(p: NovaTaggedPart): boolean {
    return !!p.state && p.state.rtg.length > 0;
  }
  function isRadiatorPart(p: NovaTaggedPart): boolean {
    return !!p.state && p.state.radiator.length > 0;
  }

  // Aggregate across all RTG components on a part. Stock has one per
  // part; the sum stays correct for hypothetical multi-RTG parts.
  function rtgThermal(p: NovaTaggedPart): {
    heatIn: number; cooling: number; rejection: number;
    tempC: number; maxTempC: number; dTdt: number;
  } {
    let heatIn = 0, cooling = 0, rejection = 0;
    let tempC = 0, maxTempC = 0, dTdt = 0;
    let n = 0;
    if (p.state) {
      for (const r of p.state.rtg) {
        heatIn    += r.wasteHeatW;
        cooling   += r.exportW;
        rejection += r.rejectionW;
        tempC    += r.currentTempC;
        maxTempC += r.maxOperatingTempC;
        dTdt     += r.dTdtCps;
        n++;
      }
    }
    if (n > 1) {
      tempC /= n;
      maxTempC /= n;
      dTdt /= n;
    }
    return { heatIn, cooling, rejection, tempC, maxTempC, dTdt };
  }

  function rtgTempFraction(p: NovaTaggedPart): number {
    const t = rtgThermal(p);
    return t.maxTempC > 0 ? t.tempC / t.maxTempC : 0;
  }

  function radiatorOf(p: NovaTaggedPart): {
    current: number; max: number; deployed: boolean; deployable: boolean;
  } {
    let current = 0, max = 0, deployed = true, deployable = false;
    if (p.state) {
      for (const r of p.state.radiator) {
        current   += r.currentCoolingW;
        max       += r.maxCoolingW;
        if (!r.isDeployed) deployed = false;
        if (r.isDeployable) deployable = true;
      }
    }
    return { current, max, deployed, deployable };
  }

  function setRadiatorDeployed(partId: string, deployed: boolean): void {
    ksp.send(NovaPartTopic(partId), 'setRadiatorDeployed', deployed);
  }

  const groups = $derived.by(() => {
    const producers: NovaTaggedPart[] = [];
    const radiators: NovaTaggedPart[] = [];
    for (const p of thermalParts.current) {
      if (isRtgPart(p)) producers.push(p);
      else if (isRadiatorPart(p)) radiators.push(p);
    }
    return { producers, radiators };
  });

  const totals = $derived({
    // Heat load on the cooling system (sum of waste-heat producers).
    heatLoad: groups.producers.reduce((a, p) => a + rtgThermal(p).heatIn, 0),
    // Active cooling provided by deployed radiators.
    cooling:    groups.radiators.reduce((a, p) => a + radiatorOf(p).current, 0),
    coolingMax: groups.radiators.reduce((a, p) => a + radiatorOf(p).max, 0),
  });

  function fmtRate(value: number): { mag: string; unit: string } {
    const abs = Math.abs(value);
    const p = siPrefix(abs);
    return { mag: fmtMag(abs / p.div), unit: p.letter + 'W' };
  }

  function fmtRatePair(current: number, max: number): { cMag: string; mMag: string; unit: string } {
    const p = siPrefix(Math.max(Math.abs(current), Math.abs(max)));
    return {
      cMag: fmtMag(current / p.div),
      mMag: fmtMag(max / p.div),
      unit: p.letter + 'W',
    };
  }

  function fmtTempC(v: number): string {
    return Math.abs(v) >= 100 ? v.toFixed(0) : v.toFixed(1);
  }

  function fmtRateCps(v: number): string {
    const abs = Math.abs(v);
    if (abs < 0.01)  return v.toFixed(3);
    if (abs < 1)     return v.toFixed(2);
    return v.toFixed(1);
  }

  const heatLoadFmt = $derived(fmtRate(totals.heatLoad));
  const coolingFmt  = $derived(fmtRatePair(totals.cooling, totals.coolingMax));

  const stageOps = useStageOps();
  function highlightOn(ids: readonly string[]): void {
    stageOps.setHighlightParts(ids);
  }
  function highlightOff(): void {
    stageOps.setHighlightParts([]);
  }
  onDestroy(() => stageOps.setHighlightParts([]));
</script>

{#snippet chev(open: boolean)}
  <svg class="thr__chev" class:thr__chev--open={open}
       viewBox="0 0 8 8" aria-hidden="true">
    <path d="M2.2 1.4 L5.8 4 L2.2 6.6" fill="none" stroke="currentColor"
          stroke-width="1.25" stroke-linecap="round" stroke-linejoin="round" />
  </svg>
{/snippet}

{#snippet emptyMsg(text: string)}
  <p class="thr__empty">
    <span class="thr__empty-rule"></span>
    <span class="thr__empty-text">{text}</span>
    <span class="thr__empty-rule"></span>
  </p>
{/snippet}

<!-- Per-radiator deploy toggle. Fixed panels (deployable=false) have
     no control. Deployable rads show EXT when retracted, RET when
     deployed. Effect is immediate; no busy/pending state. -->
{#snippet radiatorControl(p: NovaTaggedPart, r: { deployed: boolean; deployable: boolean })}
  {#if r.deployable}
    {#if !r.deployed}
      <button type="button" class="thr__deploy-btn thr__deploy-btn--ext"
              aria-label="Extend radiator"
              title="Extend radiator"
              onclick={(e) => { e.stopPropagation(); setRadiatorDeployed(p.struct.id, true); }}>
        <span>EXT</span>
      </button>
    {:else}
      <button type="button" class="thr__deploy-btn thr__deploy-btn--ret"
              aria-label="Retract radiator"
              title="Retract radiator"
              onclick={(e) => { e.stopPropagation(); setRadiatorDeployed(p.struct.id, false); }}>
        <span>RET</span>
      </button>
    {/if}
  {/if}
{/snippet}

<section class="thr">
  <!-- Producers ---------------------------------------------------- -->
  <div class="thr__node">
    <button type="button" class="thr__node-head"
            aria-expanded={expanded.producers}
            onclick={() => toggle('producers')}>
      {@render chev(expanded.producers)}
      <span class="thr__node-title">PRODUCERS</span>
      <span class="thr__rate thr__rate--load"
            class:thr__rate--zero={isZero(totals.heatLoad)}>
        {heatLoadFmt.mag}<em>{heatLoadFmt.unit}</em>
      </span>
    </button>
    {#if expanded.producers}
      {#if groups.producers.length === 0}
        {@render emptyMsg('NO HEAT PRODUCERS')}
      {:else}
        <ul class="thr__rows">
          {#each groups.producers as p (p.struct.id)}
            {@const t = rtgThermal(p)}
            {@const loadFmt = fmtRate(t.heatIn)}
            {@const rejFmt  = fmtRate(t.rejection)}
            {@const tempFrac = rtgTempFraction(p)}
            <li class="thr__row thr__row--stack"
                onmouseenter={() => highlightOn([p.struct.id])}
                onmouseleave={highlightOff}>
              <span class="thr__row-icon">
                <ComponentIcon kind="rtg" />
              </span>
              <div class="thr__row-stack">
                <!-- Line 1: name + heat-load number (amber). Single
                     number — represents this part's contribution to
                     the cooling-system load. -->
                <div class="thr__row-line">
                  <span class="thr__row-name">{p.struct.title}</span>
                  <span class="thr__rate thr__rate--load"
                        class:thr__rate--zero={isZero(t.heatIn)}
                        title="Waste heat produced by Pu decay — load on the cooling system">
                    {loadFmt.mag}<em>{loadFmt.unit}</em>
                  </span>
                </div>
                <!-- Line 2: gauge + temperature readout + dT/dt. -->
                <div class="thr__row-line thr__row-line--gauge">
                  <SegmentGauge fraction={tempFrac} />
                  <span class="thr__temp"
                        title="Device temperature / max operating temperature">
                    {fmtTempC(t.tempC)}<em>°C</em><span
                      class="thr__temp-sep">/</span>{fmtTempC(t.maxTempC)}<em>°C</em>
                  </span>
                  <span class="thr__dtdt"
                        class:thr__dtdt--up={t.dTdt > TEMP_RATE_EPSILON}
                        class:thr__dtdt--down={t.dTdt < -TEMP_RATE_EPSILON}
                        class:thr__dtdt--zero={isTempRateZero(t.dTdt)}
                        title="Rate of change of device temperature">
                    {fmtRateCps(Math.abs(t.dTdt))}<em>°C/s</em>
                  </span>
                </div>
                <!-- Line 3: passive rejection as a dim secondary.
                     Green — heat leaving the device to environment.
                     Falls to 0 when the cooling loop covers production. -->
                <div class="thr__row-line thr__row-line--secondary">
                  <span class="thr__rejection-label">passive rejection</span>
                  <span class="thr__rate thr__rate--cool"
                        class:thr__rate--zero={isZero(t.rejection)}
                        title="Body radiation to environment, quantized into 10 tiers (linear in T). Idle when cooling loop covers production.">
                    {rejFmt.mag}<em>{rejFmt.unit}</em>
                  </span>
                </div>
              </div>
            </li>
          {/each}
        </ul>
      {/if}
    {/if}
  </div>

  <!-- Radiators ---------------------------------------------------- -->
  <div class="thr__node">
    <button type="button" class="thr__node-head"
            aria-expanded={expanded.radiators}
            onclick={() => toggle('radiators')}>
      {@render chev(expanded.radiators)}
      <span class="thr__node-title">RADIATORS</span>
      <span class="thr__rate thr__rate--cool"
            class:thr__rate--zero={isZero(totals.cooling)}>
        <span class="thr__rate-cur">{coolingFmt.cMag}</span><span
          class="thr__rate-max">/{coolingFmt.mMag}</span><em>{coolingFmt.unit}</em>
      </span>
    </button>
    {#if expanded.radiators}
      {#if groups.radiators.length === 0}
        {@render emptyMsg('NO RADIATORS')}
      {:else}
        <ul class="thr__rows">
          {#each groups.radiators as p (p.struct.id)}
            {@const r = radiatorOf(p)}
            {@const cFmt = fmtRate(r.current)}
            <li class="thr__row"
                class:thr__row--closed={!r.deployed}
                onmouseenter={() => highlightOn([p.struct.id])}
                onmouseleave={highlightOff}>
              <span class="thr__row-icon">
                <ComponentIcon kind="radiator" />
              </span>
              <span class="thr__row-name">{p.struct.title}</span>
              {@render radiatorControl(p, r)}
              <span class="thr__rate thr__rate--cool"
                    class:thr__rate--zero={isZero(r.current)}
                    title={r.deployed
                      ? 'Cooling provided by this radiator'
                      : 'Retracted — providing no cooling'}>
                {cFmt.mag}<em>{cFmt.unit}</em>
              </span>
            </li>
          {/each}
        </ul>
      {/if}
    {/if}
  </div>
</section>

<style>
  .thr {
    display: flex;
    flex-direction: column;
    gap: 6px;
  }
  .thr__node {
    border: 1px solid var(--line);
    background: var(--bg-elev);
  }
  .thr__node-head {
    display: flex;
    align-items: center;
    gap: 6px;
    width: 100%;
    padding: 4px 8px;
    background: transparent;
    border: 0;
    border-bottom: 1px solid var(--line);
    color: var(--fg-dim);
    font-family: var(--font-display);
    font-size: 11px;
    letter-spacing: 0.14em;
    cursor: pointer;
  }
  .thr__node-title {
    flex: 1 1 auto;
    text-align: left;
  }
  .thr__chev {
    width: 8px;
    height: 8px;
    color: var(--fg-mute);
    transition: transform 160ms ease;
  }
  .thr__chev--open { transform: rotate(90deg); }

  .thr__rows {
    list-style: none;
    margin: 0;
    padding: 0;
  }
  .thr__row {
    display: flex;
    align-items: center;
    gap: 8px;
    padding: 4px 10px;
    border-bottom: 1px solid var(--line-faint, rgba(126, 245, 184, 0.06));
  }
  .thr__row:last-child { border-bottom: 0; }
  .thr__row--closed { color: var(--fg-mute); }
  .thr__row--closed .thr__row-name { color: var(--fg-mute); }

  .thr__row--stack {
    align-items: stretch;
  }
  .thr__row-icon {
    flex: 0 0 auto;
    color: var(--fg-mute);
    align-self: flex-start;
    padding-top: 1px;
  }
  .thr__row-stack {
    flex: 1 1 auto;
    display: flex;
    flex-direction: column;
    gap: 2px;
    min-width: 0;
  }
  .thr__row-line {
    display: flex;
    align-items: center;
    gap: 8px;
    min-width: 0;
  }
  .thr__row-line--gauge {
    gap: 0.5rem;
  }
  .thr__row-line--gauge :global(.sg) {
    flex: 1 1 auto;
  }
  .thr__row-line--secondary {
    opacity: 0.65;
    font-size: 0.85em;
    gap: 6px;
  }
  .thr__rejection-label {
    flex: 1 1 auto;
    color: var(--fg-mute);
    font-style: italic;
    white-space: nowrap;
  }
  .thr__row-name {
    flex: 1 1 auto;
    min-width: 0;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
    color: var(--fg);
  }

  /* Single-number rate readouts. Color encodes direction: amber =
     load on the cooling system (heat being added); green = relief
     (heat being removed). Magnitude only — no signs. */
  .thr__rate {
    flex: 0 0 auto;
    font-variant-numeric: tabular-nums;
  }
  .thr__rate em {
    font-style: normal;
    color: var(--fg-mute);
    margin-left: 1px;
  }
  .thr__rate--load { color: var(--accent-warm, #f0c060); }
  .thr__rate--cool { color: var(--accent); }
  .thr__rate--zero { color: var(--fg-mute) !important; }
  .thr__rate-cur, .thr__rate-max { color: inherit; }
  .thr__rate-max { color: var(--fg-mute); }

  /* Temperature readout — neutral foreground, with separator dimmed
     so the current/max values pop. */
  .thr__temp {
    flex: 0 0 auto;
    font-variant-numeric: tabular-nums;
    color: var(--fg);
    white-space: nowrap;
  }
  .thr__temp em {
    font-style: normal;
    color: var(--fg-mute);
    margin-left: 1px;
  }
  .thr__temp-sep {
    color: var(--fg-mute);
    margin: 0 2px;
  }

  .thr__dtdt {
    flex: 0 0 auto;
    font-variant-numeric: tabular-nums;
    font-size: 0.9em;
    white-space: nowrap;
  }
  .thr__dtdt em {
    font-style: normal;
    color: var(--fg-mute);
    margin-left: 1px;
  }
  .thr__dtdt--up   { color: var(--accent-warm, #f0c060); }
  .thr__dtdt--down { color: var(--accent); }
  .thr__dtdt--zero { color: var(--fg-mute); }

  /* Deploy buttons (radiator EXT/RET). Same visual language as the
     PWR view's solar buttons but local to the THM tab. */
  .thr__deploy-btn {
    flex: 0 0 auto;
    padding: 1px 6px;
    background: transparent;
    border: 1px solid var(--line);
    color: var(--fg-dim);
    font-family: var(--font-display);
    font-size: 10px;
    letter-spacing: 0.12em;
    cursor: pointer;
    transition: color 160ms ease, border-color 160ms ease, background 160ms ease;
  }
  .thr__deploy-btn:hover {
    color: var(--accent);
    border-color: var(--accent-dim);
    background: rgba(126, 245, 184, 0.06);
  }
  .thr__deploy-btn:active {
    background: rgba(126, 245, 184, 0.14);
  }
  .thr__deploy-btn--ext {
    color: var(--accent);
    border-color: var(--accent-dim);
  }
  .thr__deploy-btn--ret {
    color: var(--fg-dim);
  }

  .thr__empty {
    display: flex;
    align-items: center;
    gap: 8px;
    margin: 0;
    padding: 8px 10px;
    color: var(--fg-mute);
    font-size: 10px;
    letter-spacing: 0.14em;
  }
  .thr__empty-rule {
    flex: 1 1 auto;
    height: 1px;
    background: var(--line-faint, rgba(126, 245, 184, 0.06));
  }
  .thr__empty-text {
    flex: 0 0 auto;
  }
</style>
