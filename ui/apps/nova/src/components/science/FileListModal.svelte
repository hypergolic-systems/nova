<script lang="ts">
  // Per-storage file list. Lives in a modal so it doesn't crowd the
  // SCI tab's main view — the player asks for it via the [VIEW]
  // button on a storage row, then dismisses.
  //
  // Files are read directly from the parent's ScienceFile array (no
  // separate fetch — the per-part NovaPart frame inlines them, which
  // keeps the live age column ticking without re-subscribing).

  import type { ScienceFile } from '../../telemetry/nova-topics';
  import { fmtDuration, fmtMag } from '../../util/units';
  import Modal from '../Modal.svelte';

  interface Props {
    open: boolean;
    /** Title of the storage device this modal lists files for. */
    storageName: string;
    files: ScienceFile[];
    onClose: () => void;
  }
  const { open, storageName, files, onClose }: Props = $props();

  // Experiment chips for filtering. Recomputed from the live file
  // list so it always reflects what's actually in storage. Sorted by
  // descending count so the most prevalent experiment chip leads.
  const experiments = $derived.by(() => {
    const counts = new Map<string, number>();
    for (const f of files) counts.set(f.experimentId, (counts.get(f.experimentId) ?? 0) + 1);
    return [...counts.entries()]
      .sort((a, b) => b[1] - a[1])
      .map(([id, count]) => ({ id, count }));
  });

  let activeFilter = $state<string | null>(null);

  // Reset filter when the modal closes — opening it on a different
  // storage shouldn't carry stale state. (The chip won't render if
  // the new storage doesn't have that experiment, but the var would
  // still leak — clear it.)
  $effect(() => {
    if (!open) activeFilter = null;
  });

  type SortKey = 'subject' | 'fidelity' | 'produced';
  let sortBy = $state<SortKey>('produced');
  let sortDir = $state<'asc' | 'desc'>('desc');

  function clickSort(key: SortKey): void {
    if (sortBy === key) {
      sortDir = sortDir === 'asc' ? 'desc' : 'asc';
    } else {
      sortBy = key;
      sortDir = key === 'subject' ? 'asc' : 'desc';
    }
  }

  const visible = $derived.by(() => {
    let out = activeFilter
      ? files.filter((f) => f.experimentId === activeFilter)
      : files.slice();

    out.sort((a, b) => {
      let cmp = 0;
      if (sortBy === 'subject')   cmp = a.subjectId.localeCompare(b.subjectId);
      else if (sortBy === 'fidelity') cmp = a.fidelity - b.fidelity;
      else /* produced */         cmp = a.producedAt - b.producedAt;
      return sortDir === 'asc' ? cmp : -cmp;
    });
    return out;
  });

  // Map experimentId → palette accent colour. atm-profile reads as
  // accent green (live atmospheric sensing), lts as warn amber
  // (slow-cooked time-series), anything new falls back to accent.
  function paletteFor(experimentId: string): string {
    switch (experimentId) {
      case 'atm-profile': return 'var(--accent)';
      case 'lts':         return 'var(--warn)';
      default:            return 'var(--accent)';
    }
  }
</script>

<Modal
  {open}
  title={storageName}
  subtitle="{files.length} file{files.length === 1 ? '' : 's'}"
  {onClose}
