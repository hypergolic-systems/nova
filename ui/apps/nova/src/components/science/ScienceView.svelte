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

  import { useNovaPartsByTag } from '../../telemetry/use-nova-parts-by-tag.svelte';
  import type { NovaTaggedPart } from '../../telemetry/use-nova-parts-by-tag.svelte';
  import type {
    ScienceFile,
    AtmExperimentState,
    LtsExperimentState,
  } from '../../telemetry/nova-topics';
  import ComponentIcon from '../ComponentIcon.svelte';
  import SegmentGauge from '../SegmentGauge.svelte';
  import FileListModal from './FileListModal.svelte';
  import AtmProfileIndicator from './AtmProfileIndicator.svelte';
  import LtsOrbitIndicator from './LtsOrbitIndicator.svelte';
  import { fmtBytes, fmtDuration } from '../../util/units';
  import { experimentLabel } from '../../util/science-labels';

  interface Props {
    vesselId: string;
  }
  const { vesselId }: Props = $props();

  const instruments = useNovaPartsByTag(() => vesselId, 'science-instrument');
  const storages    = useNovaPartsByTag(() => vesselId, 'science-storage');

  // Aggregate roll-up across every DataStorage on the vessel.
  const totals = $derived.by(() => {
    let used = 0;
    let cap = 0;
    let fileCount = 0;
    for (const p of storages.current) {
      if (!p.state) continue;
      for (const ds of p.state.dataStorage) {
        used += ds.usedBytes;
        cap += ds.capacityBytes;
        fileCount += ds.fileCount;
      }
    }
    return { used, cap, fileCount };
  });

  const aggregateFraction = $derived(totals.cap > 0 ? totals.used / totals.cap : 0);

  // ---- Section open state (top-level INSTRUMENTS / STORAGE) -----
  let instrOpen = $state(true);
  let storeOpen = $state(true);

  // ---- Tree open state (per instrument-part / per experiment) ---
  // Closed-by-default sets — entries added on toggle. Default-open
  // logic: a key NOT in the set is treated as open. Toggling a key
  // collapses (adds to set); re-toggling expands (removes). Persisting
  // open-state in a Set this way means new instruments/experiments
  // appear expanded automatically, which matches the user's "show me
  // everything" expectation when a vessel comes online.
  let collapsedInstr = $state<Set<string>>(new Set());
  let collapsedExp   = $state<Set<string>>(new Set());

  function toggleInstrPart(partId: string): void {
    const next = new Set(collapsedInstr);
    if (next.has(partId)) next.delete(partId);
    else next.add(partId);
    collapsedInstr = next;
  }
  function toggleExp(partId: string, expId: string): void {
    const key = `${partId}::${expId}`;
    const next = new Set(collapsedExp);
    if (next.has(key)) next.delete(key);
    else next.add(key);
    collapsedExp = next;
  }
  function isInstrOpen(partId: string): boolean {
    return !collapsedInstr.has(partId);
  }
  function isExpOpen(partId: string, expId: string): boolean {
    return !collapsedExp.has(`${partId}::${expId}`);
  }

  // ---- File list modal ------------------------------------------
  let openModalPartId = $state<string | null>(null);

  function openFiles(p: NovaTaggedPart): void {
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
  const openFilesList = $derived.by<ScienceFile[]>(() => {
    if (!openPart?.state) return [];
    const all: ScienceFile[] = [];
    for (const ds of openPart.state.dataStorage) all.push(...ds.files);
    return all;
  });

  // ---- Per-storage helpers --------------------------------------
  function partTotals(p: NovaTaggedPart): { used: number; cap: number; files: number } {
    let used = 0, cap = 0, files = 0;
    if (!p.state) return { used, cap, files };
    for (const ds of p.state.dataStorage) {
      used += ds.usedBytes;
      cap += ds.capacityBytes;
      files += ds.fileCount;
    }
    return { used, cap, files };
  }

  function partFraction(p: NovaTaggedPart): number {
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
            {@const inst       = p.state?.instrument[0]}
            {@const partOpen   = isInstrOpen(p.struct.id)}
            <li class="sci__instr">
              <button
                type="button"
                class="sci__instr-head"
                aria-expanded={partOpen}
                onclick={() => toggleInstrPart(p.struct.id)}
              >
                <span class="sci__chev sci__chev--sub" aria-hidden="true">{partOpen ? '▾' : '▸'}</span>
                <span class="sci__row-icon">
                  <ComponentIcon kind="thermometer" />
                </span>
                <span class="sci__instr-name">{p.struct.title}</span>
              </button>

              {#if partOpen && inst && inst.experimentIds.length > 0}
                <ul class="sci__exp-rows">
                  {#each inst.experimentIds as expId (expId)}
                    {@const atm     = p.state?.atmExperiment.find((s) => s.experimentId === expId)}
                    {@const lts     = p.state?.ltsExperiment.find((s) => s.experimentId === expId)}
                    {@const expOpen = isExpOpen(p.struct.id, expId)}
                    {@const active  = atm?.active || lts?.active}
                    {@const summary = atm ? atmSummary(atm) : lts ? ltsSummary(lts) : ''}
                    {@const pct     = atm ? atmCompletion(atm) : lts ? ltsCompletion(lts) : 0}
                    <li class="sci__exp">
                      <button
                        type="button"
                        class="sci__exp-head"
                        aria-expanded={expOpen}
                        onclick={() => toggleExp(p.struct.id, expId)}
                      >
                        <span class="sci__exp-line">
                          <span class="sci__chev sci__chev--leaf" aria-hidden="true">{expOpen ? '▾' : '▸'}</span>
                          <span class="sci__exp-label">{experimentLabel(expId)}</span>
                          <span class="sci__exp-pct">{pct}%</span>
                          <span
                            class="sci__exp-status"
                            class:sci__exp-status--on={active}
                          >{active ? 'ON' : 'OFF'}</span>
                        </span>
                        <span class="sci__exp-eta">{summary}</span>
                      </button>

                      {#if expOpen}
                        <div class="sci__exp-body">
                          {#if atm}
                            <AtmProfileIndicator state={atm} />
                          {:else if lts}
                            <LtsOrbitIndicator state={lts} />
                          {/if}
                        </div>
                      {/if}
                    </li>
                  {/each}
                </ul>
              {/if}
            </li>
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

  /* Status pill — OFF dim, ON in warn-orange (mirrors the orange
     overlay on the regime indicator's currently-observing band). */
  .sci__exp-status {
    flex: 0 0 auto;
    font-family: var(--font-display);
    font-size: 8.5px;
    letter-spacing: 0.20em;
    padding: 1px 5px;
    border: 1px solid var(--line);
    color: var(--fg-mute);
    border-radius: 1px;
    transition: color 140ms ease, border-color 140ms ease, background 140ms ease;
  }
  .sci__exp-status--on {
    color: var(--warn);
    border-color: var(--warn);
    background: rgba(240, 180, 41, 0.10);
    text-shadow: 0 0 6px var(--warn-glow);
  }
  .sci__exp-eta {
    margin-left: 17px;       /* align under chevron+label, not chevron */
    font-family: var(--font-mono);
    font-size: 10px;
    color: var(--fg-mute);
    font-variant-numeric: tabular-nums;
    letter-spacing: 0.04em;
  }
  .sci__exp-body {
    margin: 6px 0 10px 18px;
    display: flex;
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
