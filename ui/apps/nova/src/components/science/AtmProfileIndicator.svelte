<script lang="ts">
  // Atmospheric Profile experiment status indicator.
  //
  // Eyebrow-style stacked arcs centered on the +Y vertical: each layer
  // is a 120° band (60° to either side of straight up), bottom→top in
  // increasing radius. The planet's surface sits as a small half-disc
  // at the center; a flat ground line spans the bounding box.
  //
  // Each band has a status color: blue when sealed locally, bright-
  // orange when the vessel currently transits it (will seal on exit),
  // dull-orange when the seal will fall short, dark when no data
  // exists. Bands FILL LEFT → RIGHT (sweep-fill, clockwise from the
  // left endpoint over the top) by fidelity. KSC archive fidelity
  // shows as a column of small dots on the right.
  //
  // Atmosphereless body → single ghosted placeholder; surface (alt <
  // body floor on an atmosphere body) renders as the layer label
  // "Surface" with no fill activity.

  import type { AtmExperimentState } from '../../telemetry/nova-topics';

  interface Props {
    state: AtmExperimentState;
  }
  const { state }: Props = $props();

  // Geometry — eyebrow arcs centered on +Y. 120° (= ±60° from
  // vertical) is enough to read as a layered "rainbow" without the
  // bulk of a full half-ring.
  const W = 96;
  const H = 40;
  const CX = W / 2;
  const CY = H - 4;       // small padding under the surface dot
  const R_SURFACE = 4;
  const R_MAX = 32;

  const ARC_HALF_DEG = 60;             // 120° span = ±60° from vertical
  const ARC_DEG     = ARC_HALF_DEG * 2;
  const START_DEG   = 90 + ARC_HALF_DEG; // 150° — math, CCW from +x
  const END_DEG_FULL = 90 - ARC_HALF_DEG; // 30°

  const topAlt = $derived(state.layers.at(-1)?.top ?? 0);

  // Maps altitude → radius using a linear scale so layer thickness
  // ratios are preserved (tropo : strato : meso).
  function altToR(alt: number): number {
    if (topAlt <= 0) return R_SURFACE;
    const ratio = Math.max(0, Math.min(1, alt / topAlt));
    return R_SURFACE + ratio * (R_MAX - R_SURFACE);
  }

  function deg2rad(d: number): number { return (d * Math.PI) / 180; }
  function ang(rad: number, r: number): { x: number; y: number } {
    return { x: CX + r * Math.cos(rad), y: CY - r * Math.sin(rad) };
  }

  // Annular slice between rInner and rOuter, sweeping clockwise (in
  // screen coords) from the left endpoint at 150° to `endDeg`.
  // endDeg = START_DEG ⇒ zero-width slice (callers should skip);
  // endDeg = END_DEG_FULL ⇒ full 120° band.
  function arcBand(rInner: number, rOuter: number, endDeg: number): string {
    const startRad = deg2rad(START_DEG);
    const endRad   = deg2rad(endDeg);
    const a = ang(startRad, rOuter);
    const b = ang(endRad,   rOuter);
    const c = ang(endRad,   rInner);
    const d = ang(startRad, rInner);
    // Outer arc: math-decreasing angle = screen-clockwise = sweep 1.
    // Inner arc: reverse direction = sweep 0. Both spans ≤ 120° so
    // large-arc-flag = 0 always.
    return [
      `M ${a.x} ${a.y}`,
      `A ${rOuter} ${rOuter} 0 0 1 ${b.x} ${b.y}`,
      `L ${c.x} ${c.y}`,
      `A ${rInner} ${rInner} 0 0 0 ${d.x} ${d.y}`,
      'Z',
    ].join(' ');
  }
  function arcBandFull(rInner: number, rOuter: number): string {
    return arcBand(rInner, rOuter, END_DEG_FULL);
  }
  // Thin top-edge arc only — drawn over each band so adjacent bands
  // remain readable when both share a fill state.
  function arcEdge(r: number): string {
    const a = ang(deg2rad(START_DEG),    r);
    const b = ang(deg2rad(END_DEG_FULL), r);
    return `M ${a.x} ${a.y} A ${r} ${r} 0 0 1 ${b.x} ${b.y}`;
  }

  interface RingArc {
    name: string;
    rInner: number;
    rOuter: number;
    /** Fidelity in [0,1] — drives how much of the band fills with the
     *  status color. 0 ⇒ the band reads as the empty/dark well. */
    fidelity: number;
    /** End angle (math-degrees CCW from +x) of the partial fill. */
    fillEndDeg: number;
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
      let fidelity = 0;
      if (isHere) {
        color    = state.willComplete ? 'active' : 'active-dull';
        fidelity = saved;          // active observation IS the saved file
      } else if (saved > 0) {
        color    = 'saved';
        fidelity = saved;
      }
      const rInner = altToR(prevTop);
      const rOuter = altToR(l.top);
      const clamped = Math.max(0, Math.min(1, fidelity));
      out.push({
        name:    l.name,
        rInner,
        rOuter,
        fidelity,
        fillEndDeg: START_DEG - ARC_DEG * clamped,
        ksc:     state.savedKsc.get(l.name) ?? 0,
        color,
      });
      prevTop = l.top;
    }
    return out;
  });

  const hasAtmosphere = $derived(state.layers.length > 0);

  function prettyLayer(name: string): string {
    if (!name) return '';
    return name.charAt(0).toUpperCase() + name.slice(1);
  }
