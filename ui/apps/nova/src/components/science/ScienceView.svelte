<script lang="ts">
  // Science tab body. Hierarchical tree:
  //   INSTRUMENTS
  //     ▾ <Instrument>          (collapsible per part)
  //         ▾ <Experiment>      (collapsible per experiment id)
  //             [status / completion in header; indicator in body]
  //   STORAGE
  //     <DataStorage rows>      (flat list, modal for files)
  //
  // Visual language matches PowerView (.pwr__) — same chrome, different
  // prefix (.sci__).

  import {
    useNovaScienceByTag,
    useNovaStorageByTag,
  } from '../../telemetry/use-nova-parts-by-tag.svelte';
  import type { NovaTagged } from '../../telemetry/use-nova-parts-by-tag.svelte';
  import { NovaScienceTopic } from '../../telemetry/nova-topics';
  import type {
    ScienceFile,
    NovaStorage,
    AtmExperimentState,
    LtsExperimentState,
  } from '../../telemetry/nova-topics';
  import { getKsp } from '@dragonglass/telemetry/svelte';
  import ComponentIcon from '../ComponentIcon.svelte';
  import SegmentGauge from '../SegmentGauge.svelte';
  import FileListModal from './FileListModal.svelte';
  import AtmProfileIndicator from './AtmProfileIndicator.svelte';
  import AtmTempPlot from './AtmTempPlot.svelte';
  import LtsOrbitIndicator from './LtsOrbitIndicator.svelte';
  import { fmtBytes, fmtDuration } from '../../util/units';
  import { experimentLabel } from '../../util/science-labels';

  type StorageTagged = NovaTagged<NovaStorage>;

  interface Props {
    vesselId: string;
  }
  const { vesselId }: Props = $props();

  const instruments = useNovaScienceByTag(() => vesselId, 'science-instrument');
  const storages    = useNovaStorageByTag(() => vesselId, 'science-storage');

  const ksp = getKsp();

  function toggleExperiment(
    partId: string,
    instrumentIndex: number,
    expId: string,
    enabled: boolean,
  ): void {
    ksp.send(NovaScienceTopic(partId), 'setExperimentEnabled', instrumentIndex, expId, !enabled);
  }

  // Aggregate roll-up across every DataStorage on the vessel.
  const totals = $derived.by(() => {
    let used = 0;
    let cap = 0;
    let fileCount = 0;
    for (const p of storages.current) {
      if (!p.state) continue;
      used += p.state.usedBytes;
      cap += p.state.capacityBytes;
      fileCount += p.state.fileCount;
    }
    return { used, cap, fileCount };
  });

  const aggregateFraction = $derived(totals.cap > 0 ? totals.used / totals.cap : 0);

  // ---- Section open state (top-level INSTRUMENTS / STORAGE) -----
  let instrOpen = $state(true);
  let storeOpen = $state(true);

  // ---- Tree open state (per instrument / per experiment) -------
  // Closed-by-default sets — entries added on toggle. Default-open
  // logic: a key NOT in the set is treated as open. Toggling a key
  // collapses (adds to set); re-toggling expands (removes). Persisting
  // open-state in a Set this way means new instruments/experiments
  // appear expanded automatically, which matches the user's "show me
  // everything" expectation when a vessel comes online.
  //
  // Instrument-level keys are `${partId}::${instrumentIndex}` so that
  // a multi-instrument part collapses each instrument independently.
  let collapsedInstr = $state<Set<string>>(new Set());
  let collapsedExp   = $state<Set<string>>(new Set());

  function instrKey(partId: string, instrIdx: number): string {
    return `${partId}::${instrIdx}`;
  }
  function toggleInstr(partId: string, instrIdx: number): void {
    const k = instrKey(partId, instrIdx);
    const next = new Set(collapsedInstr);
    if (next.has(k)) next.delete(k);
    else next.add(k);
    collapsedInstr = next;
  }
  function toggleExp(partId: string, instrIdx: number, expId: string): void {
    const k = `${partId}::${instrIdx}::${expId}`;
    const next = new Set(collapsedExp);
    if (next.has(k)) next.delete(k);
    else next.add(k);
    collapsedExp = next;
  }
  function isInstrOpen(partId: string, instrIdx: number): boolean {
    return !collapsedInstr.has(instrKey(partId, instrIdx));
  }
  function isExpOpen(partId: string, instrIdx: number, expId: string): boolean {
    return !collapsedExp.has(`${partId}::${instrIdx}::${expId}`);
  }

  // ---- File list modal ------------------------------------------
  let openModalPartId = $state<string | null>(null);

  function openFiles(p: StorageTagged): void {
    openModalPartId = p.struct.id;
  }
  function closeFiles(): void {
    openModalPartId = null;
  }

  const openPart = $derived(
    openModalPartId
      ? storages.current.find((p) => p.struct.id === openModalPartId) ?? null
      : null,
  );
  const openFilesList = $derived.by<ScienceFile[]>(() =>
    openPart?.state ? openPart.state.files : [],
  );

  // ---- Per-storage helpers --------------------------------------
  function partTotals(p: StorageTagged): { used: number; cap: number; files: number } {
    if (!p.state) return { used: 0, cap: 0, files: 0 };
    return {
      used:  p.state.usedBytes,
      cap:   p.state.capacityBytes,
      files: p.state.fileCount,
    };
  }

  function partFraction(p: StorageTagged): number {
    const t = partTotals(p);
    return t.cap > 0 ? t.used / t.cap : 0;
  }

  // ---- Experiment summary ---------------------------------------
  // Used in the experiment-row header so the player can read state
  // without expanding. Atm: completed-layer counter. Lts: ETA to next
  // slice + sealed-slice counter.

  function atmSummary(state: AtmExperimentState): string {
    if (state.layers.length === 0) return 'NO ATMOSPHERE';
    const sealed = [...state.savedLocal.values()].filter((f) => f >= 0.999).length;
    return `${sealed}/${state.layers.length} SEALED`;
  }

  // Overall completion across all of this body's regimes for this
  // experiment. Counts saved fidelity directly + the in-progress
  // accumulator on the current segment (so the % creeps up live).
  // Returns 0..100 (integer).
  function atmCompletion(state: AtmExperimentState): number {
    if (state.layers.length === 0) return 0;
    let total = 0;
    for (const f of state.savedLocal.values()) total += Math.min(1, f);
    return Math.round((total / state.layers.length) * 100);
  }

  function ltsSummary(state: LtsExperimentState): string {
    const sealed = [...state.savedLocal.values()].filter((f) => f >= 0.999).length;
    if (!state.active || state.bodyYearSeconds <= 0) {
      return `${sealed}/${state.slicesPerYear} SEALED`;
    }
    const sliceDur = state.bodyYearSeconds / state.slicesPerYear;
    const phaseInSlice = state.phase * state.slicesPerYear - state.currentSliceIndex;
    const timeLeft = (1 - phaseInSlice) * sliceDur;
    return `ETA ${fmtDuration(timeLeft)} · ${sealed}/${state.slicesPerYear}`;
  }

  // Pretty-printers for the live status block in the experiment body.
  // Display unit conventions: altitude in km (with one decimal), phase
  // as percent of body-year. Returns "—" when the relevant data isn't
  // present yet.
  function fmtKm(meters: number): string {
    return `${(meters / 1000).toFixed(1)} km`;
  }
  function fmtPct(frac: number, digits = 1): string {
    return `${(frac * 100).toFixed(digits)}%`;
  }

  // Surface floor (m) below which the experiment doesn't gather data.
  // Mirrors `AtmosphericProfileExperiment.SurfaceFloorMeters` C#-side.
  const ATM_SURFACE_FLOOR_M = 1_000;

  function atmNow(s: AtmExperimentState): string {
    if (s.currentLayerName === 'surface') {
      return `Surface · 0–${fmtKm(ATM_SURFACE_FLOOR_M)}`;
    }
    if (!s.currentLayerName) return 'above atmosphere';
    // The "now" line names the current regime AND the limits the
    // experiment must capture. Vessel position lives on the indicator;
    // the data line is the *target span*, not where the vessel is.
    const idx   = s.layers.findIndex((l) => l.name === s.currentLayerName);
    if (idx < 0) return prettyLayer(s.currentLayerName);
    const layer = s.layers[idx];
    const bottomAlt = idx === 0 ? ATM_SURFACE_FLOOR_M : s.layers[idx - 1].top;
    return `${prettyLayer(s.currentLayerName)} · ${fmtKm(bottomAlt)}–${fmtKm(layer.top)}`;
  }
  function atmSeen(s: AtmExperimentState): string {
    if (s.transitMaxAlt <= s.transitMinAlt) return '—';
    const idx   = s.layers.findIndex((l) => l.name === s.currentLayerName);
    const layer = idx >= 0 ? s.layers[idx] : undefined;
    const bottomAlt = idx === 0 ? ATM_SURFACE_FLOOR_M
                    : idx >  0  ? s.layers[idx - 1].top
                    : 0;
    const span = layer ? Math.max(0, layer.top - bottomAlt) : 0;
    const got  = Math.max(0, s.transitMaxAlt - s.transitMinAlt);
    const pct  = span > 0 ? Math.min(1, got / span) : 0;
    return `${fmtKm(s.transitMinAlt)}–${fmtKm(s.transitMaxAlt)} · ${fmtPct(pct, 0)}`;
  }
  function prettyLayer(name: string): string {
    return name ? name.charAt(0).toUpperCase() + name.slice(1) : '';
  }

  function ltsNow(s: LtsExperimentState): string {
    // Limits, not current — the slice range as a fraction of body-year.
    const sliceStart = s.currentSliceIndex / s.slicesPerYear;
    const sliceEnd   = (s.currentSliceIndex + 1) / s.slicesPerYear;
    return `slice ${s.currentSliceIndex + 1}/${s.slicesPerYear} · ${fmtPct(sliceStart, 1)}–${fmtPct(sliceEnd, 1)}`;
  }
  function ltsSeen(s: LtsExperimentState): string {
    if (s.recordedMaxPhase <= s.recordedMinPhase) return '—';
    const sliceStart = s.currentSliceIndex / s.slicesPerYear;
    const sliceEnd   = (s.currentSliceIndex + 1) / s.slicesPerYear;
    const sliceSpan  = sliceEnd - sliceStart;
    const got        = s.recordedMaxPhase - s.recordedMinPhase;
    const pct        = sliceSpan > 0 ? Math.min(1, got / sliceSpan) : 0;
    return `${fmtPct(s.recordedMinPhase, 1)}–${fmtPct(s.recordedMaxPhase, 1)} · ${fmtPct(pct, 0)}`;
  }

  function ltsCompletion(state: LtsExperimentState): number {
    let total = 0;
    for (const f of state.savedLocal.values()) total += Math.min(1, f);
    // Include the in-progress slice when it isn't already in saved
    // (otherwise we'd double-count when re-running into a prior slice).
    if (state.active && !state.savedLocal.has(state.currentSliceIndex)) {
      total += Math.min(1, state.activeFidelity);
    }
    return Math.round((total / state.slicesPerYear) * 100);
  }
