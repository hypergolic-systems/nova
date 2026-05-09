<script lang="ts">
  // Expanded body of a tank-part row in the Editor Vessel window.
  // Two nested subsections under the parent row:
  //   1. CAPACITY  — stacked segmented bar where each slice is a
  //                  resource allocation, labelled inline. Drag the
  //                  right edge of a slice to grow/shrink it against
  //                  the trailing unused-space tile (`FREE`); right-
  //                  click `FREE` to add a resource via context menu.
  //                  Slices are always sorted alphabetically by
  //                  canonical resource name so the layout is stable
  //                  across edits.
  //   2. CONTENTS  — one sub-row per slice. The segmented gauge IS
  //                  the slider — drag along its length to set the
  //                  starting amount; FULL / EMPTY are quick-pegs.
  //
  // Footer carries cumulative dry+wet mass and a REVERT. Live-applies
  // on every meaningful change with a 250 ms debounce so a drag doesn't
  // flood the wire op.
  //
  // No telemetry imports here — `onApply` carries the wire payload up
  // to the parent (which owns the `ksp.send` call). Volume is fixed
  // by the part prefab and therefore read-only; the only knobs are
  // capacity slices and starting amounts.

  import { onDestroy } from 'svelte';
  import SegmentGauge from '../SegmentGauge.svelte';
  import { resourceMeta } from '../resource/resource-codes';
  import { ContextMenu, type MenuItem } from '@dragonglass/instruments';
  import type { TankCustomEntry } from '../../telemetry/nova-topics';

  export interface TankSlice {
    resource: string;
    capacity: number;
    contents: number;
  }

  interface Props {
    partId: string;
    partTitle: string;
    volume: number;
    tanks: TankSlice[];
    onApply: (entries: TankCustomEntry[]) => void;
  }
  const { partId, partTitle, volume, tanks: initialTanks, onApply }: Props = $props();

  // Resource densities (kg / L). Mirrors Nova.Core.Resources/Resource.cs.
  // Hard-coded here because the editor doesn't get density off the wire
  // — modded resources fall through to 1.0 (fine for the indicative
  // mass readout).
  const DENSITY: Record<string, number> = {
    'Liquid Hydrogen': 0.07,
    'Liquid Oxygen':   1.20,
    'RP-1':            0.80,
    'Hydrazine':       1.00,
    'Xenon':           2.00,
  };
  const density = (r: string): number => DENSITY[r] ?? 1.0;

  // Canonical Topological resources the user can add to a tank. Source
  // of truth is C# Resource.cs; this list mirrors it. Sorted at definition
  // so the context-menu options always read alphabetically (the slices
  // themselves keep wire order — see the seed effect — so adds/changes
  // don't shuffle existing items out from under the user).
  const ADDABLE_RESOURCES: readonly string[] = [
    'Hydrazine',
    'Liquid Hydrogen',
    'Liquid Oxygen',
    'RP-1',
    'Xenon',
  ];

  // Per-fuel volumetric LOX demand for engine-typical mass ratios
  // (against Nova's densities — LH2=0.07, LOX=1.20, RP-1=0.80 kg/L).
  // For each fuel volume, the corresponding LOX volume needed in the
  // engine ratio is `volume × LOX_PER_FUEL[fuel]`.
  //
  //   Kerolox  60% LOX + 40% RP-1 by volume → LOX = 1.5  × RP-1
  //            ⇒ 2.25:1 LOX:RP-1 by mass
  //   Hydrolox 26% LOX + 74% LH2 by volume → LOX ≈ 0.351 × LH2
  //            ⇒ ~6:1 LOX:LH2 by mass
  const LOX_PER_FUEL: Record<string, number> = {
    'RP-1':            0.60 / 0.40,   // 1.5
    'Liquid Hydrogen': 0.26 / 0.74,   // ~0.3514
  };
  const OXIDIZER_RESOURCE = 'Liquid Oxygen';

  // Whether the tank's current contents support a single global
  // "Balance" — at least one recognised fuel AND the oxidizer must be
  // present. Tanks with only fuels (no LOX) or only LOX get nothing.
  function canBalance(): boolean {
    const present = new Set(slices.map((s) => s.resource));
    if (!present.has(OXIDIZER_RESOURCE)) return false;
    return Object.keys(LOX_PER_FUEL).some((f) => present.has(f));
  }

  // Single global Balance. Redistributes the fuels and LOX in the tank
  // to engine ratios while:
  //   • preserving the total capacity sum across balance-relevant
  //     slices (fuels + LOX), so a balanced 3000 L kerolox+hydrolox
  //     mix stays at 3000 L total;
  //   • preserving the relative ratio between fuels (RP-1 vs LH2)
  //     when both are present — the user's existing fuel split is
  //     kept, only the per-fuel LOX share is corrected;
  //   • leaving non-balance slices (Hydrazine, Xenon, …) and the
  //     FREE pool untouched.
  //
  // Math: for each fuel f with normalized share α_f = cap_f / Σcap_f,
  // a unit of new fuel needs (1 + LOX_PER_FUEL[f]) units of total
  // budget. Solve scale × Σ_f (α_f × (1 + LOX_PER_FUEL[f])) = budget.
  // Then new_f = scale × α_f, new_LOX = Σ_f (new_f × LOX_PER_FUEL[f]).
  function balance(): void {
    const oxSlice = slices.find((s) => s.resource === OXIDIZER_RESOURCE);
    const fuelSlices = slices.filter((s) => s.resource in LOX_PER_FUEL);
    if (!oxSlice || fuelSlices.length === 0) return;

    let budget = oxSlice.capacity;
    for (const f of fuelSlices) budget += f.capacity;
    if (budget <= 0) return;

    const fuelSum = fuelSlices.reduce((a, s) => a + s.capacity, 0);
    if (fuelSum <= 0) return; // all fuels at zero — no signal for proportions

    let denominator = 0;
    for (const f of fuelSlices) {
      const share = f.capacity / fuelSum;
      denominator += share * (1 + LOX_PER_FUEL[f.resource]);
    }
    const scale = budget / denominator;

    let newOxCap = 0;
    for (const f of fuelSlices) {
      const share = f.capacity / fuelSum;
      const newCap = scale * share;
      f.capacity = newCap;
      if (f.contents > newCap) f.contents = newCap;
      newOxCap += newCap * LOX_PER_FUEL[f.resource];
    }
    oxSlice.capacity = newOxCap;
    if (oxSlice.contents > newOxCap) oxSlice.contents = newOxCap;
  }

  // Local working copy of the slice list. Edits land here; the
  // live-apply effect debounce-pushes them up via onApply.
  let slices = $state<TankSlice[]>([]);
  // `propsKey` is the fingerprint the LIVE-APPLY effect compares the
  // current `slices` against to decide whether to dispatch. It's set
  //   (a) by the seed effect when the parent broadcasts a new shape, and
  //   (b) by the live-apply effect right before it dispatches upstream.
  let propsKey = $state<string>('');
  // `lastSyncedProps` tracks what the seed effect has *already*
  // applied. A non-reactive plain variable on purpose — if it were
  // $state and the seed effect both read and wrote it, Svelte's depth
  // tracker would flag the self-loop (`effect_update_depth_exceeded`).
  // Reading non-reactively means the seed effect only depends on
  // `initialTanks`, so the only thing that re-fires it is the parent
  // re-broadcasting a different shape.
  let lastSyncedProps = '';

  $effect(() => {
    const incoming = JSON.stringify(initialTanks);
    if (incoming !== lastSyncedProps) {
      lastSyncedProps = incoming;
      // Preserve wire order — same as C# Buffer order, which is the
      // proto round-trip's natural ordering. No sort here means resource
      // changes via right-click don't shuffle other slices around the
      // user mid-interaction.
      slices = structuredClone(initialTanks);
      propsKey = incoming;
      pendingApply = null;
    }
  });

  const sumCap   = $derived(slices.reduce((a, s) => a + s.capacity, 0));
  const sumMass  = $derived(slices.reduce((a, s) => a + s.contents * density(s.resource), 0));
  const unused   = $derived(Math.max(0, volume - sumCap));

  function setContents(i: number, value: number): void {
    const cap = slices[i].capacity;
    slices[i].contents = Math.min(cap, Math.max(0, value));
  }

  function removeSlice(i: number): void {
    slices = slices.filter((_, j) => j !== i);
  }

  // Append a resource at the current free-space size, contents = 0
  // (user fills via the contents slider). The new slice lands at the
  // end of the list — wire order — so existing slices don't shift out
  // from under the user. Called from the FREE-tile context menu; the
  // menu only opens when free > 0, so capacity is always > 0 here.
  function addResource(name: string): void {
    if (slices.some(s => s.resource === name)) return;
    const cap = unused;
    if (cap <= 0) return;
    slices = [...slices, { resource: name, capacity: cap, contents: 0 }];
  }

  // Swap the resource of an existing slice in place. Capacity and
  // contents are preserved volumetrically — the user told us "this slot
  // holds X litres", a resource change shouldn't lose that allocation.
  // Position is preserved too: the slice stays at its current index.
  function changeSliceResource(sliceIdx: number, newResource: string): void {
    if (sliceIdx < 0 || sliceIdx >= slices.length) return;
    if (slices.some((s, j) => j !== sliceIdx && s.resource === newResource)) return;
    slices[sliceIdx].resource = newResource;
  }

  // ---------- CAPACITY-BAR DRAG ----------------------------------
  // Each slice exposes a 6-px-wide handle on its right edge. Dragging
  // grows or shrinks ONLY the dragged slice — the change is absorbed
  // by the trailing FREE pool, never by the slice's neighbours. So
  // [RP-1: 500][LOX: 500] dragged-left becomes [RP-1: 400][LOX: 500]
  // [FREE: 100], with LOX visually shifting left because RP-1 is now
  // narrower. Cannot grow past free-pool size; cannot shrink below 0.
  // Contents follow capacity downward so contents never exceeds
  // capacity post-resize.
  //
  // Listener strategy mirrors `FloatingWindow`: register pointermove/
  // pointerup on `document` only while a drag is live. KSP's CEF
  // delivers events reliably via document-level listeners — the older
  // `<svelte:window>`-bound + setPointerCapture approach silently
  // dropped pointermove there, breaking the drag in the live game even
  // though the dev-server Chromium accepted it.

  let barEl: HTMLDivElement | null = $state(null);
  type CapDrag = { sliceIdx: number; startX: number; cap0: number; freeAtStart: number };
  let capDrag: CapDrag | null = null;

  function onCapHandlePointerDown(e: PointerEvent, sliceIdx: number): void {
    if (e.button !== 0) return;
    e.preventDefault();
    e.stopPropagation();
    capDrag = {
      sliceIdx,
      startX: e.clientX,
      cap0: slices[sliceIdx].capacity,
      freeAtStart: unused,
    };
    document.addEventListener('pointermove', onCapHandlePointerMove);
    document.addEventListener('pointerup', onCapHandlePointerUp);
    document.addEventListener('pointercancel', onCapHandlePointerUp);
  }
  function onCapHandlePointerMove(e: PointerEvent): void {
    if (!capDrag || !barEl) return;
    const w = barEl.clientWidth;
    if (w <= 0) return;
    const delta = ((e.clientX - capDrag.startX) / w) * volume;
    // Allowed range: [0, cap0 + freeAtStart]. Above that means the
    // user is asking for more than the free pool can give.
    const next = Math.max(0, Math.min(capDrag.cap0 + capDrag.freeAtStart, capDrag.cap0 + delta));
    const li = capDrag.sliceIdx;
    slices[li].capacity = next;
    if (slices[li].contents > next) slices[li].contents = next;
  }
  function onCapHandlePointerUp(): void {
    if (!capDrag) return;
    capDrag = null;
    document.removeEventListener('pointermove', onCapHandlePointerMove);
    document.removeEventListener('pointerup', onCapHandlePointerUp);
    document.removeEventListener('pointercancel', onCapHandlePointerUp);
  }

  // ---------- Capacity-bar context menu ---------------------------
  // Right-click on the capacity bar opens a small two-level menu.
  // Top level shows category actions:
  //   • Change to… ▸ / Add resource… ▸ — opens a sub-menu listing the
  //     resources NOT currently in the tank. Picking one swaps the
  //     clicked slice (change mode) or appends a new slice at full
  //     FREE space (add mode).
  //   • Balance — global rebalance of fuels + LOX to engine ratios
  //     (kerolox 60/40, hydrolox 74/26 by volume), preserving the
  //     fuel-ratio split and total balance-pool capacity. Only shown
  //     when the tank holds at least one recognised fuel + LOX.
  //
  // The sub-menu replaces the top-level menu in place at the same
  // anchor coords — the Dragonglass ContextMenu is single-pane. Mode
  // (change vs add) is captured at top-level click time and threaded
  // into the sub-menu's onSelect closures.
  let menu = $state<{ items: MenuItem[]; x: number; y: number } | null>(null);

  function openBarMenu(e: MouseEvent): void {
    e.preventDefault();
    e.stopPropagation();

    // Identify the click target. closest() walks up from e.target which
    // can be any inner element (slice-code/cap span, handle, the slice
    // itself, or the FREE tile). The drag handle's parent is the slice
    // it belongs to, so right-click on a handle resolves to that slice.
    const sliceEl = (e.target as HTMLElement | null)?.closest('.tre__slice') as HTMLElement | null;
    const isUnused = !!sliceEl?.classList.contains('tre__slice--unused');
    const sliceIdxStr = sliceEl?.getAttribute('data-slice-idx');
    const sliceIdx = sliceIdxStr != null ? parseInt(sliceIdxStr, 10) : -1;
    const x = e.clientX;
    const y = e.clientY;

    const present = new Set(slices.map(s => s.resource));
    const candidateResources = ADDABLE_RESOURCES.filter(r => !present.has(r));

    const items: MenuItem[] = [];

    if (candidateResources.length > 0) {
      const isChange = !isUnused && sliceIdx >= 0;
      items.push({
        label: isChange ? 'Change to… ▸' : 'Add resource… ▸',
        onSelect: () => {
          // Replace the top-level menu with a sub-menu of resource
          // options. Anchors at the same coords so it appears in place.
          const sub = candidateResources.map((r): MenuItem => ({
            label: r,
            onSelect: () => {
              if (isChange) changeSliceResource(sliceIdx, r);
              else addResource(r);
              menu = null;
            },
          }));
          menu = { items: sub, x, y };
        },
      });
    }

    if (canBalance()) {
      items.push({
        label: 'Balance',
        onSelect: () => { balance(); menu = null; },
      });
    }

    if (items.length === 0) return;
    menu = { items, x, y };
  }

  // Portal action: relocate `node` to <body> so descendant
  // `position: fixed` styles resolve against the viewport, not the
  // FloatingWindow. The FloatingWindow uses `backdrop-filter`, which
  // (per CSS containing-block rules) turns it into the containing
  // block for any fixed descendant — `.ctx-menu`'s `left/top` would
  // otherwise be measured from the panel's edge and pinned offscreen
  // for any click on the panel's right side.
  function portal(node: HTMLElement) {
    document.body.appendChild(node);
    return { destroy() { node.remove(); } };
  }

  // ---------- CONTENTS DRAG --------------------------------------
  // Drag-anywhere on the contents-gauge sub-row sets the fraction of
  // capacity for that slice. PointerDown also seeds an immediate value
  // so a click without movement registers as "set to here".
  //
  // Same document-listener pattern as the capacity-handle drag — see
  // the comment above `onCapHandlePointerDown` for why <svelte:window>
  // / setPointerCapture didn't survive KSP's CEF.
  type FillDrag = { sliceIdx: number; el: HTMLDivElement };
  let fillDrag: FillDrag | null = null;

  function fractionFromX(el: HTMLDivElement, clientX: number): number {
    const rect = el.getBoundingClientRect();
    if (rect.width <= 0) return 0;
    return Math.max(0, Math.min(1, (clientX - rect.left) / rect.width));
  }
  function onFillPointerDown(e: PointerEvent, sliceIdx: number): void {
    if (e.button !== 0) return;
    e.preventDefault();
    const el = e.currentTarget as HTMLDivElement;
    fillDrag = { sliceIdx, el };
    setContents(sliceIdx, slices[sliceIdx].capacity * fractionFromX(el, e.clientX));
    document.addEventListener('pointermove', onFillPointerMove);
    document.addEventListener('pointerup', onFillPointerUp);
    document.addEventListener('pointercancel', onFillPointerUp);
  }
  function onFillPointerMove(e: PointerEvent): void {
    if (!fillDrag) return;
    setContents(fillDrag.sliceIdx, slices[fillDrag.sliceIdx].capacity * fractionFromX(fillDrag.el, e.clientX));
  }
  function onFillPointerUp(): void {
    if (!fillDrag) return;
    fillDrag = null;
    document.removeEventListener('pointermove', onFillPointerMove);
    document.removeEventListener('pointerup', onFillPointerUp);
    document.removeEventListener('pointercancel', onFillPointerUp);
  }

  // ---------- LIVE APPLY (debounced) -----------------------------
  // Watches the staged shape and pushes a setTankCustom payload up
  // 250 ms after the last edit. Skips the first effect run (matches
  // the props the parent already has).
  //
  // `pendingApply` is intentionally NOT $state — the live-apply effect
  // reads and writes it (clearTimeout + setTimeout), and a $state read
  // would self-trigger the effect (effect_update_depth_exceeded). It's
  // a private control-flow flag; only `saving` needs to be reactive
  // because the footer reads it for the spinner.
  let pendingApply: number | null = null;
  let saving = $state(false);

  function buildPayload(): TankCustomEntry[] {
    return slices
      .filter(s => s.capacity > 0)
      .map(s => [s.resource, s.capacity, Math.min(s.contents, s.capacity)] as TankCustomEntry);
  }

  $effect(() => {
    const fingerprint = JSON.stringify(slices);
    if (fingerprint === propsKey) return; // matches what parent already shows
    if (pendingApply != null) clearTimeout(pendingApply);
    saving = true;
    pendingApply = window.setTimeout(() => {
      propsKey = fingerprint;
      pendingApply = null;
      saving = false;
      onApply(buildPayload());
    }, 250);
  });

  function revert(): void {
    if (pendingApply != null) clearTimeout(pendingApply);
    pendingApply = null;
    saving = false;
    const incoming = JSON.stringify(initialTanks);
    slices = structuredClone(initialTanks);
    propsKey = incoming;
    lastSyncedProps = incoming;
  }

  // Tonnes formatter: 0.00 t / 1.24 t / 12.4 t / 124 t.
  const fmtTonnes = (kg: number): string => {
    const t = kg / 1000;
    if (Math.abs(t) < 0.005) return '0.00';
    if (Math.abs(t) >= 100) return t.toFixed(0);
    if (Math.abs(t) >= 10) return t.toFixed(1);
    return t.toFixed(2);
  };
  const fmtL = (l: number): string => {
    if (l >= 10000) return Math.round(l).toLocaleString();
    if (l >= 100) return l.toFixed(0);
    return l.toFixed(1);
  };

  // If the component unmounts mid-drag (row collapsed, panel torn
  // down) the document listeners would otherwise keep firing against
  // stale closures. Mirrors FloatingWindow's defensive teardown.
  onDestroy(() => {
    onCapHandlePointerUp();
    onFillPointerUp();
  });
