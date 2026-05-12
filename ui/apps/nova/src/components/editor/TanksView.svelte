<script lang="ts">
  // Vessel-wide list of tank parts in the editor's current ShipConstruct.
  // Each tank part is one collapsible row; expanding it reveals the
  // TankRowEditor body where capacity slices and starting amounts are
  // edited. Right-click on a tank in the 3-D view scrolls the matching
  // row into focus and forces it expanded (parent passes `focusPartId`).
  //
  // Pattern lifted from PowerView's hierarchical tree: chevron + display-
  // font heading, mono numerics in the totals column, hover wash on rows.
  // No subgroup nesting — every tank part is a peer at the same level.
  //
  // Per-part data flows through the same NovaPart/<id> topic the flight
  // panels use; in the editor scope, NovaPartTopic.ResolveComponents
  // reads from NovaPartModule.Components rather than the live
  // VirtualVessel, so resource frames (amount/capacity per buffer) and
  // the TankVolume kind frame both arrive populated.
  //
  // Live-apply: each row's onApply prop fans out via `setTankCustom`.

  import { getKsp } from '@dragonglass/telemetry/svelte';
  import { onDestroy, tick } from 'svelte';
  import { useNovaEditorShipStructure } from '../../telemetry/use-nova-editor-ship-structure.svelte';
  import { useKeyedSubscriptions } from '../../telemetry/use-keyed-subscriptions.svelte';
  import {
    NovaPartTopic,
    decodePart,
    type NovaPart,
    type NovaPartStruct,
    type TankCustomEntry,
    type TankSlice,
    type InsulationTier,
  } from '../../telemetry/nova-topics';
  import { resourceMeta } from '../resource/resource-codes';
  import TankRowEditor from './TankRowEditor.svelte';

  interface Props {
    /** Part id from the most recent right-click PAW pulse. When this
     *  changes, the matching row scrolls into view and forces open. */
    focusPartId: string | null;
  }
  const { focusPartId }: Props = $props();

  const ksp = getKsp();
  const structureRef = useNovaEditorShipStructure();

  // Tank parts only — anything tagged 'tank' (set by SystemTags.cs for
  // parts carrying a TankVolume virtual component).
  const tankParts = $derived.by<NovaPartStruct[]>(() => {
    const s = structureRef.current;
    return s ? s.parts.filter((p) => p.tags.includes('tank')) : [];
  });

  // Per-tank-part NovaPart subscription. Provides resource frames
  // (one per Buffer = one per slice) and a TankVolume "T" component
  // frame carrying the part's geometric volume.
  const partStates = useKeyedSubscriptions<string, ReturnType<typeof decodePart> extends NovaPart ? unknown : never, NovaPart>(
    () => tankParts.map((p) => p.id),
    (id) => NovaPartTopic(id),
    (frame) => decodePart(frame as Parameters<typeof decodePart>[0]),
  );

  // Slice list comes directly from the TankVolume component frame
  // on the wire — the tank's own buffers, not state.resources (which
  // mixes EVERY buffer on the part, including unrelated Battery EC).
  // Order matches Buffer order on the C# side, which mirrors the
  // proto round-trip — stable across reconfigures.
  function slicesOf(state: NovaPart | undefined): TankSlice[] {
    return state?.tank?.[0]?.slices ?? [];
  }
  function volumeOf(state: NovaPart | undefined): number {
    return state?.tank?.[0]?.volume ?? 0;
  }

  // Compact per-row summary for the collapsed head: resource codes
  // separated by `+`, e.g. "RP1 + LOX" or "LH2".
  function mixSummary(slices: TankSlice[]): string {
    if (slices.length === 0) return '—';
    const codes = new Set<string>();
    for (const s of slices) codes.add(resourceMeta(s.resource).code);
    return Array.from(codes).join(' + ');
  }
  // Sum of contents / sum of capacity → fill fraction for the head.
  function fillFraction(slices: TankSlice[]): number {
    let cap = 0, fill = 0;
    for (const s of slices) { cap += s.capacity; fill += s.contents; }
    return cap > 0 ? fill / cap : 0;
  }

  // Per-row expansion state. New parts default to closed; the focus
  // pulse from PawTopic forces a row open.
  let expanded = $state<Record<string, boolean>>({});
  function toggle(id: string): void {
    expanded[id] = !(expanded[id] ?? false);
  }

  // Right-click → focus + auto-expand. tick() lets the row mount before
  // we scroll. `rowEls` is a $state object so `bind:this` writes are
  // tracked — Svelte 5 warns on `bind:this` to a non-reactive container
  // (the binding wouldn't fire downstream effects on remount otherwise).
  let rowEls = $state<Record<string, HTMLLIElement | null>>({});
  $effect(() => {
    const id = focusPartId;
    if (!id) return;
    expanded[id] = true;
    tick().then(() => {
      const el = rowEls[id];
      if (el) el.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
    });
  });

  function applyTankCustom(partId: string, entries: TankCustomEntry[]): void {
    ksp.send(NovaPartTopic(partId), 'setTankCustom', entries);
  }
  // Tier vector fires right after setTankCustom — the mod-side
  // `Reconfigure(buffers)` resets every slice's tier to MLI as part of
  // the loadout swap, so we re-apply the desired tiers in the same
  // burst. Order is enforced by TankRowEditor's single $effect.
  function applyTankInsulation(partId: string, tiers: InsulationTier[]): void {
    ksp.send(NovaPartTopic(partId), 'setTankInsulation', tiers);
  }

  // Highlight tracking — placeholder. The editor scene's stage-highlight
  // hook isn't wired through Dragonglass yet; logging here lets us trace
  // the gesture without producing visible behavior. Hover→row reads as
  // a row-level affordance regardless.
  function highlightOn(_id: string): void {}
  function highlightOff(): void {}
  onDestroy(() => highlightOff());