</script>

<div class="atm" title={hasAtmosphere ? `${state.bodyName} atmosphere` : `${state.bodyName} — no atmosphere`}>
  <svg class="atm__svg" viewBox="0 0 {W} {H}" aria-hidden="true">
    {#if hasAtmosphere}
      <!-- empty wells, one per layer — the dark "unobserved" floor. -->
      {#each rings as r (r.name)}
        <path
          d={arcBandFull(r.rInner, r.rOuter)}
          class="atm__ring atm__ring--empty"
        />
      {/each}

      <!-- partial fill: sub-band swept clockwise from the left edge
           as fidelity climbs. fidelity=0 ⇒ no fill (well only);
           fidelity=1 ⇒ fills the whole 120° band. -->
      {#each rings as r (r.name)}
        {#if r.fidelity > 0 && r.color !== 'empty'}
          <path
            d={arcBand(r.rInner, r.rOuter, r.fillEndDeg)}
            class="atm__ring atm__ring--{r.color}"
          />
        {/if}
      {/each}

      <!-- thin layer-boundary arcs so neighbouring bands still read
           when both have the same fill state (e.g. two saved layers). -->
      {#each rings as r (r.name)}
        <path d={arcEdge(r.rOuter)} class="atm__ring-edge" fill="none" />
      {/each}
    {:else}
      <!-- ghosted placeholder when the body has no atmosphere -->
      <path d={arcBandFull(R_SURFACE, R_MAX)} class="atm__ring atm__ring--ghost" />
      <text
        x={CX} y={CY - (R_MAX + R_SURFACE) / 2 + 3}
        class="atm__none"
        text-anchor="middle"
      >—</text>
    {/if}

    <!-- planet surface — small filled half-disc at the center -->
    <path
      d={`M ${CX - R_SURFACE} ${CY} A ${R_SURFACE} ${R_SURFACE} 0 0 1 ${CX + R_SURFACE} ${CY} Z`}
      class="atm__surface"
    />
    <line
      x1={CX - R_MAX - 3} y1={CY}
      x2={CX + R_MAX + 3} y2={CY}
      class="atm__ground"
    />

    {#if hasAtmosphere && state.currentLayerName}
      <!-- Current-regime label inside the SVG (or "Surface" when
           below the body's surface floor). Top-left of the bounding
           box; the topmost arc doesn't reach the corner. -->
      <text
        x={2} y={9}
        class="atm__layer-label"
        text-anchor="start"
      >{prettyLayer(state.currentLayerName)}</text>
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
    height: 40px;
    overflow: visible;
  }

  /* Empty cell well — every band paints this base, then status color
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

  /* Current-layer / "Surface" label inside the SVG. Tiny, dim. */
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
    padding: 4px 0;
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
