<script lang="ts">
  // Per-storage file list. Lives in a full-page modal so the player
  // can scan tens of files without crowding the side panel. Reads
  // straight from the parent's ScienceFile array — files come down
  // inlined on the per-part frame, so the table updates live as the
  // experiment writes new ones.

  import type { ScienceFile } from '../../telemetry/nova-topics';
  import { fmtMag, fmtBytes, fmtDuration } from '../../util/units';
  import Modal from '../Modal.svelte';

  interface Props {
    open: boolean;
    /** Title of the storage device this modal lists files for. */
    storageName: string;
    files: ScienceFile[];
    onClose: () => void;
  }
  const { open, storageName, files, onClose }: Props = $props();

  // -- Subject decoding -------------------------------------------
  // Subject ids are wire-format strings: "atm-profile@Kerbin:tropo"
  // or "lts@Kerbin:SrfLanded:7". The UI never shows them raw — we
  // synthesise a Body / Experiment / Regime path so the player sees
  // information, not encoding. Failing to parse falls back to "—"
  // rather than leaking the raw string.

  interface DecodedSubject {
    body: string;
    experimentLabel: string;
    regime: string;
  }

  const EXPERIMENT_LABELS: Record<string, string> = {
    'atm-profile': 'Atmospheric Profile',
    'lts':         'Long-Term Study',
  };

  // Stock atm-profile layer names → display titles. The wire form is
  // lowercase; UI rendering capitalises and adds context where it
  // helps ("Troposphere" reads better than "troposphere" in a column).
  const LAYER_LABELS: Record<string, string> = {
    troposphere:  'Troposphere',
    stratosphere: 'Stratosphere',
    mesosphere:   'Mesosphere',
  };

  // Stock ExperimentSituations enum names → display titles.
  const SITUATION_LABELS: Record<string, string> = {
    SrfLanded:   'Landed',
    SrfSplashed: 'Splashed',
    FlyingLow:   'Flying low',
    FlyingHigh:  'Flying high',
    InSpaceLow:  'Low orbit',
    InSpaceHigh: 'High orbit',
  };

  function decodeSubject(subjectId: string, experimentId: string): DecodedSubject | null {
    // "<exp>@<body>:<variant>[:<slice>]"
    const at = subjectId.indexOf('@');
    if (at < 0) return null;
    const rest = subjectId.slice(at + 1);
    const firstColon = rest.indexOf(':');
    if (firstColon < 0) return null;
    const body = rest.slice(0, firstColon);
    const tail = rest.slice(firstColon + 1);

    const secondColon = tail.indexOf(':');
    let variant: string;
    let slice: number | null = null;
    if (secondColon < 0) {
      variant = tail;
    } else {
      variant = tail.slice(0, secondColon);
      const n = parseInt(tail.slice(secondColon + 1), 10);
      slice = Number.isFinite(n) ? n : null;
    }

    let regime: string;
    if (experimentId === 'atm-profile') {
      regime = LAYER_LABELS[variant] ?? variant;
    } else if (experimentId === 'lts') {
      const sit = SITUATION_LABELS[variant] ?? variant;
      regime = slice != null ? `${sit} · slice ${slice}` : sit;
    } else {
      regime = slice != null ? `${variant} · ${slice}` : variant;
    }

    return {
      body,
      experimentLabel: EXPERIMENT_LABELS[experimentId] ?? experimentId,
      regime,
    };
  }

  // Hardcoded file-size lookup for the Size column. Mirrors the
  // mod-side constants on each Experiment class — if those change,
  // update here.
  function sizeBytes(experimentId: string): number {
    if (experimentId === 'atm-profile') return 1_000;
    if (experimentId === 'lts')         return 5_000;
    return 1_000;
  }

  // Fidelity colour by value. Mirrors SegmentGauge severity bands
  // so the readout stays honest across components.
  function fidelityColour(f: number): string {
    if (f >= 0.7) return 'var(--accent)';
    if (f >= 0.3) return 'var(--warn)';
    return 'var(--alert)';
  }

  // Sortable rows. Decode once per file then sort over decoded fields.
  type SortKey = 'instrument' | 'body' | 'experiment' | 'regime' | 'fidelity' | 'produced';
  let sortBy = $state<SortKey>('produced');
  let sortDir = $state<'asc' | 'desc'>('desc');

  function clickSort(key: SortKey): void {
    if (sortBy === key) {
      sortDir = sortDir === 'asc' ? 'desc' : 'asc';
    } else {
      sortBy = key;
      sortDir = key === 'fidelity' || key === 'produced' ? 'desc' : 'asc';
    }
  }

  interface Row {
    file: ScienceFile;
    decoded: DecodedSubject | null;
    bytes: number;
  }

  const rows = $derived.by<Row[]>(() => {
    return files.map((f) => ({
      file: f,
      decoded: decodeSubject(f.subjectId, f.experimentId),
      bytes: sizeBytes(f.experimentId),
    }));
  });

  const visible = $derived.by(() => {
    const out = rows.slice();
    out.sort((a, b) => {
      let cmp = 0;
      switch (sortBy) {
        case 'instrument':
          cmp = a.file.instrument.localeCompare(b.file.instrument);
          break;
        case 'body':
          cmp = (a.decoded?.body ?? '').localeCompare(b.decoded?.body ?? '');
          break;
        case 'experiment':
          cmp = (a.decoded?.experimentLabel ?? '').localeCompare(b.decoded?.experimentLabel ?? '');
          break;
        case 'regime':
          cmp = (a.decoded?.regime ?? '').localeCompare(b.decoded?.regime ?? '');
          break;
        case 'fidelity':
          cmp = a.file.fidelity - b.file.fidelity;
          break;
        case 'produced':
          cmp = a.file.producedAt - b.file.producedAt;
          break;
      }
      return sortDir === 'asc' ? cmp : -cmp;
    });
    return out;
  });

  // Total size across all files in this storage. Surfaced in the
  // subtitle so the modal echoes the storage row's headline number.
  const totalBytes = $derived(rows.reduce((s, r) => s + r.bytes, 0));
