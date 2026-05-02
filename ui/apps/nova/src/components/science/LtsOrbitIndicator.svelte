<script lang="ts">
  // Long-Term Study experiment status indicator.
  //
  // Renders the solar-child body's orbit around the Sun as a circular
  // ring divided into `slicesPerYear` (12) annular wedges. Each sector
  // fills clockwise within its angular slice — partial-fill width =
  // fidelity. Saved slices fill blue; the in-progress slice paints
  // orange (overrides blue when both apply). KSC archive fidelity
  // shows as a row of small green dots beneath, one per slice.
  //
  // The body marker (small circle) orbits at `phase` around the ring,
  // snapping frame-to-frame at the C# emit cadence — no JS interp.

  import type { LtsExperimentState } from '../../telemetry/nova-topics';

  interface Props {
    state: LtsExperimentState;
  }
  const { state }: Props = $props();

  // Geometry
  const W = 80;
  const H = 80;
  const CX = W / 2;
  const CY = H / 2;
  const R_OUTER = 34;
  const R_INNER = 24;
  const R_RING  = (R_OUTER + R_INNER) / 2;
  const R_SUN   = 4;

  // Slice geometry. We start the first slice at the top (12 o'clock)
  // and walk clockwise — the same convention as KSP's stock orbital
  // glyphs. Slice i spans [i*sliceArc, (i+1)*sliceArc) in radians,
  // with 0 = +x axis. Top-of-ring start means we add -π/2.
  const SLICE_ARC = $derived((Math.PI * 2) / state.slicesPerYear);

  interface SliceArc {
    index: number;
    fidelity: number;
    /**
     * Status color:
     *  - 'active'      — currently being observed, on track to seal
     *  - 'active-dull' — currently being observed, but the slice will
     *                    seal at partial fidelity (entered late or
     *                    starved). The active wedge still grows; the
     *                    color is muted to flag the shortfall.
     *  - 'saved'       — file already exists on the vessel.
     *  - 'empty'       — no observation yet.
     */
    color: 'active' | 'active-dull' | 'saved' | 'empty';
    /** SVG path for the FULL annular wedge — used as the empty-cell
     *  outline so every slice is always visible regardless of fill. */
    fullPath: string;
    /** SVG path for the FILLED portion (clockwise from start of slice
     *  to start + fidelity * sliceArc). */
    fillPath: string;
  }

  function polar(cx: number, cy: number, r: number, angle: number): [number, number] {
    return [cx + Math.cos(angle) * r, cy + Math.sin(angle) * r];
  }

  // Build one annular wedge between two angles. Two arcs (outer + inner
  // back) joined by two radial lines. `largeArc` = 1 when sweep > π.
  function annularWedge(a0: number, a1: number): string {
    const sweep = a1 - a0;
    const largeArc = sweep > Math.PI ? 1 : 0;
    const [x0o, y0o] = polar(CX, CY, R_OUTER, a0);
    const [x1o, y1o] = polar(CX, CY, R_OUTER, a1);
    const [x0i, y0i] = polar(CX, CY, R_INNER, a0);
    const [x1i, y1i] = polar(CX, CY, R_INNER, a1);
    return [
      `M ${x0o} ${y0o}`,
      `A ${R_OUTER} ${R_OUTER} 0 ${largeArc} 1 ${x1o} ${y1o}`,
      `L ${x1i} ${y1i}`,
      `A ${R_INNER} ${R_INNER} 0 ${largeArc} 0 ${x0i} ${y0i}`,
      'Z',
    ].join(' ');
  }

  const slices = $derived.by<SliceArc[]>(() => {
    const out: SliceArc[] = [];
    for (let i = 0; i < state.slicesPerYear; i++) {
      const a0 = -Math.PI / 2 + i * SLICE_ARC;
      const a1 = a0 + SLICE_ARC;

      const localFid = state.savedLocal.get(i) ?? 0;
      const isCurrent = i === state.currentSliceIndex;
      let color: SliceArc['color'] = 'empty';
      let fidelity = 0;
      if (state.active && isCurrent) {
        color = state.willComplete ? 'active' : 'active-dull';
        fidelity = state.activeFidelity;
      } else if (localFid > 0) {
        color = 'saved';
        fidelity = localFid;
      }

      const fillEnd = a0 + Math.max(0, Math.min(1, fidelity)) * SLICE_ARC;
      out.push({
        index: i,
        fidelity,
        color,
        fullPath: annularWedge(a0, a1),
        fillPath: fidelity > 0 ? annularWedge(a0, fillEnd) : '',
      });
    }
    return out;
  });

  // Body-marker position on the ring. Phase is 0..1 around the year;
  // 0 = top of ring (matches slice 0 start).
  const markerAngle = $derived(-Math.PI / 2 + state.phase * Math.PI * 2);
  const markerXY    = $derived(polar(CX, CY, R_RING, markerAngle));

  // Recorded-phase coverage. The bracket [recordedMinPhase,
  // recordedMaxPhase] maps to absolute year-angles; we draw an outer
  // arc segment along that span so the player sees "I observed from
  // here to here". Sentinel: max <= min ⇒ no observation (hide).
  const hasRecorded = $derived(state.recordedMaxPhase > state.recordedMinPhase);
  const recordedArcPath = $derived.by(() => {
    if (!hasRecorded) return '';
    const a0 = -Math.PI / 2 + state.recordedMinPhase * Math.PI * 2;
    const a1 = -Math.PI / 2 + state.recordedMaxPhase * Math.PI * 2;
    const [x0, y0] = polar(CX, CY, R_OUTER + 2, a0);
    const [x1, y1] = polar(CX, CY, R_OUTER + 2, a1);
    const sweep = a1 - a0;
    const largeArc = sweep > Math.PI ? 1 : 0;
    return `M ${x0} ${y0} A ${R_OUTER + 2} ${R_OUTER + 2} 0 ${largeArc} 1 ${x1} ${y1}`;
  });

  // Slice number label position — outside the body marker, offset
  // along the same radial.
  const sliceLabelXY = $derived.by(() => {
    const r = R_OUTER + 8;
    return polar(CX, CY, r, markerAngle);
  });

  // KSC dots strip: one per slice, left→right by index.
  const kscDots = $derived.by(() =>
    Array.from({ length: state.slicesPerYear }, (_, i) => ({
      i,
      fid: state.savedKsc.get(i) ?? 0,
    })),
  );
