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
    useNovaParts,
    useNovaScienceParts,
    useNovaStorageParts,
  } from '../../telemetry/use-nova-parts.svelte';
  import type { NovaPartEntry } from '../../telemetry/use-nova-parts.svelte';
  import { NovaPartTopic, NovaScienceTopic } from '../../telemetry/nova-topics';
  import type {
    ScienceFile,
    NovaStorage,
    AtmExperimentState,
    LtsExperimentState,
  } from '../../telemetry/nova-topics';
  import { getKsp } from '@dragonglass/telemetry/svelte';
  import ComponentIcon from '../ComponentIcon.svelte';
  import SegmentGauge from '../SegmentGauge.svelte';
  import Subheading from '../common/Subheading.svelte';
  import Chip from '../common/Chip.svelte';
  import FileListModal from './FileListModal.svelte';
  import AtmProfileIndicator from './AtmProfileIndicator.svelte';
  import AtmTempPlot from './AtmTempPlot.svelte';
  import LtsOrbitIndicator from './LtsOrbitIndicator.svelte';
  import MysteryGooChamber from './MysteryGooChamber.svelte';
  import { fmtBytes, fmtDuration } from '../../util/units';
  import { experimentLabel } from '../../util/science-labels';

  type StorageEntry = NovaPartEntry<NovaStorage>;

  interface Props {
    vesselId: string;
    /** Bound out: true when the view has hardware to render. */
    hasContent?: boolean;
  }
  let { vesselId, hasContent = $bindable(true) }: Props = $props();

  const allScience = useNovaScienceParts(() => vesselId);
  const allStorage = useNovaStorageParts(() => vesselId);
  // NovaParts gives us the goo-chamber state via the 'G' frame on each
  // part. Thermometer instruments come through NovaScience; goo chambers
  // come through NovaPart. Both render under the same INSTRUMENTS tree.
  const allParts = useNovaParts(() => vesselId);
  // Iterate every part's NovaScience/NovaStorage frame, drop parts that
  // emit nothing of interest. The per-part topic returns empty
  // instrument / file lists for non-science / non-storage parts.
  const instruments = $derived(
    allScience.current.filter((p) => (p.state?.instruments.length ?? 0) > 0),
  );
  const gooParts = $derived(
    allParts.current.filter((p) => (p.state?.goo.length ?? 0) > 0),
  );
  // Total goo chambers across all parts (one part = one chamber today,
  // but the wire is shaped to support multi-chamber parts already).
  const gooChamberCount = $derived(
    gooParts.reduce((n, p) => n + (p.state?.goo.length ?? 0), 0),
  );
  const instrumentSummaryTotal = $derived(instruments.length + gooChamberCount);
  const storages = $derived(
    allStorage.current.filter((p) => (p.state?.capacityBytes ?? 0) > 0),
  );

  function setGooCoverOpen(partId: string, open: boolean): void {
    ksp.send(NovaPartTopic(partId), 'setGooCoverOpen', open);
  }

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
    for (const p of storages) {
      if (!p.state) continue;
      used += p.state.usedBytes;
      cap += p.state.capacityBytes;
      fileCount += p.state.fileCount;
    }
    return { used, cap, fileCount };
  });

  const aggregateFraction = $derived(totals.cap > 0 ? totals.used / totals.cap : 0);

  // ---- Per-experiment fold state -------------------------------
  // Closed-by-default Set — entries added on toggle. Default-open
  // logic: a key NOT in the set is treated as open. New experiments
  // appear expanded automatically, which matches the player's
  // "show me everything" expectation when a vessel comes online.
  // The previous per-instrument fold layer was removed when the
  // tree was flattened — each (instrument, experiment) tuple is
  // now its own top-level row.
  let collapsedExp = $state<Set<string>>(new Set());

  function expKey(partId: string, instrIdx: number, expId: string): string {
    return `${partId}::${instrIdx}::${expId}`;
  }
  function toggleExp(partId: string, instrIdx: number, expId: string): void {
    const k = expKey(partId, instrIdx, expId);
    const next = new Set(collapsedExp);
    if (next.has(k)) next.delete(k);
    else next.add(k);
    collapsedExp = next;
  }
  function isExpOpen(partId: string, instrIdx: number, expId: string): boolean {
    return !collapsedExp.has(expKey(partId, instrIdx, expId));
  }

  // ---- Flat experiment list ------------------------------------
  // One row per (part, instrumentIndex, experimentId). Pre-derived
  // so the template can iterate a single flat list instead of
  // nested per-instrument and per-experiment loops, which is what
  // gave the previous design its left-indent cascade. Each row
  // carries enough context to render standalone (instrument name,
  // formatted summary, dest storage).
  interface ExperimentRow {
    key:            string;
    partId:         string;
    instrumentName: string;
    instrumentIndex: number;
    experimentId:   string;
    label:          string;
    atm?:           AtmExperimentState;
    lts?:           LtsExperimentState;
    active:         boolean;
    enabled:        boolean;
    completion:     number;
    summary:        string;
    destStorage:    string;
  }
  const experimentRows = $derived.by<ExperimentRow[]>(() => {
    const out: ExperimentRow[] = [];
    for (const p of instruments) {
      const instArr = p.state?.instruments ?? [];
      for (let i = 0; i < instArr.length; i++) {
        const inst = instArr[i];
        for (const expId of inst.experimentIds) {
          const atm = inst.atmExperiment?.experimentId === expId ? inst.atmExperiment : undefined;
          const lts = inst.ltsExperiment?.experimentId === expId ? inst.ltsExperiment : undefined;
          const active     = (atm?.active || lts?.active) ?? false;
          const enabled    = atm?.enabled ?? lts?.enabled ?? false;
          const completion = atm ? atmCompletion(atm) : lts ? ltsCompletion(lts) : 0;
          const summary    = atm ? atmSummary(atm) : lts ? ltsSummary(lts) : '';
          const destStorage = atm?.destinationStorage ?? lts?.destinationStorage ?? '';
          out.push({
            key: expKey(p.struct.id, i, expId),
            partId: p.struct.id,
            instrumentName: inst.name || p.struct.title,
            instrumentIndex: i,
            experimentId: expId,
            label: experimentLabel(expId),
            atm, lts,
            active, enabled,
            completion,
            summary,
            destStorage,
          });
        }
      }
    }
    return out;
  });

  $effect(() => {
    hasContent = experimentRows.length > 0
              || gooParts.length > 0
              || storages.length > 0;
  });

  // ---- File list modal ------------------------------------------
  let openModalPartId = $state<string | null>(null);

  function openFiles(p: StorageEntry): void {
    openModalPartId = p.struct.id;
  }
  function closeFiles(): void {
    openModalPartId = null;
  }

  const openPart = $derived(
    openModalPartId
      ? storages.find((p) => p.struct.id === openModalPartId) ?? null
      : null,
  );
  const openFilesList = $derived.by<ScienceFile[]>(() =>
    openPart?.state ? openPart.state.files : [],
  );

  // ---- Per-storage helpers --------------------------------------
  function partTotals(p: StorageEntry): { used: number; cap: number; files: number } {
    if (!p.state) return { used: 0, cap: 0, files: 0 };
    return {
      used:  p.state.usedBytes,
      cap:   p.state.capacityBytes,
      files: p.state.fileCount,
    };
  }

  function partFraction(p: StorageEntry): number {
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
  {#if experimentRows.length > 0 || gooParts.length > 0}
  <Subheading title="Instruments">
    {#snippet summary()}
      {#if instrumentSummaryTotal > 0}
        <span class="sci__head-files">{instrumentSummaryTotal}</span>
      {:else}
        <span class="sci__head-empty">—</span>
      {/if}
    {/snippet}

      <!-- Flat list — one row per (instrument, experiment) tuple,
           plus one row per goo chamber. No left indent, no per-
           instrument fold; the instrument context lives inline on
           each row's metadata line. -->
      <ul class="sci__exp-list">
        {#each experimentRows as row (row.key)}
          {@const expOpen = isExpOpen(row.partId, row.instrumentIndex, row.experimentId)}
          {@const stateLabel = !row.enabled ? 'OFF' : row.active ? 'ON' : 'IDLE'}
          <li class="sci__exp">
            <div
              class="sci__exp-head"
              role="button"
              tabindex="0"
              aria-expanded={expOpen}
              onclick={() => toggleExp(row.partId, row.instrumentIndex, row.experimentId)}
              onkeydown={(e) => {
                if (e.key === 'Enter' || e.key === ' ') {
                  e.preventDefault();
                  toggleExp(row.partId, row.instrumentIndex, row.experimentId);
                }
              }}
            >
              <div class="sci__exp-row1">
                <span class="sci__exp-label">{row.label}</span>
                <span class="sci__exp-pct">{row.completion}%</span>
                <Chip
                  kind="latch"
                  intent={row.enabled && row.active ? 'ok' : 'idle'}
                  size="sm"
                  pressed={row.enabled}
                  label={stateLabel}
                  minWidth="32px"
                  aria-label={`${row.enabled ? 'Disable' : 'Enable'} ${row.label}`}
                  onclick={(e) => {
                    e.stopPropagation();
                    toggleExperiment(row.partId, row.instrumentIndex, row.experimentId, row.enabled);
                  }}
                />
              </div>
              <div class="sci__exp-row2">
                <span class="sci__exp-instr">{row.instrumentName}</span>
                {#if row.summary}
                  <span class="sci__exp-dot">·</span>
                  <span class="sci__exp-summary">{row.summary}</span>
                {/if}
              </div>
            </div>

            {#if expOpen}
              <div class="sci__exp-body">
                <div class="sci__exp-viz">
                  {#if row.atm}
                    <AtmProfileIndicator state={row.atm} />
                    <AtmTempPlot atm={row.atm} />
                  {:else if row.lts}
                    <LtsOrbitIndicator state={row.lts} />
                  {/if}
                </div>
                {#if row.enabled}
                  <div class="sci__exp-detail">
                    {#if row.atm}
                      <div class="sci__exp-detail-line">
                        <span class="sci__exp-detail-key">LIMITS</span>
                        <span class="sci__exp-detail-val">{atmNow(row.atm)}</span>
                      </div>
                      <div class="sci__exp-detail-line">
                        <span class="sci__exp-detail-key">SEEN</span>
                        <span class="sci__exp-detail-val">{atmSeen(row.atm)}</span>
                      </div>
                    {:else if row.lts}
                      <div class="sci__exp-detail-line">
                        <span class="sci__exp-detail-key">LIMITS</span>
                        <span class="sci__exp-detail-val">{ltsNow(row.lts)}</span>
                      </div>
                      <div class="sci__exp-detail-line">
                        <span class="sci__exp-detail-key">SEEN</span>
                        <span class="sci__exp-detail-val">{ltsSeen(row.lts)}</span>
                      </div>
                    {/if}
                    <div class="sci__exp-detail-line">
                      <span class="sci__exp-detail-key">STORAGE</span>
                      <span
                        class="sci__exp-detail-val"
                        class:sci__exp-detail-val--missing={row.destStorage === ''}
                      >{row.destStorage === '' ? 'NO STORAGE' : row.destStorage}</span>
                    </div>
                  </div>
                {/if}
              </div>
            {/if}
          </li>
        {/each}

        <!-- Mystery Goo chambers — siblings of the experiment rows.
             Each chamber renders as its own self-contained block. -->
        {#each gooParts as p (p.struct.id)}
          {#each p.state?.goo ?? [] as goo, gooIdx (gooIdx)}
            <MysteryGooChamber
              partId={p.struct.id}
              title={p.struct.title}
              {goo}
              onToggle={(open) => setGooCoverOpen(p.struct.id, open)}
            />
          {/each}
        {/each}
      </ul>
  </Subheading>
  {/if}

  <!-- Storage -------------------------------------------------- -->
  {#if storages.length > 0}
  <Subheading title="Storage">
    {#snippet summary()}
      {#if totals.cap > 0}
        <span class="sci__head-files">{totals.fileCount} file{totals.fileCount === 1 ? '' : 's'}</span>
        <span class="sci__head-sep">·</span>
        <span class="sci__head-bytes">{fmtBytes(totals.used)}<em>/{fmtBytes(totals.cap)}</em></span>
      {:else}
        <span class="sci__head-empty">—</span>
      {/if}
    {/snippet}

      <div class="sci__node-gauge">
        <SegmentGauge fraction={aggregateFraction} />
      </div>

        <ul class="sci__rows">
          {#each storages as p (p.struct.id)}
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
                  <Chip
                    kind="action"
                    intent="idle"
                    label="VIEW"
                    aria-label={`View files in ${p.struct.title}`}
                    title={`View files in ${p.struct.title}`}
                    onclick={() => openFiles(p)}
                  />
                </div>
                <div class="sci__row-line sci__row-line--gauge">
                  <SegmentGauge fraction={f} />
                </div>
              </div>
            </li>
          {/each}
        </ul>
  </Subheading>
  {/if}
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

  /* Right-aligned summary spans rendered inside the Subheading
     summary snippet. Same vocabulary the inline node-summary
     used before, just scoped to the new class prefix. */
  .sci__head-files {
    color: var(--fg-dim);
    font-family: var(--font-mono);
    font-size: 9.5px;
    letter-spacing: 0.06em;
    font-variant-numeric: tabular-nums;
  }
  .sci__head-sep { color: var(--fg-mute); }
  .sci__head-bytes {
    font-family: var(--font-mono);
    font-size: 9.5px;
    letter-spacing: 0.04em;
    font-variant-numeric: tabular-nums;
    color: var(--fg-dim);
  }
  .sci__head-bytes em {
    font-style: normal;
    color: var(--fg-mute);
  }
  .sci__head-empty {
    color: var(--fg-mute);
  }

  /* Aggregate-storage gauge sits inside the Storage Subheading
     body, above the per-storage rows. */
  .sci__node-gauge {
    padding: 2px 0 8px;
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

  /* ---- Flat experiment list ---------------------------------
     Every (instrument, experiment) tuple is a top-level row,
     plus one row per goo chamber as a sibling. No left indent
     anywhere — instrument context lives inline on row 2 instead
     of consuming horizontal whitespace via tree depth. */
  .sci__exp-list {
    list-style: none;
    margin: 0;
    padding: 0;
    display: flex;
    flex-direction: column;
  }
  /* Hairline divider between any two rows (experiment OR goo).
     `> * + *` targets every direct child except the first, so
     borders never hang off the last row. */
  .sci__exp-list > * + * {
    border-top: 1px solid var(--line);
  }

  .sci__exp {
    display: flex;
    flex-direction: column;
    padding: 4px 0 6px 0;
  }
  .sci__exp-head {
    cursor: pointer;
    display: flex;
    flex-direction: column;
    gap: 2px;
    padding: 2px 2px;
    transition: background 140ms ease;
  }
  .sci__exp-head:hover,
  .sci__exp-head:focus-visible {
    background: rgba(126, 245, 184, 0.04);
    outline: none;
  }

  /* Row 1: experiment label · completion · status pill. The
     label takes the available width; pct and pill anchor to
     the right edge so the column rhythm holds across rows. */
  .sci__exp-row1 {
    display: flex;
    align-items: center;
    gap: 6px;
  }
  .sci__exp-label {
    flex: 1 1 auto;
    min-width: 0;
    color: var(--fg);
    font-family: var(--font-display);
    font-size: 10.5px;
    letter-spacing: 0.16em;
    text-transform: uppercase;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }
  .sci__exp-head:hover .sci__exp-label,
  .sci__exp-head:focus-visible .sci__exp-label {
    color: var(--accent-soft);
  }

  /* Row 2: instrument name · live summary. Flush-left, single
     line, ellipsis-on-overflow so a long instrument name + a
     long summary degrade gracefully without wrapping. */
  .sci__exp-row2 {
    display: flex;
    align-items: baseline;
    gap: 5px;
    font-family: var(--font-mono);
    font-size: 10px;
    color: var(--fg-mute);
    font-variant-numeric: tabular-nums;
    overflow: hidden;
    white-space: nowrap;
    text-overflow: ellipsis;
  }
  .sci__exp-instr {
    color: var(--fg-dim);
    font-family: var(--font-display);
    font-size: 9.5px;
    letter-spacing: 0.14em;
    text-transform: uppercase;
    flex: 0 0 auto;
  }
  .sci__exp-dot {
    color: var(--fg-mute);
    opacity: 0.6;
    flex: 0 0 auto;
  }
  .sci__exp-summary {
    color: var(--fg-mute);
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
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

  /* Indicator(s) sit in a top row; LIMITS/SEEN/destination lives
     below. Stacking the detail under the visualization keeps each
     visualization free to use its own width (atm-profile + temp plot
     are side-by-side; lts is a single ring) and lets the data block
     stretch the full row width. No left indent — the body sits
     flush with the head, claiming the full readable column. */
  .sci__exp-body {
    margin: 6px 2px 6px 2px;
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

</style>