>
  <div class="fl">
    {#if files.length === 0}
      <p class="fl__empty">No files yet.</p>
    {:else}
      {#if experiments.length > 1}
        <div class="fl__filter" role="toolbar" aria-label="Filter by experiment">
          <button
            type="button"
            class="fl__chip"
            class:fl__chip--on={activeFilter === null}
            onclick={() => (activeFilter = null)}
          >ALL <em>{files.length}</em></button>
          {#each experiments as exp (exp.id)}
            <button
              type="button"
              class="fl__chip"
              class:fl__chip--on={activeFilter === exp.id}
              style:--fl-chip-tint={paletteFor(exp.id)}
              onclick={() => (activeFilter = activeFilter === exp.id ? null : exp.id)}
            >{exp.id.toUpperCase()} <em>{exp.count}</em></button>
          {/each}
        </div>
      {/if}

      <div class="fl__table" role="table">
        <div class="fl__head" role="row">
          <button
            type="button"
            class="fl__col fl__col--subject fl__sort"
            class:fl__sort--active={sortBy === 'subject'}
            onclick={() => clickSort('subject')}
          >SUBJECT{#if sortBy === 'subject'}<em>{sortDir === 'asc' ? '↑' : '↓'}</em>{/if}</button>
          <button
            type="button"
            class="fl__col fl__col--fidelity fl__sort"
            class:fl__sort--active={sortBy === 'fidelity'}
            onclick={() => clickSort('fidelity')}
          >FID{#if sortBy === 'fidelity'}<em>{sortDir === 'asc' ? '↑' : '↓'}</em>{/if}</button>
          <button
            type="button"
            class="fl__col fl__col--age fl__sort"
            class:fl__sort--active={sortBy === 'produced'}
            onclick={() => clickSort('produced')}
          >T+{#if sortBy === 'produced'}<em>{sortDir === 'asc' ? '↑' : '↓'}</em>{/if}</button>
        </div>
        <ul class="fl__rows">
          {#each visible as f (f.subjectId)}
            <li
              class="fl__row"
              role="row"
              style:--fl-row-tint={paletteFor(f.experimentId)}
            >
              <span class="fl__col fl__col--subject" role="cell">
                {f.subjectId}
              </span>
              <span class="fl__col fl__col--fidelity" role="cell">
                <span class="fl__fid-bar">
                  <span class="fl__fid-fill" style:width="{Math.round(f.fidelity * 100)}%"></span>
                </span>
                <span class="fl__fid-num">{fmtMag(f.fidelity)}</span>
              </span>
              <span class="fl__col fl__col--age" role="cell">
                {fmtDuration(f.producedAt)}
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
    gap: 10px;
    min-width: 0;
  }

  .fl__empty {
    margin: 24px 0;
    text-align: center;
    color: var(--fg-mute);
    font-family: var(--font-display);
    letter-spacing: 0.22em;
  }

  /* ---- Filter chips. Compact, inherit `--fl-chip-tint` per chip
         so the active background and underline pick up the
         experiment's palette colour. ---- */
  .fl__filter {
    display: flex;
    flex-wrap: wrap;
    gap: 4px;
  }
  .fl__chip {
    appearance: none;
    background: transparent;
    border: 1px solid var(--line);
    color: var(--fg-dim);
    font-family: var(--font-display);
    font-size: 10px;
    letter-spacing: 0.16em;
    padding: 3px 8px;
    cursor: pointer;
    --fl-chip-tint: var(--accent);
    transition:
      color 140ms ease,
      border-color 140ms ease,
      background 140ms ease;
  }
  .fl__chip em {
    font-style: normal;
    margin-left: 6px;
    color: var(--fg-mute);
    font-variant-numeric: tabular-nums;
  }
  .fl__chip:hover {
    color: var(--fl-chip-tint);
    border-color: color-mix(in srgb, var(--fl-chip-tint) 40%, var(--line));
  }
  .fl__chip--on {
    color: var(--fl-chip-tint);
    border-color: var(--fl-chip-tint);
    background: color-mix(in srgb, var(--fl-chip-tint) 8%, transparent);
    text-shadow: 0 0 6px color-mix(in srgb, var(--fl-chip-tint) 50%, transparent);
  }
  .fl__chip--on em {
    color: var(--fl-chip-tint);
  }

  /* ---- Table. Plain CSS grid: subject (1fr) · fidelity (140px) ·
         age (84px). The header is a row of buttons (sortable)
         using the same grid; each row is also a grid so columns
         stay aligned without a real <table>. ---- */
  .fl__table {
    display: flex;
    flex-direction: column;
    gap: 0;
    margin-top: 4px;
  }
  .fl__head {
    display: grid;
    grid-template-columns: 1fr 140px 84px;
    gap: 12px;
    align-items: baseline;
    border-bottom: 1px solid var(--line);
    padding: 0 4px 4px;
    margin-bottom: 4px;
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

  .fl__rows {
    list-style: none;
    margin: 0;
    padding: 0;
    display: flex;
    flex-direction: column;
  }
  .fl__row {
    display: grid;
    grid-template-columns: 1fr 140px 84px;
    gap: 12px;
    align-items: center;
    padding: 4px;
    --fl-row-tint: var(--accent);
    border-left: 2px solid transparent;
    transition:
      background 140ms ease,
      border-color 140ms ease;
  }
  .fl__row:hover {
    background: color-mix(in srgb, var(--fl-row-tint) 6%, transparent);
    border-left-color: var(--fl-row-tint);
  }
  .fl__col--subject {
    font-family: var(--font-mono);
    font-size: 11px;
    color: var(--fl-row-tint);
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }
  .fl__col--fidelity {
    display: flex;
    align-items: center;
    gap: 8px;
  }
  .fl__fid-bar {
    flex: 1 1 auto;
    height: 6px;
    background: rgba(0, 0, 0, 0.4);
    border: 1px solid var(--line);
    overflow: hidden;
    position: relative;
  }
  .fl__fid-fill {
    display: block;
    height: 100%;
    background: linear-gradient(90deg,
      color-mix(in srgb, var(--fl-row-tint) 60%, black 40%) 0%,
      var(--fl-row-tint) 100%);
    box-shadow: 0 0 5px color-mix(in srgb, var(--fl-row-tint) 50%, transparent);
  }
  .fl__fid-num {
    flex: 0 0 auto;
    font-family: var(--font-mono);
    font-variant-numeric: tabular-nums;
    color: var(--fg);
    width: 32px;
    text-align: right;
  }
  .fl__col--age {
    color: var(--fg-dim);
    font-variant-numeric: tabular-nums;
    font-size: 10px;
    text-align: right;
    white-space: nowrap;
  }
</style>