</script>

<section class="sci">
  <!-- Instruments ---------------------------------------------- -->
  <div class="sci__node">
    <button
      type="button"
      class="sci__node-head"
      aria-expanded={instrOpen}
      onclick={() => (instrOpen = !instrOpen)}
    >
      <span class="sci__chev" aria-hidden="true">{instrOpen ? '▾' : '▸'}</span>
      <span class="sci__node-title">INSTRUMENTS</span>
      <span class="sci__node-summary">
        {#if instruments.current.length > 0}
          <span class="sci__node-files">{instruments.current.length}</span>
        {:else}
          <span class="sci__node-empty">—</span>
        {/if}
      </span>
    </button>

    {#if instrOpen}
      {#if instruments.current.length === 0}
        <p class="sci__empty">No science instruments on this vessel.</p>
      {:else}
        <ul class="sci__instr-rows">
          {#each instruments.current as p (p.struct.id)}
            {#each p.state?.instruments ?? [] as inst, instrIdx (instrIdx)}
              {@const partOpen = isInstrOpen(p.struct.id, instrIdx)}
              {@const headLabel = inst.name || p.struct.title}
              <li class="sci__instr">
                <button
                  type="button"
                  class="sci__instr-head"
                  aria-expanded={partOpen}
                  onclick={() => toggleInstr(p.struct.id, instrIdx)}
                >
                  <span class="sci__chev sci__chev--sub" aria-hidden="true">{partOpen ? '▾' : '▸'}</span>
                  <span class="sci__row-icon">
                    <ComponentIcon kind="thermometer" />
                  </span>
                  <span class="sci__instr-name">{headLabel}</span>
                </button>

              {#if partOpen && inst.experimentIds.length > 0}
                <ul class="sci__exp-rows">
                  {#each inst.experimentIds as expId (expId)}
                    {@const atm     = inst.atmExperiment?.experimentId === expId ? inst.atmExperiment : undefined}
                    {@const lts     = inst.ltsExperiment?.experimentId === expId ? inst.ltsExperiment : undefined}
                    {@const expOpen = isExpOpen(p.struct.id, instrIdx, expId)}
                    {@const active  = atm?.active || lts?.active}
                    {@const enabled = atm?.enabled ?? lts?.enabled ?? false}
                    {@const summary = atm ? atmSummary(atm) : lts ? ltsSummary(lts) : ''}
                    {@const pct     = atm ? atmCompletion(atm) : lts ? ltsCompletion(lts) : 0}
                    {@const dest    = atm?.destinationStorage ?? lts?.destinationStorage ?? ''}
                    {@const stateLabel =
                      !enabled ? 'OFF' : active ? 'ON' : 'IDLE'}
                    <li class="sci__exp">
                      <div
                        class="sci__exp-head"
                        role="button"
                        tabindex="0"
                        aria-expanded={expOpen}
                        onclick={() => toggleExp(p.struct.id, instrIdx, expId)}
                        onkeydown={(e) => {
                          if (e.key === 'Enter' || e.key === ' ') {
                            e.preventDefault();
                            toggleExp(p.struct.id, instrIdx, expId);
                          }
                        }}
                      >
                        <span class="sci__exp-line">
                          <span class="sci__chev sci__chev--leaf" aria-hidden="true">{expOpen ? '▾' : '▸'}</span>
                          <span class="sci__exp-label">{experimentLabel(expId)}</span>
                          <span class="sci__exp-pct">{pct}%</span>
                          <button
                            type="button"
                            class="sci__exp-status"
                            class:sci__exp-status--on={enabled && active}
                            class:sci__exp-status--idle={enabled && !active}
                            class:sci__exp-status--off={!enabled}
                            aria-pressed={enabled}
                            aria-label={`${enabled ? 'Disable' : 'Enable'} ${experimentLabel(expId)}`}
                            onclick={(e) => {
                              e.stopPropagation();
                              toggleExperiment(p.struct.id, instrIdx, expId, enabled);
                            }}
                          >{stateLabel}</button>
                        </span>
                        <span class="sci__exp-eta">{summary}</span>
                      </div>

                      {#if expOpen}
                        <div class="sci__exp-body">
                          <div class="sci__exp-viz">
                            {#if atm}
                              <AtmProfileIndicator state={atm} />
                              <AtmTempPlot atm={atm} />
                            {:else if lts}
                              <LtsOrbitIndicator state={lts} />
                            {/if}
                          </div>
                          {#if enabled}
                            <div class="sci__exp-detail">
                              {#if atm}
                                <div class="sci__exp-detail-line">
                                  <span class="sci__exp-detail-key">LIMITS</span>
                                  <span class="sci__exp-detail-val">{atmNow(atm)}</span>
                                </div>
                                <div class="sci__exp-detail-line">
                                  <span class="sci__exp-detail-key">SEEN</span>
                                  <span class="sci__exp-detail-val">{atmSeen(atm)}</span>
                                </div>
                              {:else if lts}
                                <div class="sci__exp-detail-line">
                                  <span class="sci__exp-detail-key">LIMITS</span>
                                  <span class="sci__exp-detail-val">{ltsNow(lts)}</span>
                                </div>
                                <div class="sci__exp-detail-line">
                                  <span class="sci__exp-detail-key">SEEN</span>
                                  <span class="sci__exp-detail-val">{ltsSeen(lts)}</span>
                                </div>
                              {/if}
                              <div class="sci__exp-detail-line">
                                <span class="sci__exp-detail-key">STORAGE</span>
                                <span
                                  class="sci__exp-detail-val"
                                  class:sci__exp-detail-val--missing={dest === ''}
                                >{dest === '' ? 'NO STORAGE' : dest}</span>
                              </div>
                            </div>
                          {/if}
                        </div>
                      {/if}
                    </li>
                  {/each}
                </ul>
              {/if}
              </li>
            {/each}
          {/each}
        </ul>
      {/if}
    {/if}
  </div>

  <!-- Storage -------------------------------------------------- -->
  <div class="sci__node">
    <button
      type="button"
      class="sci__node-head"
      aria-expanded={storeOpen}
      onclick={() => (storeOpen = !storeOpen)}
    >
      <span class="sci__chev" aria-hidden="true">{storeOpen ? '▾' : '▸'}</span>
      <span class="sci__node-title">STORAGE</span>
      <span class="sci__node-summary">
        {#if totals.cap > 0}
          <span class="sci__node-files">{totals.fileCount} file{totals.fileCount === 1 ? '' : 's'}</span>
          <span class="sci__node-sep">·</span>
          <span class="sci__node-bytes">{fmtBytes(totals.used)}<em>/{fmtBytes(totals.cap)}</em></span>
        {:else}
          <span class="sci__node-empty">—</span>
        {/if}
      </span>
    </button>

    {#if storages.current.length > 0}
      <div class="sci__node-gauge">
        <SegmentGauge fraction={aggregateFraction} />
      </div>
    {/if}

    {#if storeOpen}
      {#if storages.current.length === 0}
        <p class="sci__empty">No data storage on this vessel — install a probe core.</p>
      {:else}
        <ul class="sci__rows">
          {#each storages.current as p (p.struct.id)}
            {@const t = partTotals(p)}
            {@const f = partFraction(p)}
            <li class="sci__row">
              <span class="sci__row-icon">
                <ComponentIcon kind="dataStorage" />
              </span>
              <div class="sci__row-stack">
                <div class="sci__row-line">
                  <span class="sci__row-name">{p.struct.title}</span>
                  <span class="sci__row-count">{t.files}</span>
                  <button
                    type="button"
                    class="sci__view-btn"
                    aria-label={`View files in ${p.struct.title}`}
                    title={`View files in ${p.struct.title}`}
                    onclick={() => openFiles(p)}
                  >VIEW</button>
                </div>
                <div class="sci__row-line sci__row-line--gauge">
                  <SegmentGauge fraction={f} />
                </div>
              </div>
            </li>
          {/each}
        </ul>
      {/if}
    {/if}
  </div>
</section>

<FileListModal
  open={openPart !== null}
  storageName={openPart?.struct.title ?? ''}
  files={openFilesList}
  onClose={closeFiles}
/>

<style>
  .sci {
    display: flex;
    flex-direction: column;
    gap: 0;
    padding-left: 4px;
    margin-left: -4px;
  }

  .sci__node {
    margin-top: 12px;
  }
  .sci__node:first-child {
    margin-top: 0;
  }

  /* Top-level section header (INSTRUMENTS / STORAGE). */
  .sci__node-head {
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
  .sci__node-head::after {
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
  .sci__node-head:hover::after,
  .sci__node-head:focus-visible::after {
    background: linear-gradient(90deg,
      transparent 0%,
      rgba(126, 245, 184, 0.22) 18%,
      rgba(126, 245, 184, 0.22) 82%,
      transparent 100%);
  }
  .sci__node-head::before {
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
  .sci__node-head[aria-expanded='true']::before {
    opacity: 0.45;
    transform: translateY(-50%) scaleY(1);
    background: var(--accent-dim);
  }
  .sci__node-head:hover::before,
  .sci__node-head:focus-visible::before {
    opacity: 1;
    transform: translateY(-50%) scaleY(1);
    background: var(--accent);
    box-shadow: 0 0 6px var(--accent-glow);
  }

  .sci__node-head:focus-visible { outline: none; }
  .sci__node-head:hover {
    border-bottom-color: var(--accent-dim);
  }
  .sci__node-head:hover .sci__node-title,
  .sci__node-head:focus-visible .sci__node-title {
    color: var(--accent-soft);
    text-shadow: 0 0 6px var(--accent-glow);
  }
  .sci__node-title {
    flex: 1 1 auto;
    font-size: 11px;
    color: var(--fg-dim);
    transition:
      color 220ms cubic-bezier(0.4, 0, 0.2, 1),
      text-shadow 220ms cubic-bezier(0.4, 0, 0.2, 1);
  }
  .sci__chev {
    flex: 0 0 auto;
    width: 10px;
    color: var(--accent-dim);
    font-family: var(--font-display);
    font-size: 10px;
  }
  .sci__chev--sub {
    width: 9px;
    font-size: 9px;
    color: var(--fg-mute);
  }
  .sci__chev--leaf {
    width: 9px;
    font-size: 9px;
    color: var(--fg-mute);
  }
  .sci__node-summary {
    flex: 0 0 auto;
    display: flex;
    align-items: baseline;
    gap: 6px;
    font-family: var(--font-mono);
    letter-spacing: 0.06em;
    font-size: 11px;
    color: var(--fg);
    font-variant-numeric: tabular-nums;
  }
  .sci__node-files {
    color: var(--fg-dim);
    font-size: 10px;
    letter-spacing: 0.10em;
  }
  .sci__node-sep   { color: var(--fg-mute); }
  .sci__node-bytes em {
    font-style: normal;
    color: var(--fg-mute);
  }
  .sci__node-empty {
    color: var(--fg-mute);
  }

  .sci__node-gauge {
    padding: 2px 0 8px;
  }

  /* Empty-state message. */
  .sci__empty {
    margin: 14px 4px;
    color: var(--fg-mute);
    font-size: 10px;
    letter-spacing: 0.04em;
    line-height: 1.5;
  }

  /* Per-storage rows. */
  .sci__rows {
    list-style: none;
    margin: 0;
    padding: 0;
    display: flex;
    flex-direction: column;
    gap: 0;
  }
  .sci__row {
    display: flex;
    align-items: stretch;
    gap: 10px;
    padding: 6px 4px;
    position: relative;
    border-left: 2px solid transparent;
    transition:
      background 160ms ease,
      border-left-color 160ms ease;
  }
  .sci__row:hover {
    background: rgba(126, 245, 184, 0.04);
    border-left-color: var(--accent-dim);
  }
  .sci__row-icon {
    flex: 0 0 auto;
    color: var(--fg-dim);
    width: 14px;
    height: 14px;
    display: flex;
    align-items: flex-start;
    padding-top: 1px;
  }
  .sci__row:hover .sci__row-icon {
    color: var(--accent);
  }
  .sci__row-stack {
    flex: 1 1 auto;
    min-width: 0;
    display: flex;
    flex-direction: column;
    gap: 4px;
  }
  .sci__row-line {
    display: flex;
    align-items: baseline;
    gap: 8px;
    min-width: 0;
  }
  .sci__row-name {
    flex: 1 1 auto;
    min-width: 0;
    color: var(--fg);
    font-family: var(--font-mono);
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }
  .sci__row-count {
    flex: 0 0 auto;
    color: var(--fg-dim);
    font-variant-numeric: tabular-nums;
    font-size: 11px;
    min-width: 24px;
    text-align: right;
  }

  /* ---- Instruments level (level-2 collapsibles) -------------- */
  .sci__instr-rows {
    list-style: none;
    margin: 0;
    padding: 0;
    display: flex;
    flex-direction: column;
    gap: 4px;
  }
  .sci__instr {
    margin-left: 4px;
  }
  .sci__instr-head {
    appearance: none;
    background: transparent;
    border: none;
    width: 100%;
    color: inherit;
    font: inherit;
    text-align: left;
    cursor: pointer;
    display: flex;
    align-items: center;
    gap: 8px;
    padding: 4px 4px;
    position: relative;
    border-left: 2px solid transparent;
    transition: background 160ms ease, border-left-color 160ms ease;
  }
  .sci__instr-head:hover,
  .sci__instr-head:focus-visible {
    background: rgba(126, 245, 184, 0.04);
    border-left-color: var(--accent-dim);
    outline: none;
  }
  .sci__instr-head:hover .sci__row-icon {
    color: var(--accent);
  }
  .sci__instr-name {
    flex: 1 1 auto;
    min-width: 0;
    color: var(--fg);
    font-family: var(--font-mono);
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  /* ---- Experiment level (level-3 collapsibles) --------------- */
  .sci__exp-rows {
    list-style: none;
    margin: 2px 0 4px 18px;       /* indent under instrument */
    padding: 0;
    display: flex;
    flex-direction: column;
    gap: 0;
    border-left: 1px solid var(--line);
    padding-left: 10px;
  }
  .sci__exp {
    display: flex;
    flex-direction: column;
  }
  .sci__exp-head {
    appearance: none;
    background: transparent;
    border: none;
    width: 100%;
    color: inherit;
    font: inherit;
    text-align: left;
    cursor: pointer;
    display: flex;
    flex-direction: column;
    gap: 1px;
    padding: 3px 4px;
    transition: background 140ms ease;
  }
  .sci__exp-line {
    display: flex;
    align-items: center;
    gap: 8px;
  }
  .sci__exp-head:hover,
  .sci__exp-head:focus-visible {
    background: rgba(126, 245, 184, 0.04);
    outline: none;
  }
  .sci__exp-label {
    flex: 1 1 auto;
    min-width: 0;
    color: var(--fg-dim);
    font-family: var(--font-display);
    font-size: 10px;
    letter-spacing: 0.16em;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }
  .sci__exp-head:hover .sci__exp-label,
  .sci__exp-head:focus-visible .sci__exp-label {
    color: var(--accent-soft);
  }
  /* Live-updating completion percentage. Sits between the label and
     the ON/OFF pill — mono so it tabular-aligns across rows. */
  .sci__exp-pct {
    flex: 0 0 auto;
    font-family: var(--font-mono);
    font-size: 11px;
    color: var(--accent);
    font-variant-numeric: tabular-nums;
    letter-spacing: 0.02em;
  }

  /* Status pill — also a click target. Three states: ON (currently
     observing), IDLE (enabled but waiting for the right regime), OFF
     (user-disabled). Clicking flips between OFF ↔ ON|IDLE; the active
     state itself is environment-derived. */
  .sci__exp-status {
    flex: 0 0 auto;
    appearance: none;
    cursor: pointer;
    font-family: var(--font-display);
    font-size: 8.5px;
    letter-spacing: 0.20em;
    padding: 1px 5px;
    border: 1px solid var(--line);
    background: transparent;
    color: var(--fg-mute);
    border-radius: 1px;
    transition: color 140ms ease, border-color 140ms ease,
                background 140ms ease, text-shadow 140ms ease;
  }
  .sci__exp-status:focus-visible {
    outline: 1px solid var(--accent);
    outline-offset: 1px;
  }
  .sci__exp-status--on {
    color: var(--warn);
    border-color: var(--warn);
    background: rgba(240, 180, 41, 0.10);
    text-shadow: 0 0 6px var(--warn-glow);
  }
  .sci__exp-status--idle {
    color: var(--accent-dim);
    border-color: var(--accent-dim);
    background: transparent;
  }
  .sci__exp-status--idle:hover {
    color: var(--accent);
    border-color: var(--accent);
    background: rgba(126, 245, 184, 0.06);
  }
  .sci__exp-status--off:hover {
    color: var(--fg-dim);
    border-color: var(--fg-dim);
  }
  .sci__exp-eta {
    margin-left: 17px;       /* align under chevron+label, not chevron */
    font-family: var(--font-mono);
    font-size: 10px;
    color: var(--fg-mute);
    font-variant-numeric: tabular-nums;
    letter-spacing: 0.04em;
  }
  /* Indicator(s) sit in a top row; LIMITS/SEEN/destination lives
     below. Stacking the detail under the visualization keeps each
     visualization free to use its own width (atm-profile + temp plot
     are side-by-side; lts is a single ring) and lets the data block
     stretch the full row width. */
  .sci__exp-body {
    margin: 6px 0 10px 18px;
    display: flex;
    flex-direction: column;
    align-items: stretch;
    gap: 6px;
  }
  /* Top viz row — one or more indicators side-by-side. Atm shows the
     profile arc + the live temp plot; lts shows just the orbit ring. */
  .sci__exp-viz {
    display: flex;
    flex-direction: row;
    align-items: flex-start;
    gap: 8px;
  }
  /* Live status block under the viz row — two compact rows of
     LIMITS / SEEN data plus a destination strip. Only rendered when
     the experiment is enabled. */
  .sci__exp-detail {
    display: grid;
    grid-template-columns: auto 1fr;
    column-gap: 8px;
    row-gap: 2px;
    font-variant-numeric: tabular-nums;
  }
  .sci__exp-detail-line {
    display: contents;
  }
  .sci__exp-detail-key {
    color: var(--fg-mute);
    font-family: var(--font-display);
    font-size: 8.5px;
    letter-spacing: 0.20em;
  }
  .sci__exp-detail-val {
    color: var(--fg-dim);
    font-family: var(--font-mono);
    font-size: 10px;
    letter-spacing: 0.02em;
    min-width: 0;
    line-height: 1.35;
    word-spacing: -0.05em;
  }
  /* Storage destination value rendered as a regular detail row;
     turns red when no storage is reachable so the player notices
     data loss. */
  .sci__exp-detail-val--missing {
    color: var(--alert);
    text-shadow: 0 0 4px rgba(255, 82, 82, 0.4);
  }

  /* The VIEW button — small, monospaced, accent-bordered. */
  .sci__view-btn {
    flex: 0 0 auto;
    appearance: none;
    background: transparent;
    border: 1px solid var(--line);
    color: var(--fg-dim);
    font-family: var(--font-display);
    font-size: 9px;
    letter-spacing: 0.20em;
    padding: 2px 8px;
    cursor: pointer;
    transition:
      color 140ms ease,
      border-color 140ms ease,
      background 140ms ease;
  }
  .sci__view-btn:hover,
  .sci__view-btn:focus-visible {
    color: var(--accent);
    border-color: var(--accent);
    background: rgba(126, 245, 184, 0.08);
    text-shadow: 0 0 6px var(--accent-glow);
    outline: none;
  }
</style>