</script>

<Modal
  {open}
  title={storageName}
  subtitle="{files.length} file{files.length === 1 ? '' : 's'} · {fmtBytes(totalBytes)}"
  {onClose}
>
  <div class="fl">
    {#if files.length === 0}
      <p class="fl__empty">No files yet.</p>
    {:else}
      <div class="fl__table" role="table">
        <div class="fl__head" role="row">
          <button
            type="button"
            class="fl__col fl__col--instrument fl__sort"
            class:fl__sort--active={sortBy === 'instrument'}
            onclick={() => clickSort('instrument')}
          >INSTRUMENT{#if sortBy === 'instrument'}<em>{sortDir === 'asc' ? '↑' : '↓'}</em>{/if}</button>
          <button
            type="button"
            class="fl__col fl__col--body fl__sort"
            class:fl__sort--active={sortBy === 'body'}
            onclick={() => clickSort('body')}
          >BODY{#if sortBy === 'body'}<em>{sortDir === 'asc' ? '↑' : '↓'}</em>{/if}</button>
          <button
            type="button"
            class="fl__col fl__col--exp fl__sort"
            class:fl__sort--active={sortBy === 'experiment'}
            onclick={() => clickSort('experiment')}
          >EXPERIMENT{#if sortBy === 'experiment'}<em>{sortDir === 'asc' ? '↑' : '↓'}</em>{/if}</button>
          <button
            type="button"
            class="fl__col fl__col--regime fl__sort"
            class:fl__sort--active={sortBy === 'regime'}
            onclick={() => clickSort('regime')}
          >REGIME{#if sortBy === 'regime'}<em>{sortDir === 'asc' ? '↑' : '↓'}</em>{/if}</button>
          <button
            type="button"
            class="fl__col fl__col--fid fl__sort"
            class:fl__sort--active={sortBy === 'fidelity'}
            onclick={() => clickSort('fidelity')}
          >FID{#if sortBy === 'fidelity'}<em>{sortDir === 'asc' ? '↑' : '↓'}</em>{/if}</button>
          <span class="fl__col fl__col--size">SIZE</span>
          <button
            type="button"
            class="fl__col fl__col--age fl__sort"
            class:fl__sort--active={sortBy === 'produced'}
            onclick={() => clickSort('produced')}
          >T+{#if sortBy === 'produced'}<em>{sortDir === 'asc' ? '↑' : '↓'}</em>{/if}</button>
        </div>
        <ul class="fl__rows">
          {#each visible as r (r.file.subjectId)}
            <li class="fl__row" role="row">
              <span class="fl__col fl__col--instrument" role="cell">
                {r.file.instrument || '—'}
              </span>
              <span class="fl__col fl__col--body" role="cell">
                {r.decoded?.body ?? '—'}
              </span>
              <span class="fl__col fl__col--exp" role="cell">
                {r.decoded?.experimentLabel ?? r.file.experimentId}
              </span>
              <span class="fl__col fl__col--regime" role="cell">
                {r.decoded?.regime ?? r.file.subjectId}
              </span>
              <span
                class="fl__col fl__col--fid"
                role="cell"
                style:color={fidelityColour(r.file.fidelity)}
              >{fmtMag(r.file.fidelity)}</span>
              <span class="fl__col fl__col--size" role="cell">
                {fmtBytes(r.bytes)}
              </span>
              <span class="fl__col fl__col--age" role="cell">
                {fmtDuration(r.file.producedAt)}
              </span>
            </li>
          {/each}
        </ul>
      </div>
    {/if}
  </div>
</Modal>

<style>
  .fl {
    display: flex;
    flex-direction: column;
    min-width: 0;
    height: 100%;
  }

  .fl__empty {
    margin: 64px 0;
    text-align: center;
    color: var(--fg-mute);
    font-family: var(--font-display);
    letter-spacing: 0.22em;
  }

  /* Six-column grid: body · experiment · regime (flexes) · fid · size · t+.
     Numeric columns (fid / size / t+) get fixed widths so values
     align across rows; text columns share whatever's left, with
     regime taking the largest share since it's the most variable. */
  .fl__table {
    display: flex;
    flex-direction: column;
    flex: 1 1 auto;
    min-height: 0;
  }
  .fl__head,
  .fl__row {
    display: grid;
    grid-template-columns:
      minmax(140px, 1.4fr)     /* instrument */
      minmax(80px, 1fr)        /* body */
      minmax(140px, 1.4fr)     /* experiment */
      minmax(160px, 2fr)       /* regime */
      72px                     /* fid */
      72px                     /* size */
      88px;                    /* t+ */
    gap: 16px;
    align-items: baseline;
  }
  .fl__head {
    border-bottom: 1px solid var(--line);
    padding: 4px 6px 6px;
    margin-bottom: 4px;
    flex: 0 0 auto;
  }
  .fl__sort {
    appearance: none;
    background: transparent;
    border: none;
    text-align: left;
    color: var(--fg-mute);
    font-family: var(--font-display);
    font-size: 10px;
    letter-spacing: 0.20em;
    padding: 0;
    cursor: pointer;
    transition: color 140ms ease;
  }
  .fl__sort:hover { color: var(--accent-soft); }
  .fl__sort--active { color: var(--accent); }
  .fl__sort em {
    font-style: normal;
    margin-left: 4px;
    font-size: 9px;
  }
  .fl__col--size {
    color: var(--fg-mute);
    font-family: var(--font-display);
    font-size: 10px;
    letter-spacing: 0.20em;
    text-align: right;
  }

  .fl__rows {
    list-style: none;
    margin: 0;
    padding: 0;
    display: flex;
    flex-direction: column;
    flex: 1 1 auto;
    min-height: 0;
    overflow-y: auto;
  }
  .fl__row {
    padding: 6px 6px;
    border-left: 2px solid transparent;
    transition:
      background 140ms ease,
      border-color 140ms ease;
  }
  .fl__row:hover {
    background: rgba(126, 245, 184, 0.04);
    border-left-color: var(--accent-dim);
  }
  .fl__col--instrument {
    color: var(--fg);
    font-family: var(--font-mono);
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }
  .fl__col--body {
    color: var(--fg);
    font-family: var(--font-mono);
  }
  .fl__col--exp {
    color: var(--fg-dim);
    font-family: var(--font-mono);
  }
  .fl__col--regime {
    color: var(--fg);
    font-family: var(--font-mono);
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }
  .fl__col--fid {
    font-family: var(--font-mono);
    font-variant-numeric: tabular-nums;
    text-align: right;
    text-shadow: 0 0 6px currentColor;
  }
  .fl__col--age {
    color: var(--fg-dim);
    font-variant-numeric: tabular-nums;
    font-size: 11px;
    text-align: right;
    white-space: nowrap;
  }
  /* Body/row cells override the header `.fl__col--size` color tone. */
  .fl__row .fl__col--size {
    color: var(--fg-dim);
    font-family: var(--font-mono);
    font-size: 11px;
    letter-spacing: 0;
    text-transform: none;
    font-variant-numeric: tabular-nums;
  }

  .fl__rows::-webkit-scrollbar {
    width: 8px;
  }
  .fl__rows::-webkit-scrollbar-track {
    background: rgba(0, 0, 0, 0.25);
    border-left: 1px solid var(--line);
  }
  .fl__rows::-webkit-scrollbar-thumb {
    background: rgba(126, 245, 184, 0.18);
    border: 1px solid transparent;
    background-clip: padding-box;
  }
  .fl__rows::-webkit-scrollbar-thumb:hover {
    background: rgba(126, 245, 184, 0.42);
    background-clip: padding-box;
  }
</style>
