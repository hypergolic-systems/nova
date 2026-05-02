<script lang="ts">
  // Atmospheric Profile experiment status indicator.
  //
  // Wifi-style semicircular layout: the planet's surface sits at the
  // bottom-center as a small filled half-disc; the atmosphere layers
  // are concentric half-rings stacked outward. A vessel marker rides
  // a vertical radial from the center, positioned at the radius that
  // corresponds to the live altitude — so as the vessel ascends the
  // marker pushes outward through the layered shells, like a WiFi
  // signal-strength glyph but radial-out instead of angular-out.
  //
  // Each layer-ring carries a status color: blue when sealed locally,
  // bright-orange when the vessel is currently transiting it (will
  // seal on exit), dull-orange when the seal will fall short, dark
  // when no data exists. KSC archive fidelity shows as a ladder of
  // small green dots arrayed along the radial axis (one dot per
  // layer, innermost-first), matching the LTS indicator's KSC strip.
  //
  // Atmosphereless body → single ghosted "—" placeholder; the layout
  // doesn't reflow when the vessel transits between bodies.

  import type { AtmExperimentState } from '../../telemetry/nova-topics';

  interface Props {
    state: AtmExperimentState;
  }
  const { state }: Props = $props();

  // Geometry — semicircle bounding box. Center at bottom-center.
  // The arc fans up and out; surface lives at the center.
  const W = 96;
  const H = 60;
  const CX = W / 2;
  const CY = H - 8;       // a few px of padding under the surface
  const R_SURFACE = 5;    // half-disc radius (the planet)
  const R_MAX = 46;       // outermost arc radius (top of topmost layer)

  const topAlt = $derived(state.layers.at(-1)?.top ?? 0);

  // Maps altitude → radius using a linear scale so layer thickness
  // ratios are preserved (tropo : strato : meso).
  function altToR(alt: number): number {
    if (topAlt <= 0) return R_SURFACE;
    const ratio = Math.max(0, Math.min(1, alt / topAlt));
    return R_SURFACE + ratio * (R_MAX - R_SURFACE);
  }

  // Semicircular annular path from `rInner` to `rOuter`, sweeping
  // 180° from (cx-r, cy) up around to (cx+r, cy).
  function halfRingPath(rInner: number, rOuter: number): string {
    return [
      `M ${CX - rOuter} ${CY}`,
      `A ${rOuter} ${rOuter} 0 0 1 ${CX + rOuter} ${CY}`,
      `L ${CX + rInner} ${CY}`,
      `A ${rInner} ${rInner} 0 0 0 ${CX - rInner} ${CY}`,
      'Z',
    ].join(' ');
  }

  interface RingArc {
    name: string;
    rInner: number;
    rOuter: number;
    saved: number;
    ksc: number;
    color: 'saved' | 'active' | 'active-dull' | 'empty';
  }

  // Resolve which layer the vessel is currently in. Identical contract
  // to `AtmosphericProfileExperiment.LayerAt` on the C# side.
  const currentLayer = $derived.by(() => {
    for (const l of state.layers) if (state.altitude < l.top) return l.name;
    return null;
  });

  const rings = $derived.by<RingArc[]>(() => {
    if (state.layers.length === 0 || topAlt <= 0) return [];
    const out: RingArc[] = [];
    let prevTop = 0;
    for (const l of state.layers) {
      const saved = state.savedLocal.get(l.name) ?? 0;
      const isHere = state.active && currentLayer === l.name;
      let color: RingArc['color'] = 'empty';
      if (isHere) {
        color = state.willComplete ? 'active' : 'active-dull';
      } else if (saved > 0) {
        color = 'saved';
      }
      out.push({
        name:    l.name,
        rInner:  altToR(prevTop),
        rOuter:  altToR(l.top),
        saved,
        ksc:     state.savedKsc.get(l.name) ?? 0,
        color,
      });
      prevTop = l.top;
    }
    return out;
  });

  // Vessel altitude marker: rides the +Y radial from center upward,
  // at radius = altitude in scaled units. Above the topmost layer
  // (in space) we let it float past R_MAX.
  const markerR = $derived(
    topAlt > 0
      ? Math.max(R_SURFACE + 1, R_SURFACE + (state.altitude / topAlt) * (R_MAX - R_SURFACE))
      : R_SURFACE,
  );
  const markerY = $derived(CY - markerR);

  const hasAtmosphere = $derived(state.layers.length > 0);

  // Recorded-bounds bracket: tick marks + connecting line on the +Y
  // radial at the radii corresponding to `transitMinAlt` / `transitMaxAlt`.
  // Sentinel: max <= min ⇒ no observation, hide the bracket entirely.
  const hasBracket = $derived(state.transitMaxAlt > state.transitMinAlt);
  const bracketTopY    = $derived(CY - altToR(state.transitMaxAlt));
  const bracketBottomY = $derived(CY - altToR(state.transitMinAlt));

  // Pretty-print the current layer name for the small label inside the
  // SVG. Title-case, to read as a label rather than a wire id.
  function prettyLayer(name: string): string {
    if (!name) return '';
    return name.charAt(0).toUpperCase() + name.slice(1);
  }