</script>

{#snippet chev(open: boolean)}
  <svg class="tv__chev" class:tv__chev--open={open}
       viewBox="0 0 8 8" aria-hidden="true">
    <path d="M2.2 1.4 L5.8 4 L2.2 6.6" fill="none" stroke="currentColor"
          stroke-width="1.25" stroke-linecap="round" stroke-linejoin="round" />
  </svg>
{/snippet}

<section class="tv">
  {#if tankParts.length === 0}
    <p class="tv__empty">
      <span class="tv__empty-rule"></span>
      <span class="tv__empty-text">NO TANKS</span>
      <span class="tv__empty-rule"></span>
    </p>
  {:else}
    <ul class="tv__rows">
      {#each tankParts as p (p.id)}
        {@const state = partStates.get(p.id)}
        {@const slices = slicesOf(state)}
        {@const volume = volumeOf(state)}
        {@const isOpen = expanded[p.id] ?? false}
        <li class="tv__row"
            bind:this={rowEls[p.id]}
            onmouseenter={() => highlightOn(p.id)}
            onmouseleave={highlightOff}>
          <button type="button" class="tv__row-head"
                  aria-expanded={isOpen}
                  onclick={() => toggle(p.id)}>
            {@render chev(isOpen)}
            <span class="tv__row-name">{p.title}</span>
            <span class="tv__row-mix">{mixSummary(slices)}</span>
            <span class="tv__row-fill"
                  class:tv__row-fill--low={fillFraction(slices) < 0.05}>
              {Math.round(fillFraction(slices) * 100)}<em>%</em>
            </span>
          </button>
          {#if isOpen && volume > 0}
            <TankRowEditor
              partId={p.id}
              partTitle={p.title}
              {volume}
              tanks={slices}
              onApply={(entries) => applyTankCustom(p.id, entries)}
              onApplyTiers={(tiers) => applyTankInsulation(p.id, tiers)}
            />
          {/if}
        </li>
      {/each}
    </ul>
  {/if}
</section>

<style>
  .tv {
    display: flex;
    flex-direction: column;
    gap: 0;
  }

  .tv__rows {
    list-style: none;
    margin: 0;
    padding: 0;
    display: flex;
    flex-direction: column;
  }

  .tv__row {
    border-bottom: 1px solid rgba(26, 35, 53, 0.55);
  }
  .tv__row:last-child { border-bottom: 0; }

  .tv__row-head {
    appearance: none;
    background: transparent;
    border: none;
    width: 100%;
    padding: 6px 4px;
    color: inherit;
    font: inherit;
    text-align: left;
    cursor: pointer;
    display: flex;
    align-items: center;
    gap: 8px;
    position: relative;
    transition: background 200ms cubic-bezier(0.4, 0, 0.2, 1);
  }
  .tv__row-head::before {
    content: '';
    position: absolute;
    left: 0;
    top: 4px;
    bottom: 4px;
    width: 2px;
    background: var(--accent);
    opacity: 0;
    transform: scaleY(0);
    transform-origin: top;
    transition:
      opacity 220ms cubic-bezier(0.4, 0, 0.2, 1),
      transform 280ms cubic-bezier(0.16, 1, 0.3, 1);
  }
  .tv__row-head:hover { background: rgba(126, 245, 184, 0.04); }
  .tv__row-head:hover::before {
    opacity: 0.7;
    transform: scaleY(1);
  }
  .tv__row-head[aria-expanded='true']::before {
    opacity: 0.5;
    transform: scaleY(1);
    background: var(--accent-dim);
  }
  .tv__row-head:focus-visible { outline: none; }

  .tv__chev {
    flex: 0 0 8px;
    width: 8px;
    height: 8px;
    color: var(--fg-mute);
    transition:
      transform 280ms cubic-bezier(0.16, 1, 0.3, 1),
      color 220ms cubic-bezier(0.4, 0, 0.2, 1),
      filter 220ms cubic-bezier(0.4, 0, 0.2, 1);
  }
  .tv__chev--open { transform: rotate(90deg); }
  .tv__row-head:hover .tv__chev {
    color: var(--accent);
    filter: drop-shadow(0 0 3px var(--accent-glow));
  }

  .tv__row-name {
    flex: 1 1 auto;
    min-width: 0;
    color: var(--fg);
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }
  .tv__row-mix {
    flex: 0 0 auto;
    font-family: var(--font-display);
    font-size: 10px;
    letter-spacing: 0.16em;
    color: var(--fg-dim);
  }
  .tv__row-fill {
    flex: 0 0 auto;
    min-width: 32px;
    text-align: right;
    font-variant-numeric: tabular-nums;
    color: var(--accent);
  }
  .tv__row-fill--low { color: var(--warn); }
  .tv__row-fill em {
    font-style: normal;
    font-size: 9px;
    color: var(--fg-dim);
    margin-left: 2px;
    letter-spacing: 0.14em;
  }

  /* Empty state — same etched-rule pattern PowerView uses. */
  .tv__empty {
    display: flex;
    align-items: center;
    gap: 8px;
    margin: 18px 0;
    color: var(--fg-mute);
    font-family: var(--font-display);
    font-size: 10px;
    letter-spacing: 0.22em;
  }
  .tv__empty-rule {
    flex: 1 1 auto;
    height: 1px;
    background: var(--line);
  }
  .tv__empty-text { flex: 0 0 auto; }
</style>
