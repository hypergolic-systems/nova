<script lang="ts">
  // One mystery-goo chamber row. Lives inside the SCI tab's INSTRUMENTS
  // tree, alongside Thermometer rows.
  //
  // Visual model is "Specimen Drawer": each sample is a discrete cell
  // showing a typed goo-blob and its condition (Pristine / Exposing /
  // Exposed / Invalidated). The actively-exposing slot wears a sweep
  // ring that fills as the timer counts down. Invalidating samples
  // scar red — the player should feel the *cost* of cycling the cover
  // mid-exposure.
  //
  // The cover toggle is the only player control. Three visual states:
  //   CLOSED — dim, neutral border
  //   OPEN   — mint accent (idle: open but nothing exposing)
  //   EXPOSE — pulsing amber (mid-exposure: closing now INVALIDATES)
  //
  // Goo-flavor colours: "prime" maps to KSP-stock amber-orange; "dark"
  // maps to a deep magenta. The map lives below; new SampleType ids
  // fall back to the dim "unknown" hue rather than crashing.

  import { SampleCondition, type GooState, type GooSample } from '../../telemetry/nova-topics';
  import ComponentIcon from '../ComponentIcon.svelte';
  import { fmtDuration } from '../../util/units';

  interface Props {
    partId:    string;
    title:     string;
    goo:       GooState;
    onToggle:  (open: boolean) => void;
  }
  // svelte-check rejects a prop literally named `state` (it collides
  // with the `$state` rune naming heuristic) — using `goo` keeps the
  // type tight and the reading-on-screen unambiguous.
  const { partId: _partId, title, goo, onToggle }: Props = $props();

  let open = $state(true);  // collapsible-body open by default

  // ---- Color & display per sample type ---------------------------
  // The map deliberately uses both `--accent`-family ramps and bespoke
  // hex values: the canonical mystery-goo orange is iconic enough that
  // we want it to read as "stock KSP goo" at a glance, regardless of
  // the surrounding chrome tint. Dark-goo gets a complementary magenta
  // so two flavors in one drawer are immediately distinguishable.
  const TYPE_VIS: Record<string, { label: string; short: string; color: string; glow: string }> = {
    'mystery-goo-prime': {
      label: 'Prime',
      short: 'P',
      color: '#f0a020',
      glow:  'rgba(240, 160, 32, 0.55)',
    },
    'mystery-goo-dark': {
      label: 'Dark',
      short: 'D',
      color: '#b04ed8',
      glow:  'rgba(176, 78, 216, 0.55)',
    },
  };
  function visFor(typeId: string) {
    return TYPE_VIS[typeId] ?? { label: typeId, short: '?', color: '#6e7a7a', glow: 'rgba(110,122,122,0.4)' };
  }

  // ---- Inventory summary -----------------------------------------
  const inventoryStr = $derived.by(() => {
    if (goo.samples.length === 0) return '—';
    const counts = new Map<string, number>();
    for (const s of goo.samples) {
      if (s.condition !== SampleCondition.Pristine) continue;
      counts.set(s.typeId, (counts.get(s.typeId) ?? 0) + 1);
    }
    if (counts.size === 0) return 'all consumed';
    return [...counts.entries()]
      .map(([id, n]) => `${visFor(id).label} ×${n}`)
      .join(', ');
  });

  const totalMassGrams = $derived(
    goo.samples.reduce((a, s) => a + s.massKg, 0) * 1000,
  );

  const exposedCount = $derived(
    goo.samples.filter((s) => s.condition === SampleCondition.Exposed).length,
  );
  const invalidatedCount = $derived(
    goo.samples.filter((s) => s.condition === SampleCondition.Invalidated).length,
  );
  const pristineCount = $derived(
    goo.samples.filter((s) => s.condition === SampleCondition.Pristine).length,
  );

  // ---- Status line + cover-toggle visual state -------------------
  const isExposing = $derived(goo.coverOpen && goo.exposingIndex >= 0);
  // EXPOSE = cover open AND a sample is mid-exposure: closing now
  // would invalidate it. The pill turns warning-amber and pulses.
  // OPEN   = cover open but nothing is exposing (chamber idle).
  // CLOSED = cover closed.
  const coverMode = $derived(
    isExposing ? 'expose' : goo.coverOpen ? 'open' : 'closed',
  );
  const coverLabel = $derived(
    coverMode === 'expose' ? 'EXPOSE' : coverMode === 'open' ? 'OPEN' : 'CLOSED',
  );
  const coverHint = $derived(
    coverMode === 'expose' ? 'CLOSE INVALIDATES' :
    coverMode === 'open'   ? (pristineCount === 0 ? 'NO SAMPLES' : 'TAP TO CLOSE') :
                              pristineCount > 0    ? 'TAP TO EXPOSE'  : 'CHAMBER SPENT',
  );

  const statusLine = $derived.by(() => {
    if (isExposing) {
      return `EXPOSING · ${fmtDuration(goo.remainingSec)} REMAINING`;
    }
    if (goo.coverOpen && pristineCount === 0) return 'COVER OPEN · NO PRISTINE SAMPLES';
    if (goo.coverOpen) return 'COVER OPEN · NO ACTIVE EXPOSURE';
    if (pristineCount === 0) return 'CHAMBER SPENT · ALL SAMPLES CONSUMED';
    return `READY · ${pristineCount} SAMPLE${pristineCount === 1 ? '' : 'S'} PRISTINE`;
  });

  // Percent of samples *cleanly* exposed — invalidated samples don't
  // count toward completion, but they're visible in the chamber so
  // the player still sees them.
  const completionPct = $derived(
    goo.samples.length === 0
      ? 0
      : Math.round((exposedCount / goo.samples.length) * 100),
  );

  function handleToggleCover() {
    onToggle(!goo.coverOpen);
  }

  function slotState(s: GooSample): 'pristine' | 'exposing' | 'exposed' | 'invalidated' {
    if (s.condition === SampleCondition.Exposed)     return 'exposed';
    if (s.condition === SampleCondition.Invalidated) return 'invalidated';
    return 'pristine';
  }

  // Per-slot transform — gives each cell's blob a subtle distinct
  // shape & bob-phase. Deterministic from index so the same slot
  // looks the same every frame.
  function slotPhase(i: number): { rot: number; delay: string } {
    return {
      rot: (i * 137) % 360,           // golden-angle-ish for variety
      delay: `${(i * 0.47).toFixed(2)}s`,
    };
  }
