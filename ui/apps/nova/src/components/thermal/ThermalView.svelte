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

  import { useNovaParts } from '../../telemetry/use-nova-parts.svelte';
  import type { NovaPartHandle } from '../../telemetry/use-nova-parts.svelte';
  import { NovaPartTopic, IonTripReason } from '../../telemetry/nova-topics';
  import { getKsp, useStageOps } from '@dragonglass/telemetry/svelte';
  import { onDestroy } from 'svelte';
  import ComponentIcon from '../ComponentIcon.svelte';
  import SegmentGauge from '../SegmentGauge.svelte';
  import { siPrefix, fmtMag } from '../../util/units';
  import { resourceMeta } from '../resource/resource-codes';

  const RATE_EPSILON = 0.005;
  const TEMP_RATE_EPSILON = 0.001;
  const isZero = (v: number): boolean => Math.abs(v) < RATE_EPSILON;
  const isTempRateZero = (v: number): boolean => Math.abs(v) < TEMP_RATE_EPSILON;

  interface Props {
    vesselId: string;
  }
  const { vesselId }: Props = $props();

  const vesselParts = useNovaParts(() => vesselId);
  const ksp = getKsp();

  type NodeKey = 'producers' | 'radiators';
  let expanded = $state<Record<NodeKey, boolean>>({
    producers: true,
    radiators: true,
  });
  function toggle(k: NodeKey): void {
    expanded[k] = !expanded[k];
  }

  function isRtgPart(p: NovaPartHandle): boolean {
    return !!p.state && p.state.rtg.length > 0;
  }
  function isIonPart(p: NovaPartHandle): boolean {
    return !!p.state && p.state.ion.length > 0;
  }
  function isRadiatorPart(p: NovaPartHandle): boolean {
    return !!p.state && p.state.radiator.length > 0;
  }

  // Aggregate across all ion engines on a part. Sums waste-heat
  // injection + rejection so a player can read the thermal handshake
  // (engine producing X W, exporting Y W to the bus). Temperatures
  // average if multiple engines somehow live on one part — keeps the
  // row layout symmetric with the RTG sum.
  function ionThermal(p: NovaPartHandle): {
    heatIn: number; rejection: number;
    tempK: number; maxTempK: number;
    tripped: boolean; tripReason: IonTripReason;
  } {
    let heatIn = 0, rejection = 0, tempK = 0, maxTempK = 0;
    let tripped = false;
    let tripReason: IonTripReason = IonTripReason.None;
    let n = 0;
    if (p.state) {
      for (const ion of p.state.ion) {
        heatIn    += ion.wasteHeatW;
        rejection += ion.rejectionW;
        tempK     += ion.coreTempK;
        maxTempK  += ion.maxOperatingTempK;
        if (ion.tripped) { tripped = true; tripReason = ion.tripReason; }
        n++;
      }
    }
    if (n > 1) {
      tempK /= n;
      maxTempK /= n;
    }
    return { heatIn, rejection, tempK, maxTempK, tripped, tripReason };
  }

  function ionTempFraction(p: NovaPartHandle): number {
    const t = ionThermal(p);
    // Same ambient assumption as the runtime view — 290 K floor so the
    // bar starts near empty rather than ~20% full at idle. The gauge
    // reads as "headroom to the trip line".
    const ambient = 290;
    const span = t.maxTempK - ambient;
    if (span <= 0) return 0;
    return Math.max(0, Math.min(1, (t.tempK - ambient) / span));
  }

  // Aggregate across all RTG components on a part. Stock has one per
  // part; the sum stays correct for hypothetical multi-RTG parts.
  function rtgThermal(p: NovaPartHandle): {
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

  function rtgTempFraction(p: NovaPartHandle): number {
    const t = rtgThermal(p);
    return t.maxTempC > 0 ? t.tempC / t.maxTempC : 0;
  }

  function radiatorOf(p: NovaPartHandle): {
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

  // One row per cryocooler-bearing slice across all tanks on the vessel.
  // Each row carries its own stage toggle + live heat draw; aggregate
  // heat is added to the cooling-system load alongside RTG waste heat.
  interface CoolerRow {
    key: string;
    partId: string;
    partTitle: string;
    /** Canonical resource name — drives `resourceMeta` lookup for the
     *  tint colour. The short code is computed at render time so this
     *  one carrier is enough. */
    resource: string;
    resourceCode: string;
    sliceIndex: number;
    stage: number;
    maxStage: number;
    coolerEcW: number;
    coolerHeatW: number;
    boiloffFractionPerDay: number;
    stageVector: number[];
  }
  const coolerRows = $derived.by((): CoolerRow[] => {
    const rows: CoolerRow[] = [];
    for (const p of vesselParts.current) {
      if (!p.state) continue;
      for (const tank of p.state.tank) {
        const stageVector = tank.slices.map((s) => s.stage);
        for (let i = 0; i < tank.slices.length; i++) {
          const s = tank.slices[i];
          if (s.maxStage <= 0) continue;
          rows.push({
            key: `${p.struct.id}:t${i}`,
            partId: p.struct.id,
            partTitle: p.struct.title,
            resource: s.resource,
            resourceCode: resourceMeta(s.resource).code,
            sliceIndex: i,
            stage: s.stage,
            maxStage: s.maxStage,
            coolerEcW: s.coolerEcW,
            coolerHeatW: s.coolerHeatW,
            boiloffFractionPerDay: s.boiloffFractionPerDay,
            stageVector,
          });
        }
      }
    }
    return rows;
  });

  function cycleTankCooler(row: CoolerRow): void {
    const next = (row.stage + 1) % (row.maxStage + 1);
    const vector = [...row.stageVector];
    vector[row.sliceIndex] = next;
    ksp.send(NovaPartTopic(row.partId), 'setTankCooler', vector);
  }
  function tankStageLabel(stage: number, maxStage: number): string {
    if (stage === 0) return 'OFF';
    if (maxStage === 1) return 'ON';
    return 'S' + stage;
  }
  function fmtFracPctPerDay(frac: number): string {
    return (frac * 100).toFixed(3) + '%/d';
  }

  const groups = $derived.by(() => {
    const rtgs: NovaPartHandle[] = [];
    const ions: NovaPartHandle[] = [];
    const radiators: NovaPartHandle[] = [];
    for (const p of vesselParts.current) {
      if (isRtgPart(p)) rtgs.push(p);
      if (isIonPart(p)) ions.push(p);
      if (isRadiatorPart(p)) radiators.push(p);
    }
    return { rtgs, ions, radiators };
  });

  const totals = $derived({
    // Heat load on the cooling system — RTG waste heat + ion engine
    // waste heat + cryocooler waste heat (Q_hot = Q_cold + W_in all
    // lands on the bus radiators have to reject).
    heatLoad: groups.rtgs.reduce((a, p) => a + rtgThermal(p).heatIn, 0)
            + groups.ions.reduce((a, p) => a + ionThermal(p).heatIn, 0)
            + coolerRows.reduce((a, r) => a + r.coolerHeatW, 0),
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

<!-- Cooler row — two-line stack matching the RTG layout. Primary line
     carries the heat load (amber, like RTG waste heat); secondary line
     shows the EC pump cost + realised boiloff so the player sees the
     full tradeoff. Resource code in the title gets the resource's
     per-tint colour from `resourceMeta`. -->
{#snippet coolerRow(row: CoolerRow)}
  {@const heatFmt = fmtRate(row.coolerHeatW)}
  {@const ecFmt   = fmtRate(row.coolerEcW)}
  {@const tint    = resourceMeta(row.resource).color}
  <li class="thr__row thr__row--stack thr__row--cooler"
      class:thr__row--closed={row.stage === 0}
      onmouseenter={() => highlightOn([row.partId])}
      onmouseleave={highlightOff}>
    <span class="thr__row-icon">
      <ComponentIcon kind="tank" />
    </span>
    <div class="thr__row-stack">
      <div class="thr__row-line">
        <span class="thr__row-name">
          {row.partTitle}<em class="thr__row-subname"
            style:color={tint}> · {row.resourceCode}</em>
        </span>
        <button type="button" class="thr__deploy-btn thr__cooler-btn"
                class:thr__cooler-btn--on={row.stage > 0}
                aria-label="Cycle cryocooler stage"
                title={row.maxStage === 1
                  ? 'Toggle cryocooler (OFF / ON)'
                  : 'Cycle cryocooler (OFF / S1 / S2)'}
                onclick={(e) => { e.stopPropagation(); cycleTankCooler(row); }}>
          <span>{tankStageLabel(row.stage, row.maxStage)}</span>
        </button>
        <span class="thr__rate thr__rate--load thr__rate--col"
              class:thr__rate--zero={isZero(row.coolerHeatW)}
              title="Waste heat dumped to the cooling bus by this cryocooler">
          {heatFmt.mag}<em>{heatFmt.unit}</em>
        </span>
      </div>
      <div class="thr__row-line thr__row-line--secondary">
        <span class="thr__cooler-meta"
              title="EC pump cost and realised boiloff for this slice">
          EC {ecFmt.mag}<em>{ecFmt.unit}</em>
          · boil {fmtFracPctPerDay(row.boiloffFractionPerDay)}
        </span>
      </div>
    </div>
  </li>
{/snippet}

<!-- Per-radiator deploy toggle. Fixed panels (deployable=false) have
     no control. Deployable rads show EXT when retracted, RET when
     deployed. Effect is immediate; no busy/pending state. -->
{#snippet radiatorControl(p: NovaPartHandle, r: { deployed: boolean; deployable: boolean })}
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
      {#if groups.rtgs.length === 0 && groups.ions.length === 0 && coolerRows.length === 0}
        {@render emptyMsg('NO HEAT PRODUCERS')}
      {:else}
        <ul class="thr__rows">
          {#each groups.rtgs as p (p.struct.id)}
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
          <!-- Ion engines — accelerator-grid waste heat exported to the
               same bus as the RTGs. Trip latch surfaces as a chip on
               the primary line; reset lives in PWR (where the player
               manages EC consumers). Layout mirrors the RTG row: gauge
               + core T / max T + delta from the trip threshold. -->
          {#each groups.ions as p (p.struct.id)}
            {@const t = ionThermal(p)}
            {@const loadFmt = fmtRate(t.heatIn)}
            {@const rejFmt  = fmtRate(t.rejection)}
            {@const tempFrac = ionTempFraction(p)}
            {@const tempMarginK = t.maxTempK - t.tempK}
            <li class="thr__row thr__row--stack"
                class:thr__row--closed={t.tripped}
                onmouseenter={() => highlightOn([p.struct.id])}
                onmouseleave={highlightOff}>
              <span class="thr__row-icon">
                <ComponentIcon kind="ion" />
              </span>
              <div class="thr__row-stack">
                <!-- Line 1: name + waste-heat number. Trip badge sits
                     between name and rate; on `XeStarvation` the engine
                     has zero EC draw, so heat too — the row reads as a
                     calm trip rather than a runaway. -->
                <div class="thr__row-line">
                  <span class="thr__row-name">{p.struct.title}</span>
                  {#if t.tripped}
                    <span class="thr__ion-trip"
                          title={t.tripReason === IonTripReason.XeStarvation
                            ? 'Tripped — Xenon supply collapsed. Clear in PWR.'
                            : t.tripReason === IonTripReason.Overtemp
                              ? 'Tripped — overtemp. Add radiator capacity and clear in PWR.'
                              : 'Tripped — clear in PWR'}>
                      {t.tripReason === IonTripReason.XeStarvation ? 'TRIP·XE'
                        : t.tripReason === IonTripReason.Overtemp ? 'TRIP·HOT'
                        : 'TRIP'}
                    </span>
                  {/if}
                  <span class="thr__rate thr__rate--load"
                        class:thr__rate--zero={isZero(t.heatIn)}
                        title="Waste heat injected this tick = currentEcW × (1 − jetEfficiency)">
                    {loadFmt.mag}<em>{loadFmt.unit}</em>
                  </span>
                </div>
                <!-- Line 2: temp gauge + K readout + headroom-to-trip.
                     `tempMarginK` flips the readout color when the
                     buffer is closing in on `maxTempK`. -->
                <div class="thr__row-line thr__row-line--gauge">
                  <SegmentGauge fraction={tempFrac} />
                  <span class="thr__temp"
                        title="Core temperature / overtemp trip threshold">
                    {fmtTempC(t.tempK - 273.15)}<em>°C</em><span
                      class="thr__temp-sep">/</span>{fmtTempC(t.maxTempK - 273.15)}<em>°C</em>
                  </span>
                  <span class="thr__dtdt"
                        class:thr__dtdt--up={tempMarginK < 50}
                        class:thr__dtdt--down={tempMarginK > 200}
                        class:thr__dtdt--zero={tempMarginK <= 0}
                        title="Headroom to overtemp trip. Negative = engine has tripped.">
                    Δ{tempMarginK > 0 ? fmtTempC(tempMarginK) : '—'}<em>K</em>
                  </span>
                </div>
                <!-- Line 3: rejection — the radiator-bus handshake.
                     Same colour idiom as RTG passive rejection (mint =
                     heat leaving the part); dim when matched, alert
                     when production outruns it (precursor to overtemp). -->
                <div class="thr__row-line thr__row-line--secondary">
                  <span class="thr__rejection-label">bus rejection</span>
                  <span class="thr__rate"
                        class:thr__rate--cool={t.rejection >= t.heatIn - RATE_EPSILON}
                        class:thr__rate--load={t.rejection < t.heatIn - RATE_EPSILON}
                        class:thr__rate--zero={isZero(t.rejection)}
                        title={t.rejection < t.heatIn - RATE_EPSILON
                          ? 'Radiator headroom short — heat accumulating, overtemp incoming'
                          : 'Radiator covers production — thermal balance steady'}>
                    {rejFmt.mag}<em>{rejFmt.unit}</em>
                  </span>
                </div>
              </div>
            </li>
          {/each}
          <!-- Tank cryocoolers — heat producers in the same sense as
               RTGs (waste heat hits the bus radiators have to reject)
               but sourced from EC × (1 + COP). Each row has its own
               stage toggle: BAC = on/off, ZBO = off/s1/s2 (s1 runs
               BAC-equivalent). -->
          {#each coolerRows as row (row.key)}
            {@render coolerRow(row)}
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

  /* Cryocooler stage toggle. Fixed width so OFF / ON / S1 / S2 all
     occupy the same footprint — the right-side rate column stays at a
     constant horizontal position as the player cycles. */
  .thr__cooler-btn {
    color: var(--fg-mute);
    border-color: var(--line);
    width: 44px;
    text-align: center;
    flex: 0 0 auto;
  }
  .thr__cooler-btn--on {
    color: var(--accent);
    border-color: var(--accent-dim);
    background: rgba(126, 245, 184, 0.08);
  }

  /* Rate cell when used in a cooler row — fixed minimum width and
     right-aligned so the button to its left lines up across rows
     regardless of digit count ("200 W" vs "1.5 kW"). */
  .thr__rate--col {
    min-width: 56px;
    text-align: right;
  }

  /* Resource-code subname appended to a cooler row title. Carries
     the resource's per-tint colour via inline style:color, so this
     rule only sets the typographic frame (display font + tracking). */
  .thr__row-subname {
    font-family: var(--font-display);
    font-size: 9px;
    letter-spacing: 0.18em;
    font-style: normal;
  }
  /* Cooler secondary line — EC + boiloff. Use a slightly less-dim
     foreground than fg-mute so the numbers stay readable against
     the dark panel background. */
  .thr__cooler-meta {
    flex: 1 1 auto;
    color: var(--fg-dim);
    font-variant-numeric: tabular-nums;
    white-space: nowrap;
  }
  .thr__cooler-meta em {
    font-style: normal;
    color: var(--fg-mute);
    margin-left: 1px;
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

  /* Ion engine trip chip. Sits in the primary row line between the
     name and the heat-load number. Same chip shape as the cooler
     stage toggle, but alert-tinted + pulsing. No reset button here
     — reset lives in PWR (the trip is a power-side action). */
  .thr__ion-trip {
    flex: 0 0 auto;
    padding: 1px 5px;
    border: 1px solid var(--alert);
    color: var(--alert);
    background: rgba(60, 12, 12, 0.42);
    font-family: var(--font-display);
    font-size: 9px;
    letter-spacing: 0.18em;
    line-height: 1.3;
    animation: thr-ion-pulse 1.4s ease-in-out infinite;
  }

  @keyframes thr-ion-pulse {
    0%, 100% { opacity: 1; }
    50%      { opacity: 0.55; }
  }
</style>
