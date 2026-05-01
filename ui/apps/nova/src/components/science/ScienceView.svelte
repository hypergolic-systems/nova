<script lang="ts">
  // Science tab body. M4-1 ships only the Storage section; the
  // Experiments panel lands in a follow-up. Visual language matches
  // PowerView (.pwr__) — same chrome, different prefix (.sci__).

  import { useNovaPartsByTag } from '../../telemetry/use-nova-parts-by-tag.svelte';
  import type { NovaTaggedPart } from '../../telemetry/use-nova-parts-by-tag.svelte';
  import type { ScienceFile } from '../../telemetry/nova-topics';
  import ComponentIcon from '../ComponentIcon.svelte';
  import SegmentGauge from '../SegmentGauge.svelte';
  import FileListModal from './FileListModal.svelte';
  import { fmtBytes } from '../../util/units';

  interface Props {
    vesselId: string;
  }
  const { vesselId }: Props = $props();

  const storages = useNovaPartsByTag(() => vesselId, 'science-storage');

  // Aggregate roll-up across every DataStorage on the vessel. The
  // header summary uses these as numerator/denominator and the
  // SegmentGauge under the header reads the same fraction.
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

  let storeOpen = $state(true);
  function toggleStore(): void {
    storeOpen = !storeOpen;
  }

  // The currently-open file list modal — null when no modal is open.
  // The state holds a part id rather than a part reference so reactive
  // updates to the part's state object flow through into the modal
  // body without us re-binding.
  let openModalPartId = $state<string | null>(null);

  function openFiles(p: NovaTaggedPart): void {
    openModalPartId = p.struct.id;
  }
  function closeFiles(): void {
    openModalPartId = null;
  }

  // Live lookup for the currently-open modal: re-derived each frame
  // so the file list, count, and ages refresh while the modal is up.
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

  // Per-row helpers: a part might host multiple DataStorage
  // components in principle (a stack stage with two drives). Fold
  // them into one synthetic row so the player sees one device per
  // part — the modal still lists every file across both drives.
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
</script>

<section class="sci">
  <div class="sci__node">
    <button
      type="button"
      class="sci__node-head"
      aria-expanded={storeOpen}
      onclick={toggleStore}
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

  /* Section header (matches PowerView's .pwr__node-head). The
     left-edge indicator bar and trailing underline rhyme are kept
     so SCI feels like a sibling tab, not a different visual world. */
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

  /* Per-storage rows — taller storage rows mirroring PWR's battery
     pattern (icon + stacked text/gauge). */
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

  /* The VIEW button — small, monospaced, accent-bordered. Mirrors
     PWR's deploy buttons. */
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