</script>

<li class="goo">
  <!-- Head is a div+role=button (not a <button>) so the cover toggle
       can live inside it without nesting buttons (which the HTML spec
       forbids — svelte-check / browsers will "repair" by re-parenting). -->
  <div
    class="goo__head"
    role="button"
    tabindex="0"
    aria-expanded={open}
    onclick={() => (open = !open)}
    onkeydown={(e) => {
      if (e.key === 'Enter' || e.key === ' ') {
        e.preventDefault();
        open = !open;
      }
    }}
  >
    <span class="goo__chev" aria-hidden="true">{open ? '▾' : '▸'}</span>
    <span class="goo__head-icon">
      <ComponentIcon kind="mysteryGoo" />
    </span>
    <span class="goo__head-name">{title}</span>
    <span class="goo__head-pct">{completionPct}%</span>
    <button
      type="button"
      class="goo__cover goo__cover--{coverMode}"
      aria-pressed={goo.coverOpen}
      aria-label={`${goo.coverOpen ? 'Close' : 'Open'} chamber cover`}
      title={coverHint}
      onclick={(e) => { e.stopPropagation(); handleToggleCover(); }}
    >
      <span class="goo__cover-label">{coverLabel}</span>
    </button>
  </div>

  {#if open}
    <div class="goo__body">
      <!-- Specimen drawer: one cell per sample slot. -->
      <div class="goo__drawer" role="list" aria-label="Sample slots">
        {#each goo.samples as sample, i (i)}
          {@const v   = visFor(sample.typeId)}
          {@const st  = slotState(sample)}
          {@const act = i === goo.exposingIndex && isExposing}
          {@const ph  = slotPhase(i)}
          <div
            class="goo__slot goo__slot--{st}"
            class:goo__slot--active={act}
            role="listitem"
            title={`${v.label} · ${st}`}
            style="--goo-color: {v.color}; --goo-glow: {v.glow};"
          >
            <div class="goo__cell">
              {#if act}
                <!-- Sweep ring: progresses from 0 (slot just armed) to
                     full (sample about to flip to Exposed). The ring is
                     drawn with stroke-dasharray; the dashoffset is bound
                     to (1 - progress). -->
                <svg class="goo__ring" viewBox="0 0 48 48" aria-hidden="true">
                  <circle class="goo__ring-track" cx="24" cy="24" r="20.5" />
                  <circle
                    class="goo__ring-sweep"
                    cx="24"
                    cy="24"
                    r="20.5"
                    style="stroke-dashoffset: {(1 - goo.exposureProgress) * 128.8};"
                  />
                </svg>
              {/if}

              <!-- The goo blob itself. Organic border-radius + slow bob
                   animation; tint comes from --goo-color. Pristine
                   samples shimmer subtly. Exposed/Invalidated samples
                   freeze and desaturate via the slot modifier. -->
              <div
                class="goo__blob"
                style="--goo-rot: {ph.rot}deg; animation-delay: {ph.delay};"
              ></div>

              <!-- State overlay: only Exposed and Invalidated render a
                   glyph; Pristine and Exposing leave the blob unmasked
                   (the blob IS the visualization for active states). -->
              {#if st === 'exposed'}
                <svg class="goo__pip goo__pip--exposed" viewBox="0 0 16 16" aria-hidden="true">
                  <path d="M3 8 L7 12 L13 4" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" />
                </svg>
              {:else if st === 'invalidated'}
                <svg class="goo__pip goo__pip--invalid" viewBox="0 0 16 16" aria-hidden="true">
                  <path d="M3 3 L13 13 M13 3 L3 13" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" />
                </svg>
              {/if}
            </div>
            <div class="goo__slot-label">
              <span class="goo__slot-idx">{String(i + 1).padStart(2, '0')}</span>
              <span class="goo__slot-type" style="color: var(--goo-color);">{v.short}</span>
            </div>
          </div>
        {/each}
      </div>

      <!-- Status strip: status line + linear progress bar (always
           rendered so layout doesn't shift between states; fill drops
           to 0 when not exposing). -->
      <div class="goo__status goo__status--{coverMode}">
        <span class="goo__status-text">{statusLine}</span>
        <div class="goo__bar" aria-hidden="true">
          <div class="goo__bar-fill" style="width: {goo.exposureProgress * 100}%;"></div>
        </div>
      </div>

      <!-- Detail grid: INVENTORY / MASS / SLOTS. Compact, monospaced;
           same shape as the thermometer's LIMITS/SEEN/STORAGE block. -->
      <div class="goo__detail">
        <div class="goo__detail-line">
          <span class="goo__detail-key">INVENTORY</span>
          <span class="goo__detail-val">{inventoryStr}</span>
        </div>
        <div class="goo__detail-line">
          <span class="goo__detail-key">MASS</span>
          <span class="goo__detail-val">{totalMassGrams.toFixed(0)} g</span>
        </div>
        <div class="goo__detail-line">
          <span class="goo__detail-key">SLOTS</span>
          <span class="goo__detail-val">
            <span class="goo__count goo__count--pristine">{pristineCount} ○</span>
            <span class="goo__count goo__count--exposed">{exposedCount} ●</span>
            <span class="goo__count goo__count--invalid" class:goo__count--mute={invalidatedCount === 0}>{invalidatedCount} ✕</span>
            <span class="goo__count-cap">/ {goo.capacity}</span>
          </span>
        </div>
      </div>
    </div>
  {/if}
</li>

<style>
  /* The chamber row mounts as a list item inside ScienceView's
     `.sci__instr-rows`. Matching class hierarchy lets it share spacing
     and indent with thermometer rows. */
  .goo {
    margin-left: 4px;
    list-style: none;
  }

  .goo__head {
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
    border-left: 2px solid transparent;
    transition: background 160ms ease, border-left-color 160ms ease;
  }
  .goo__head:hover,
  .goo__head:focus-visible {
    background: rgba(126, 245, 184, 0.04);
    border-left-color: var(--accent-dim);
    outline: none;
  }
  .goo__chev {
    flex: 0 0 auto;
    width: 9px;
    color: var(--fg-mute);
    font-family: var(--font-display);
    font-size: 9px;
  }
  .goo__head-icon {
    flex: 0 0 auto;
    color: var(--fg-dim);
    width: 14px;
    height: 14px;
    display: flex;
    align-items: flex-start;
    padding-top: 1px;
  }
  .goo__head:hover .goo__head-icon {
    color: var(--accent);
  }
  .goo__head-name {
    flex: 1 1 auto;
    min-width: 0;
    color: var(--fg);
    font-family: var(--font-mono);
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }
  .goo__head-pct {
    flex: 0 0 auto;
    font-family: var(--font-mono);
    font-size: 11px;
    color: var(--accent);
    font-variant-numeric: tabular-nums;
    letter-spacing: 0.02em;
  }

  /* Cover toggle pill — three visual states encode the chamber's
     immediate-action consequence. EXPOSE pulses so the player can't
     miss that closing the cover right now wastes a sample. */
  .goo__cover {
    flex: 0 0 auto;
    appearance: none;
    cursor: pointer;
    font-family: var(--font-display);
    font-size: 8.5px;
    letter-spacing: 0.20em;
    padding: 1px 7px;
    border: 1px solid var(--line);
    background: transparent;
    color: var(--fg-mute);
    border-radius: 1px;
    transition: color 140ms ease, border-color 140ms ease,
                background 140ms ease, text-shadow 140ms ease;
  }
  .goo__cover:focus-visible {
    outline: 1px solid var(--accent);
    outline-offset: 1px;
  }
  .goo__cover--closed:hover {
    color: var(--accent);
    border-color: var(--accent);
    background: rgba(126, 245, 184, 0.08);
  }
  .goo__cover--open {
    color: var(--accent);
    border-color: var(--accent);
    background: rgba(126, 245, 184, 0.10);
    text-shadow: 0 0 6px var(--accent-glow);
  }
  .goo__cover--expose {
    color: var(--warn);
    border-color: var(--warn);
    background: rgba(240, 180, 41, 0.14);
    text-shadow: 0 0 6px var(--warn-glow);
    animation: goo-expose-pulse 1.4s ease-in-out infinite;
  }

  @keyframes goo-expose-pulse {
    0%, 100% { box-shadow: 0 0 0 0 rgba(240, 180, 41, 0); }
    50%      { box-shadow: 0 0 0 3px rgba(240, 180, 41, 0.12); }
  }

  /* ---- Body --------------------------------------------------- */
  .goo__body {
    margin: 6px 0 12px 18px;
    display: flex;
    flex-direction: column;
    gap: 10px;
  }

  /* ---- Specimen drawer --------------------------------------- */
  .goo__drawer {
    display: flex;
    gap: 10px;
    padding: 8px 6px;
    border: 1px solid var(--line);
    background: linear-gradient(180deg,
      rgba(8, 14, 14, 0.5) 0%,
      rgba(8, 14, 14, 0.2) 100%);
    /* Inner shadow + subtle grain via a 1px tall radial overlay sells
       the "drawer interior" surface — flat fills read as a panel, not
       a chamber. */
    box-shadow:
      inset 0 1px 0 rgba(255, 255, 255, 0.02),
      inset 0 0 18px rgba(0, 0, 0, 0.35);
  }
  .goo__slot {
    flex: 0 0 auto;
    display: flex;
    flex-direction: column;
    align-items: center;
    gap: 4px;
  }
  /* The cell hosts the ring + blob + state pip in a fixed 48×48 box
     so slots align horizontally regardless of contents. */
  .goo__cell {
    position: relative;
    width: 48px;
    height: 48px;
    display: grid;
    place-items: center;
    border: 1px solid rgba(126, 245, 184, 0.10);
    background:
      radial-gradient(circle at 50% 35%,
        rgba(255, 255, 255, 0.04) 0%,
        transparent 60%),
      rgba(4, 8, 8, 0.6);
  }
  .goo__slot--exposing .goo__cell {
    border-color: var(--warn);
    box-shadow: 0 0 12px rgba(240, 180, 41, 0.25);
  }
  .goo__slot--exposed .goo__cell {
    border-color: rgba(126, 245, 184, 0.18);
  }
  .goo__slot--invalidated .goo__cell {
    border-color: rgba(255, 82, 82, 0.4);
    background:
      repeating-linear-gradient(
        45deg,
        rgba(255, 82, 82, 0.04) 0,
        rgba(255, 82, 82, 0.04) 3px,
        transparent 3px,
        transparent 6px),
      rgba(4, 8, 8, 0.6);
  }

  /* ---- The goo blob: organic shape via asymmetric border-radius,
          subtle perpetual jiggle. Pristine blobs are vivid; the slot
          modifier desaturates / dims spent or invalidated ones. ---- */
  .goo__blob {
    width: 28px;
    height: 28px;
    background: radial-gradient(circle at 35% 30%,
      color-mix(in srgb, var(--goo-color) 100%, white 35%) 0%,
      var(--goo-color) 55%,
      color-mix(in srgb, var(--goo-color) 70%, black 30%) 100%);
    box-shadow:
      0 0 12px var(--goo-glow),
      inset -2px -3px 4px color-mix(in srgb, var(--goo-color) 60%, black 40%),
      inset 2px 2px 3px color-mix(in srgb, var(--goo-color) 60%, white 40%);
    /* Asymmetric blob: gives a recognisable organic silhouette
       distinct from a circle. The keyframes morph through related
       shapes so the goo "breathes" without ever looking circular. */
    border-radius: 47% 53% 51% 49% / 49% 47% 53% 51%;
    transform: rotate(var(--goo-rot, 0deg));
    animation: goo-jiggle 6.5s ease-in-out infinite;
    transition: filter 300ms ease, opacity 300ms ease;
  }
  @keyframes goo-jiggle {
    0%, 100% {
      border-radius: 47% 53% 51% 49% / 49% 47% 53% 51%;
      transform: rotate(var(--goo-rot, 0deg)) scale(1);
    }
    25% {
      border-radius: 54% 46% 49% 51% / 52% 50% 50% 48%;
      transform: rotate(calc(var(--goo-rot, 0deg) + 8deg)) scale(1.03);
    }
    50% {
      border-radius: 49% 51% 56% 44% / 47% 53% 47% 53%;
      transform: rotate(calc(var(--goo-rot, 0deg) - 4deg)) scale(0.97);
    }
    75% {
      border-radius: 51% 49% 46% 54% / 54% 46% 52% 48%;
      transform: rotate(calc(var(--goo-rot, 0deg) + 3deg)) scale(1.02);
    }
  }
  .goo__slot--exposing .goo__blob {
    /* Active sample bubbles faster + glows harder. */
    animation-duration: 2.2s;
    filter: brightness(1.15);
  }
  .goo__slot--exposed .goo__blob {
    filter: saturate(0.25) brightness(0.5);
    opacity: 0.65;
    animation: none;          /* freeze — sample is spent. */
    box-shadow: inset 0 0 4px rgba(0, 0, 0, 0.4);
  }
  .goo__slot--invalidated .goo__blob {
    filter: saturate(0.1) brightness(0.3) hue-rotate(-20deg);
    opacity: 0.45;
    animation: none;
    box-shadow: none;
  }

  /* ---- Sweep ring: dasharray-driven progress around the active
          cell. Track is a faint accent; sweep itself uses --warn
          to match the EXPOSE pill. ---- */
  .goo__ring {
    position: absolute;
    inset: 0;
    width: 100%;
    height: 100%;
    pointer-events: none;
    transform: rotate(-90deg);  /* start at 12 o'clock */
  }
  .goo__ring-track {
    fill: none;
    stroke: rgba(240, 180, 41, 0.15);
    stroke-width: 2;
  }
  .goo__ring-sweep {
    fill: none;
    stroke: var(--warn);
    stroke-width: 2;
    stroke-linecap: round;
    stroke-dasharray: 128.8;  /* 2 × π × r where r = 20.5 ≈ 128.8 */
    filter: drop-shadow(0 0 4px rgba(240, 180, 41, 0.6));
    transition: stroke-dashoffset 250ms linear;
  }

  /* ---- State pips: overlaid on Exposed (✓) and Invalidated (✕)
          to make condition unambiguous at a glance. Pristine and
          Exposing leave the blob unobscured. ---- */
  .goo__pip {
    position: absolute;
    top: 50%;
    left: 50%;
    width: 18px;
    height: 18px;
    transform: translate(-50%, -50%);
    pointer-events: none;
  }
  .goo__pip--exposed {
    color: var(--accent);
    filter: drop-shadow(0 0 4px var(--accent-glow));
  }
  .goo__pip--invalid {
    color: var(--alert);
    filter: drop-shadow(0 0 4px rgba(255, 82, 82, 0.55));
  }

  .goo__slot-label {
    display: flex;
    gap: 4px;
    font-family: var(--font-mono);
    font-size: 9px;
    line-height: 1;
    letter-spacing: 0.04em;
  }
  .goo__slot-idx {
    color: var(--fg-mute);
    font-variant-numeric: tabular-nums;
  }
  .goo__slot-type {
    text-shadow: 0 0 4px var(--goo-glow);
  }

  /* ---- Status strip: live text + linear progress bar ---- */
  .goo__status {
    display: flex;
    flex-direction: column;
    gap: 5px;
  }
  .goo__status-text {
    font-family: var(--font-display);
    font-size: 9px;
    letter-spacing: 0.20em;
    color: var(--fg-mute);
    transition: color 200ms ease;
  }
  .goo__status--expose .goo__status-text {
    color: var(--warn);
    text-shadow: 0 0 6px var(--warn-glow);
  }
  .goo__status--open .goo__status-text {
    color: var(--accent-dim);
  }

  .goo__bar {
    height: 2px;
    background: rgba(126, 245, 184, 0.08);
    overflow: hidden;
  }
  .goo__bar-fill {
    height: 100%;
    background: linear-gradient(90deg, var(--warn) 0%, color-mix(in srgb, var(--warn) 70%, white 30%) 100%);
    box-shadow: 0 0 6px var(--warn-glow);
    transition: width 250ms linear;
  }

  /* ---- Detail grid: matches the thermometer's LIMITS/SEEN/STORAGE
          block to keep visual rhythm across instrument types. ---- */
  .goo__detail {
    display: grid;
    grid-template-columns: auto 1fr;
    column-gap: 8px;
    row-gap: 2px;
    font-variant-numeric: tabular-nums;
  }
  .goo__detail-line { display: contents; }
  .goo__detail-key {
    color: var(--fg-mute);
    font-family: var(--font-display);
    font-size: 8.5px;
    letter-spacing: 0.20em;
  }
  .goo__detail-val {
    color: var(--fg-dim);
    font-family: var(--font-mono);
    font-size: 10px;
    letter-spacing: 0.02em;
    display: flex;
    flex-wrap: wrap;
    align-items: baseline;
    gap: 8px;
  }
  .goo__count {
    font-family: var(--font-mono);
    font-size: 10px;
    letter-spacing: 0.04em;
  }
  .goo__count--pristine { color: var(--fg-dim); }
  .goo__count--exposed  { color: var(--accent); }
  .goo__count--invalid  { color: var(--alert); }
  .goo__count--mute     { color: var(--fg-mute); }
  .goo__count-cap       { color: var(--fg-mute); }
</style>