</script>

<div class="atm" title={hasAtmosphere ? `${state.bodyName} atmosphere` : `${state.bodyName} — no atmosphere`}>
  <svg class="atm__svg" viewBox="0 0 {W} {H}" aria-hidden="true">
    {#if hasAtmosphere}
      <!-- atmospheric layer rings, innermost → outermost -->
      {#each rings as r (r.name)}
        <path
          d={halfRingPath(r.rInner, r.rOuter)}
          class="atm__ring atm__ring--{r.color}"
        />
      {/each}

      <!-- thin layer-boundary arcs so neighbouring rings still read
           when both have the same fill state (e.g. two saved layers) -->
      {#each rings as r (r.name)}
        <path
          d={`M ${CX - r.rOuter} ${CY} A ${r.rOuter} ${r.rOuter} 0 0 1 ${CX + r.rOuter} ${CY}`}
          class="atm__ring-edge"
          fill="none"
        />
      {/each}
    {:else}
      <!-- ghosted placeholder when the body has no atmosphere -->
      <path d={halfRingPath(R_SURFACE, R_MAX)} class="atm__ring atm__ring--ghost" />
      <text
        x={CX} y={CY - (R_MAX + R_SURFACE) / 2 + 3}
        class="atm__none"
        text-anchor="middle"
      >—</text>
    {/if}

    <!-- planet surface — small filled half-disc at the center -->
    <path
      d={halfRingPath(0, R_SURFACE)}
      class="atm__surface"
    />
    <line
      x1={CX - R_MAX - 3} y1={CY}
      x2={CX + R_MAX + 3} y2={CY}
      class="atm__ground"
    />

    {#if hasAtmosphere}
      <!-- vessel altitude marker — radial pointer with a small triangle
           tip. The radial line stays full-length so the eye reads
           position-along-radius rather than just-a-dot. -->
      <line
        x1={CX} y1={CY - R_SURFACE}
        x2={CX} y2={markerY}
        class="atm__radial"
      />
      <polygon
        points="{CX},{markerY - 3} {CX - 3},{markerY + 2} {CX + 3},{markerY + 2}"
        class="atm__marker"
      />

      {#if hasBracket}
        <!-- Recorded altitude bracket — the range the vessel has covered
             during the current layer transit. Two short horizontal ticks
             on either side of the radial, with a vertical line connecting
             them. Renders to the right of the radial so the live marker
             still reads cleanly. -->
        <line
          x1={CX + 4} y1={bracketTopY}
          x2={CX + 4} y2={bracketBottomY}
          class="atm__bracket"
        />
        <line
          x1={CX + 2} y1={bracketTopY}
          x2={CX + 7} y2={bracketTopY}
          class="atm__bracket-tick"
        />
        <line
          x1={CX + 2} y1={bracketBottomY}
          x2={CX + 7} y2={bracketBottomY}
          class="atm__bracket-tick"
        />
      {/if}

      {#if state.currentLayerName}
        <!-- Currently-observed layer name. Top-left of the bounding box;
             the topmost arc is symmetric so this corner is empty even
             when the largest ring is fully filled. -->
        <text
          x={2} y={9}
          class="atm__layer-label"
          text-anchor="start"
        >{prettyLayer(state.currentLayerName)}</text>
      {/if}
    {/if}
  </svg>

  {#if hasAtmosphere}
    <ul class="atm__ksc" aria-label="KSC archive fidelity per layer">
      {#each rings as r (r.name)}
        <li
          class="atm__ksc-dot"
          class:atm__ksc-dot--lit={r.ksc > 0}
          style:opacity={r.ksc > 0 ? 0.4 + r.ksc * 0.6 : 0.18}
        ></li>
      {/each}
    </ul>
  {/if}
</div>

<style>
  .atm {
    display: flex;
    align-items: stretch;
    gap: 4px;
  }
  .atm__svg {
    width: 96px;
    height: 60px;
    overflow: visible;
  }

  /* Empty cell well — every ring paints this base, then status color
     overlays. Keeps unobserved layers visible as concentric outlines. */
  .atm__ring {
    fill: rgba(0, 0, 0, 0.42);
    stroke: var(--line);
    stroke-width: 0.6;
  }
  .atm__ring--ghost {
    fill: rgba(0, 0, 0, 0.22);
    stroke-dasharray: 2 2;
    opacity: 0.6;
  }
  .atm__ring--saved {
    fill: var(--info);
    fill-opacity: 0.78;
    filter: drop-shadow(0 0 3px var(--info-glow));
  }
  .atm__ring--active {
    fill: var(--warn);
    fill-opacity: 0.88;
    filter: drop-shadow(0 0 4px var(--warn-glow));
    animation: atm-pulse 1.6s ease-in-out infinite;
  }
  /* Dull-orange — in progress, won't reach full seal. Same hue, half
     opacity, slower pulse, no glow. */
  .atm__ring--active-dull {
    fill: var(--warn);
    fill-opacity: 0.40;
    animation: atm-pulse 2.2s ease-in-out infinite;
  }
  .atm__ring-edge {
    stroke: var(--line-bright);
    stroke-width: 0.6;
    opacity: 0.5;
  }

  .atm__surface {
    fill: var(--line-bright);
    opacity: 0.85;
    stroke: var(--fg-mute);
    stroke-width: 0.5;
  }
  .atm__ground {
    stroke: var(--line-accent);
    stroke-width: 1;
    opacity: 0.7;
  }

  .atm__radial {
    stroke: var(--accent-soft);
    stroke-width: 0.7;
    opacity: 0.55;
  }
  .atm__marker {
    fill: var(--accent-soft);
    filter: drop-shadow(0 0 4px var(--accent-glow));
  }
  /* Recorded-altitude bracket — alt-min/alt-max ticks during the
     current transit. Dim accent so it reads as "previously seen" and
     doesn't fight the live vessel marker. */
  .atm__bracket {
    stroke: var(--accent-dim);
    stroke-width: 0.7;
    opacity: 0.7;
  }
  .atm__bracket-tick {
    stroke: var(--accent);
    stroke-width: 0.9;
    opacity: 0.85;
  }
  /* Current-layer label inside the SVG. Tiny, dim — intentional weak
     contrast so it doesn't compete with the rings or markers. */
  .atm__layer-label {
    fill: var(--fg-dim);
    font-family: var(--font-display);
    font-size: 6.5px;
    letter-spacing: 0.10em;
    text-transform: uppercase;
  }

  .atm__none {
    fill: var(--fg-mute);
    font-family: var(--font-display);
    font-size: 8px;
    letter-spacing: 0.18em;
  }

  .atm__ksc {
    list-style: none;
    margin: 0;
    padding: 0;
    display: flex;
    flex-direction: column;
    justify-content: space-around;
    gap: 4px;
    padding: 8px 0 6px 0;
  }
  .atm__ksc-dot {
    width: 4px;
    height: 4px;
    border-radius: 50%;
    background: rgba(126, 245, 184, 0.18);
    transition: background 220ms ease, box-shadow 220ms ease;
  }
  .atm__ksc-dot--lit {
    background: var(--accent);
    box-shadow: 0 0 4px var(--accent-glow);
  }

  @keyframes atm-pulse {
    0%, 100% { fill-opacity: var(--atm-base, 0.78); }
    50%      { fill-opacity: 1; }
  }
</style>
