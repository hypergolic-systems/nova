<script lang="ts">
  // Atmospheric Profile visualization for one body. Layers stacked
  // bottom→top (matches the in-flight `AtmProfileIndicator` orientation
  // — troposphere on bottom, mesosphere on top). Each row: layer name,
  // fidelity meter, count "X/N km observed" pulled from fidelity if a
  // record exists. Hovering a row pops the SubjectRow detail directly
  // beneath, integrated into the same stack.

  import type { ArchiveSubject } from '../../telemetry/nova-topics';
  import SubjectRow from './SubjectRow.svelte';

  interface Props {
    subjects: ArchiveSubject[];
  }
  const { subjects }: Props = $props();

  // Layers arrive bottom-to-top from the wire. Render top-down so
  // the visual orientation matches a vertical altitude axis (sky
  // above, ground below).
  const ordered = $derived([...subjects].reverse());

  // Sticky hover/focus highlight — clicking a row pins it; clicking
  // again or another row reassigns. This is the "integrate, don't
  // append" detail surface: the chosen row shows its SubjectRow
  // beneath without growing a separate panel.
  let activeIdx = $state<number>(-1);
  function pickIdx(i: number): void {
    activeIdx = activeIdx === i ? -1 : i;
  }
  function bottomUpIndex(reversedIdx: number): number {
    return ordered.length - 1 - reversedIdx;
  }
</script>

<div class="atm">
  {#each ordered as s, i (s.variant)}
    {@const archived = s.fidelity > 0}
    {@const pct = Math.round(Math.min(1, Math.max(0, s.fidelity)) * 100)}
    {@const isActive = activeIdx === bottomUpIndex(i)}
    <button
      type="button"
      class="atm__row"
      class:atm__row--archived={archived}
      class:atm__row--active={isActive}
      onclick={() => pickIdx(bottomUpIndex(i))}
    >
      <span class="atm__name">{s.variant}</span>
      <span class="atm__bar" aria-hidden="true">
        <span class="atm__fill" style:width={`${pct}%`}></span>
      </span>
      <span class="atm__pct">{archived ? `${pct}%` : '·'}</span>
    </button>
  {/each}
  {#if activeIdx >= 0 && subjects[activeIdx]}
    <div class="atm__detail">
      <SubjectRow subject={subjects[activeIdx]} variantLabel={subjects[activeIdx].variant} />
    </div>
  {/if}
</div>

<style>
  .atm {
    display: flex;
    flex-direction: column;
    gap: 0;
    padding-left: 2px;
  }
  .atm__row {
    appearance: none;
    background: transparent;
    border: none;
    border-left: 2px solid transparent;
    color: inherit;
    font: inherit;
    text-align: left;
    cursor: pointer;
    display: grid;
    grid-template-columns: 6.5em 1fr 3em;
    align-items: center;
    column-gap: 8px;
    padding: 2px 6px 2px 4px;
    transition: background 140ms ease, border-left-color 140ms ease;
  }
  .atm__row:hover,
  .atm__row:focus-visible {
    background: rgba(126, 245, 184, 0.05);
    border-left-color: var(--accent-dim);
    outline: none;
  }
  .atm__row--active {
    background: rgba(126, 245, 184, 0.07);
    border-left-color: var(--accent);
  }
  .atm__name {
    font-family: var(--font-mono);
    font-size: 10px;
    color: var(--fg-dim);
    letter-spacing: 0.06em;
    text-transform: lowercase;
  }
  .atm__row--archived .atm__name {
    color: var(--fg);
  }
  .atm__bar {
    position: relative;
    height: 6px;
    background: rgba(0, 0, 0, 0.4);
    border: 1px solid var(--line);
    overflow: hidden;
  }
  .atm__fill {
    position: absolute;
    inset: 0 auto 0 0;
    background: linear-gradient(90deg, var(--accent-dim), var(--accent));
    box-shadow: 0 0 4px var(--accent-glow);
  }
  .atm__pct {
    font-family: var(--font-mono);
    font-size: 10px;
    color: var(--accent);
    text-align: right;
    font-variant-numeric: tabular-nums;
  }
  .atm__row:not(.atm__row--archived) .atm__pct {
    color: var(--fg-mute);
  }
  .atm__detail {
    margin: 4px 0 2px 4px;
    padding: 4px 4px 4px 8px;
    border-left: 1px solid var(--accent-dim);
    background: rgba(126, 245, 184, 0.03);
  }
</style>