</script>

<div class="lts" title={`${state.bodyName} · ${state.situation} · slice ${state.currentSliceIndex + 1}/${state.slicesPerYear}`}>
  <svg class="lts__svg" viewBox="0 0 {W} {H}" aria-hidden="true">
    <!-- inner sun glyph with a soft static glow -->
    <defs>
      <radialGradient id="lts-sun-glow" cx="50%" cy="50%" r="50%">
        <stop offset="0%"   stop-color="var(--warn)" stop-opacity="1" />
        <stop offset="60%"  stop-color="var(--warn)" stop-opacity="0.45" />
        <stop offset="100%" stop-color="var(--warn)" stop-opacity="0" />
      </radialGradient>
    </defs>
    <circle cx={CX} cy={CY} r={R_SUN * 2.4} class="lts__sun-halo" />
    <circle cx={CX} cy={CY} r={R_SUN}        class="lts__sun" />

    <!-- annular slice cells (always visible) -->
    {#each slices as s (s.index)}
      <path d={s.fullPath} class="lts__cell" />
    {/each}

    <!-- partial fills, one per slice (skip empty) -->
    {#each slices as s (s.index)}
      {#if s.color !== 'empty'}
        <path d={s.fillPath} class="lts__fill lts__fill--{s.color}" />
      {/if}
    {/each}

    <!-- slice-boundary radial ticks. Drawn after fills so they read
         on top — gives the indicator the segmented-clock feel. -->
    {#each slices as s (s.index)}
      {@const a = -Math.PI / 2 + s.index * SLICE_ARC}
      {@const [x0, y0] = polar(CX, CY, R_INNER, a)}
      {@const [x1, y1] = polar(CX, CY, R_OUTER, a)}
      <line x1={x0} y1={y0} x2={x1} y2={y1} class="lts__tick" />
    {/each}

    <!-- orbit guide ring at midline; subtle, sits behind the marker -->
    <circle cx={CX} cy={CY} r={R_RING} class="lts__ring" />

    <!-- recorded-phase coverage: outer arc spanning the year-angles
         where the instrument has actively observed during the current
         slice. Sits outside the wedge ring so the in-progress fill
         stays uncluttered. -->
    {#if hasRecorded}
      <path d={recordedArcPath} class="lts__recorded" fill="none" />
    {/if}

    <!-- body marker -->
    <circle
      cx={markerXY[0]} cy={markerXY[1]} r="2.4"
      class="lts__body"
    />

    <!-- slice number label outside the body marker -->
    <text
      x={sliceLabelXY[0]} y={sliceLabelXY[1] + 2}
      class="lts__slice-label"
      text-anchor="middle"
    >{state.currentSliceIndex + 1}/{state.slicesPerYear}</text>
  </svg>

  <ul class="lts__ksc" aria-label="KSC archive fidelity per slice">
    {#each kscDots as d (d.i)}
      <li
        class="lts__ksc-dot"
        class:lts__ksc-dot--lit={d.fid > 0}
        style:opacity={d.fid > 0 ? 0.4 + d.fid * 0.6 : 0.18}
      ></li>
    {/each}
  </ul>
</div>

<style>
  .lts {
    display: flex;
    flex-direction: column;
    align-items: center;
    gap: 4px;
  }
  .lts__svg {
    width: 80px;
    height: 80px;
    overflow: visible;
  }

  .lts__sun {
    fill: var(--warn);
    filter: drop-shadow(0 0 4px var(--warn-glow));
  }
  .lts__sun-halo {
    fill: url(#lts-sun-glow);
    opacity: 0.7;
  }

  .lts__cell {
    fill: rgba(0, 0, 0, 0.42);
    stroke: var(--line);
    stroke-width: 0.6;
  }
  .lts__fill--saved {
    fill: var(--info);
    opacity: 0.82;
    filter: drop-shadow(0 0 3px var(--info-glow));
  }
  .lts__fill--active {
    fill: var(--warn);
    opacity: 0.92;
    filter: drop-shadow(0 0 4px var(--warn-glow));
    animation: lts-pulse 1.6s ease-in-out infinite;
  }
  /* Dull-orange variant — same hue, half opacity, no glow. The
     accumulator is still ticking but won't reach 1.0 by slice end,
     so the wedge reads "in progress, but degraded". */
  .lts__fill--active-dull {
    fill: var(--warn);
    opacity: 0.42;
    animation: lts-pulse 2.2s ease-in-out infinite;
  }

  .lts__tick {
    stroke: var(--line-bright);
    stroke-width: 0.6;
    opacity: 0.55;
  }
  .lts__ring {
    fill: none;
    stroke: var(--accent-dim);
    stroke-width: 0.5;
    stroke-dasharray: 1 2;
    opacity: 0.55;
  }
  .lts__body {
    fill: var(--accent);
    filter: drop-shadow(0 0 3px var(--accent-glow));
  }
  /* Recorded-phase coverage arc — sits outside the orbit ring,
     spanning the year-angles where the instrument has been actively
     observing in the current slice. Dim accent so it reads as
     "covered" without competing with the live wedge fill. */
  .lts__recorded {
    stroke: var(--accent-dim);
    stroke-width: 2;
    opacity: 0.85;
    stroke-linecap: round;
  }
  /* Slice number label — small, dim, sits outside the orbit ring near
     the body marker. */
  .lts__slice-label {
    fill: var(--fg-dim);
    font-family: var(--font-display);
    font-size: 6.5px;
    letter-spacing: 0.06em;
  }

  /* 12 dots in a horizontal strip below the orbit. Mirrors atm
     indicator's KSC column convention but in row form. */
  .lts__ksc {
    list-style: none;
    margin: 0;
    padding: 0;
    display: flex;
    gap: 3px;
    align-items: center;
  }
  .lts__ksc-dot {
    width: 3.5px;
    height: 3.5px;
    border-radius: 50%;
    background: rgba(126, 245, 184, 0.18);
    transition: background 220ms ease, box-shadow 220ms ease;
  }
  .lts__ksc-dot--lit {
    background: var(--accent);
    box-shadow: 0 0 4px var(--accent-glow);
  }

  @keyframes lts-pulse {
    0%, 100% { opacity: 0.92; }
    50%      { opacity: 1; }
  }
</style>