</script>

<div class="tre" data-part-id={partId} aria-label={`Tank editor for ${partTitle}`}>
  <!-- CAPACITY stacked bar ---------------------------------------- -->
  <div class="tre__section">
    <div class="tre__section-head">
      <span class="tre__rule"></span>
      <span class="tre__section-title">CAPACITY</span>
      <span class="tre__section-meta">
        {fmtL(sumCap)} / {fmtL(volume)}<em>L</em>
      </span>
    </div>

    <div class="tre__bar"
         bind:this={barEl}
         role="presentation"
         oncontextmenu={openBarMenu}>
      {#each slices as s, i (i)}
        {@const meta = resourceMeta(s.resource)}
        {@const pct = volume > 0 ? (s.capacity / volume) * 100 : 0}
        <div class="tre__slice"
             class:tre__slice--narrow={pct < 8}
             style:width="{pct}%"
             style:--slice-color={meta.color}
             style:--slice-tint={meta.tint}
             data-slice-idx={i}
             title="Right-click to change resource">
          <span class="tre__slice-code">{meta.code}</span>
          <span class="tre__slice-cap">{fmtL(s.capacity)}<em>L</em></span>
          <button type="button"
                  class="tre__slice-x"
                  aria-label="Remove {meta.code}"
                  title="Remove {meta.code}"
                  onclick={() => removeSlice(i)}>×</button>
          <span class="tre__handle"
                role="separator"
                aria-orientation="vertical"
                aria-label="Drag to resize {meta.code}"
                onpointerdown={(e) => onCapHandlePointerDown(e, i)}></span>
        </div>
      {/each}
      {#if unused > 0.5}
        <div class="tre__slice tre__slice--unused"
             style:width="{(unused / volume) * 100}%"
             title="Right-click to add a resource">
          <span class="tre__slice-code">FREE</span>
          <span class="tre__slice-cap">{fmtL(unused)}<em>L</em></span>
        </div>
      {/if}
    </div>
  </div>

  <!-- CONTENTS sliders --------------------------------------------- -->
  {#if slices.length > 0}
    <div class="tre__section">
      <div class="tre__section-head">
        <span class="tre__rule"></span>
        <span class="tre__section-title">STARTING AMOUNT</span>
      </div>

      <ul class="tre__contents">
        {#each slices as s, i (i)}
          {@const meta = resourceMeta(s.resource)}
          {@const frac = s.capacity > 0 ? s.contents / s.capacity : 0}
          {@const massT = (s.contents * density(s.resource)) / 1000}
          <li class="tre__row">
            <span class="tre__row-code"
                  style:--slice-color={meta.color}
                  style:--slice-tint={meta.tint}>{meta.code}</span>
            <div class="tre__row-gauge"
                 style:--sg-color-tint={meta.color}
                 style:--sg-glow-tint={meta.glow}
                 style:--sg-tint-tint={meta.tint}
                 onpointerdown={(e) => onFillPointerDown(e, i)}
                 role="slider"
                 tabindex="0"
                 aria-label={`${meta.code} starting amount`}
                 aria-valuemin={0}
                 aria-valuemax={s.capacity}
                 aria-valuenow={s.contents}>
              <SegmentGauge fraction={frac} segments={20} />
            </div>
            <button type="button" class="tre__peg"
                    aria-label="Empty"
                    title="Empty"
                    onclick={() => setContents(i, 0)}>0</button>
            <button type="button" class="tre__peg"
                    aria-label="Full"
                    title="Full"
                    onclick={() => setContents(i, s.capacity)}>F</button>
            <span class="tre__row-amount">{fmtL(s.contents)}<em>L</em></span>
            <span class="tre__row-mass" class:tre__row-mass--zero={massT < 0.005}>
              {fmtTonnes(s.contents * density(s.resource))}<em>t</em>
            </span>
          </li>
        {/each}
      </ul>
    </div>
  {/if}

  <!-- FOOTER ------------------------------------------------------- -->
  <div class="tre__footer">
    <span class="tre__footer-mass">
      <span class="tre__label tre__label--dim">Σ MASS</span>
      <span class="tre__footer-mass-value">{fmtTonnes(sumMass)}<em>t</em></span>
      {#if saving}<span class="tre__saving" aria-label="Saving">∙∙∙</span>{/if}
    </span>
    <button type="button" class="tre__revert"
            disabled={JSON.stringify(slices) === propsKey}
            onclick={revert}>REVERT</button>
  </div>
</div>

{#if menu}
  <div use:portal class="tre__menu-portal">
    <ContextMenu items={menu.items} x={menu.x} y={menu.y} onDismiss={() => (menu = null)} />
  </div>
{/if}

<style>
  /* Single-component scope. Indentation depth from the parent panel
     comes from the consuming list's nesting; this body sits flush at
     its parent's content edge with its own internal rhythm. */

  .tre {
    display: flex;
    flex-direction: column;
    gap: 12px;
    padding: 8px 4px 10px 16px;
    border-left: 1px solid var(--line);
    margin-left: 6px;
    font-family: var(--font-mono);
    font-size: 11px;
    color: var(--fg);
  }

  /* Section heads carry a leading rule + small caps title + meta. */
  .tre__section {
    display: flex;
    flex-direction: column;
    gap: 6px;
  }
  .tre__section-head {
    display: flex;
    align-items: center;
    gap: 8px;
    font-family: var(--font-display);
    font-size: 10px;
    letter-spacing: 0.22em;
    color: var(--fg-dim);
  }
  .tre__rule {
    flex: 0 0 12px;
    height: 1px;
    background: var(--line-bright);
  }
  .tre__section-title { flex: 0 0 auto; }
  .tre__section-meta {
    margin-left: auto;
    font-family: var(--font-mono);
    font-size: 11px;
    letter-spacing: 0;
    color: var(--accent);
    font-variant-numeric: tabular-nums;
  }
  .tre__section-meta em {
    font-style: normal; font-size: 9px;
    color: var(--fg-dim); margin-left: 3px; letter-spacing: 0.14em;
  }

  /* Stacked capacity bar. Slices are positioned via flex-basis on
     percentage widths; each slice paints its own background ramp from
     its --slice-color so they read as discrete materials side-by-side
     rather than a single tinted strip. The trailing FREE tile uses a
     diagonal hatch so unused volume reads visually different from any
     allocated resource — even at narrow widths. */
  .tre__bar {
    position: relative;
    display: flex;
    align-items: stretch;
    height: 30px;
    border: 1px solid var(--line-bright);
    background:
      linear-gradient(180deg, rgba(0, 0, 0, 0.50) 0%, rgba(0, 0, 0, 0.25) 100%);
    box-shadow: inset 0 1px 0 rgba(0, 0, 0, 0.55);
    user-select: none;
    overflow: hidden;
  }
  .tre__slice {
    position: relative;
    min-width: 0;
    display: flex;
    flex-direction: column;
    justify-content: center;
    align-items: flex-start;
    padding: 0 6px;
    color: var(--fg);
    /* No `overflow: hidden` here. The drag handle is positioned at
       `right: -3px; width: 6px` so half of it extends past the slice
       boundary — and `overflow: hidden` on a parent makes the
       overflowing half non-hit-testable in older Chromium (e.g. the
       CEF version KSP ships). The bar's own overflow: hidden still
       clips at the bar's outer edge, so handles can't escape into the
       panel chrome. Slice-internal text overflow is mitigated by
       `tre__slice--narrow` hiding the L value at small widths. */
    cursor: context-menu;
    background:
      linear-gradient(180deg,
        color-mix(in srgb, var(--slice-color) 28%, transparent) 0%,
        color-mix(in srgb, var(--slice-color) 14%, transparent) 60%,
        color-mix(in srgb, var(--slice-color) 8%,  transparent) 100%),
      var(--slice-tint, transparent);
    box-shadow: inset 1px 0 0 rgba(255, 255, 255, 0.04);
  }
  .tre__slice:first-child { box-shadow: none; }
  .tre__slice--narrow .tre__slice-cap { display: none; }
  .tre__slice--narrow .tre__slice-code { font-size: 9px; }
  .tre__slice-code {
    font-family: var(--font-display);
    font-size: 10px;
    letter-spacing: 0.16em;
    color: var(--slice-color);
    text-shadow: 0 0 6px color-mix(in srgb, var(--slice-color) 35%, transparent);
    line-height: 1.1;
  }
  .tre__slice-cap {
    font-size: 10px;
    color: var(--fg);
    font-variant-numeric: tabular-nums;
    line-height: 1.1;
  }
  .tre__slice-cap em {
    font-style: normal;
    font-size: 8px;
    color: var(--fg-dim);
    margin-left: 2px;
    letter-spacing: 0.14em;
  }
  .tre__slice-x {
    position: absolute;
    top: 2px;
    right: 8px;
    width: 12px;
    height: 12px;
    line-height: 11px;
    text-align: center;
    color: var(--fg-mute);
    font-size: 12px;
    background: rgba(0, 0, 0, 0.35);
    border: 1px solid transparent;
    opacity: 0;
    transition: opacity 160ms ease, color 160ms ease, border-color 160ms ease;
    cursor: pointer;
  }
  .tre__slice:hover .tre__slice-x { opacity: 0.85; }
  .tre__slice-x:hover { color: var(--alert); border-color: var(--alert); opacity: 1; }

  /* Drag handle: 12 px-wide hit zone straddling the slice's right edge
     (-6 left, +6 right of the boundary). Generous because the boundary
     line itself is only 2 px and KSP's CEF hit-test needs forgiveness.
     Two pieces visualize the affordance ALWAYS so the user can see
     where to grab without hovering first:
       • a 2 px-wide vertical line (accent-dim) sitting on the boundary
       • a small grip pip — 4 px-wide notch in the middle — visible
         against the dim line, snaps to bright accent on hover.
     Hover/active state lifts both pieces to bright accent + glow. */
  .tre__handle {
    position: absolute;
    top: 0;
    bottom: 0;
    right: -6px;
    width: 12px;
    cursor: col-resize;
    z-index: 2;
  }
  .tre__handle::before {
    content: '';
    position: absolute;
    top: 3px;
    bottom: 3px;
    left: 50%;
    margin-left: -1px;
    width: 2px;
    background: var(--accent-dim);
    opacity: 0.55;
    transition: background 160ms ease, opacity 160ms ease, box-shadow 160ms ease;
  }
  .tre__handle::after {
    content: '';
    position: absolute;
    top: 50%;
    left: 50%;
    margin-left: -2px;
    margin-top: -3px;
    width: 4px;
    height: 6px;
    border-left: 1px solid var(--bg-panel-strong);
    border-right: 1px solid var(--bg-panel-strong);
    pointer-events: none;
  }
  .tre__handle:hover::before,
  .tre__handle:active::before {
    background: var(--accent);
    opacity: 1;
    box-shadow: 0 0 6px var(--accent-glow);
  }

  /* Unused-volume tile: diagonal hatch over a darker background. */
  /* Unused-volume tile: diagonal hatch + cursor hint that this is
     interactive (right-click to add a resource). Hover lifts the tile
     into accent territory so it reads as a target. */
  .tre__slice--unused {
    background:
      repeating-linear-gradient(
        45deg,
        rgba(255, 255, 255, 0.025) 0 4px,
        transparent 4px 8px),
      rgba(0, 0, 0, 0.40);
    color: var(--fg-mute);
    cursor: context-menu;
    transition: background 200ms ease, color 200ms ease;
  }
  .tre__slice--unused:hover {
    background:
      repeating-linear-gradient(
        45deg,
        rgba(126, 245, 184, 0.10) 0 4px,
        transparent 4px 8px),
      rgba(126, 245, 184, 0.05);
    color: var(--accent);
  }
  .tre__slice--unused:hover .tre__slice-code {
    color: var(--accent);
    text-shadow: 0 0 5px var(--accent-glow);
  }
  .tre__slice--unused:hover .tre__slice-cap { color: var(--fg); }
  .tre__slice--unused .tre__slice-code {
    color: var(--fg-mute);
    text-shadow: none;
  }
  .tre__slice--unused .tre__slice-cap { color: var(--fg-dim); }
  .tre__slice--unused:focus-visible { outline: 1px solid var(--accent); outline-offset: -2px; }

  /* Contents rows. The gauge is the slider — pointerdown anywhere on
     the row's gauge area sets the value to that x-fraction. The row's
     own background nudges on hover so the row-as-control reads. */
  .tre__contents {
    list-style: none;
    margin: 0; padding: 0;
    display: flex;
    flex-direction: column;
    gap: 0;
  }
  .tre__row {
    display: flex;
    align-items: center;
    gap: 8px;
    padding: 4px 4px;
    border-bottom: 1px solid rgba(26, 35, 53, 0.55);
  }
  .tre__row:last-child { border-bottom: 0; }
  .tre__row-code {
    flex: 0 0 36px;
    font-family: var(--font-display);
    font-size: 10px;
    letter-spacing: 0.14em;
    color: var(--slice-color);
    text-shadow: 0 0 5px color-mix(in srgb, var(--slice-color) 30%, transparent);
    padding: 1px 4px;
    border-left: 2px solid var(--slice-color);
    background: var(--slice-tint, transparent);
  }
  /* Gauge wrapper clips its own glow halo at the wrapper edge so a
     fully-lit rightmost segment doesn't bleed onto the adjacent
     [0] / [F] peg buttons. The padding gives a few pixels of vertical
     room for the glow inside the wrapper before the clip. */
  .tre__row-gauge {
    flex: 1 1 auto;
    min-width: 0;
    cursor: ew-resize;
    padding: 4px 0;
    overflow: hidden;
  }
  .tre__row-gauge:hover :global(.sg) { box-shadow: inset 0 0 0 1px var(--accent-dim); }

  /* Quick-pegs and amount/mass readouts: tabular numerals + tight padding. */
  .tre__peg {
    flex: 0 0 18px;
    height: 18px;
    background: transparent;
    border: 1px solid var(--line);
    color: var(--fg-dim);
    font-family: var(--font-display);
    font-size: 10px;
    line-height: 1;
    letter-spacing: 0.06em;
    cursor: pointer;
    transition: color 160ms ease, border-color 160ms ease;
  }
  .tre__peg:hover {
    color: var(--accent);
    border-color: var(--accent-dim);
  }
  .tre__row-amount {
    flex: 0 0 auto;
    min-width: 56px;
    text-align: right;
    color: var(--fg);
    font-variant-numeric: tabular-nums;
  }
  .tre__row-amount em {
    font-style: normal; font-size: 9px;
    color: var(--fg-dim); margin-left: 3px; letter-spacing: 0.14em;
  }
  .tre__row-mass {
    flex: 0 0 auto;
    min-width: 54px;
    text-align: right;
    color: var(--accent);
    font-variant-numeric: tabular-nums;
  }
  .tre__row-mass--zero { color: var(--fg-mute); }
  .tre__row-mass em {
    font-style: normal; font-size: 9px;
    color: var(--fg-dim); margin-left: 2px; letter-spacing: 0.14em;
  }

  /* Footer: cumulative mass on the left, REVERT on the right.
     Accentuated divider above so the section reads as a tray. */
  .tre__footer {
    display: flex;
    align-items: center;
    gap: 12px;
    padding-top: 6px;
    border-top: 1px solid var(--line);
  }
  .tre__footer-mass {
    display: inline-flex;
    align-items: baseline;
    gap: 6px;
    flex: 1 1 auto;
  }
  .tre__footer-mass-value {
    font-size: 14px;
    color: var(--accent);
    font-variant-numeric: tabular-nums;
    text-shadow: 0 0 6px var(--accent-glow);
  }
  .tre__footer-mass-value em {
    font-style: normal; font-size: 10px;
    color: var(--fg-dim); margin-left: 3px; letter-spacing: 0.14em;
    text-shadow: none;
  }
  .tre__saving {
    color: var(--accent-dim);
    font-size: 12px;
    letter-spacing: 0.4em;
    animation: tre-saving 1.2s ease-in-out infinite;
  }
  @keyframes tre-saving {
    0%, 100% { opacity: 0.35; }
    50%      { opacity: 1; }
  }
  .tre__revert {
    background: transparent;
    border: 1px solid var(--line);
    color: var(--fg-dim);
    font-family: var(--font-display);
    font-size: 10px;
    letter-spacing: 0.18em;
    padding: 3px 8px;
    cursor: pointer;
    transition: color 160ms ease, border-color 160ms ease, background 160ms ease;
  }
  .tre__revert:hover:not(:disabled) {
    color: var(--warn);
    border-color: var(--warn);
    background: rgba(240, 180, 41, 0.06);
  }
  .tre__revert:disabled { opacity: 0.35; cursor: not-allowed; }

  /* Portal host for the add-resource ContextMenu. Sits at body level
     (via `use:portal`) so the menu's `position: fixed` resolves against
     the viewport. `position: relative` + a high `z-index` creates a new
     stacking context that paints above the FloatingWindow (whose own
     z-index climbs as the user raises it). */
  .tre__menu-portal {
    position: relative;
    z-index: 9999;
  }
</style>
