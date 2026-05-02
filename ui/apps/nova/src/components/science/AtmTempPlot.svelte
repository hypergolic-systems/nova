<script lang="ts">
  // Live temperature-vs-altitude plot for the current atm regime.
  //
  // Each frame the topic ships a `(altitude, temperatureK)` reading.
  // We accumulate readings client-side into a per-regime bucket map
  // keyed by altitude index, so the curve fills in as the vessel
  // traverses the layer. The bucket resets when the active regime
  // (body + layer) changes — files are unbroken-observation records,
  // so the on-screen profile mirrors that scope.
  //
  // The plot only renders when the vessel is inside an actual layer
  // (currentLayerName is troposphere/stratosphere/mesosphere — not
  // "", not "surface"). X is layer-bounded altitude; Y auto-fits to
  // the temperature range observed so far. The vessel's current
  // position rides the curve as a small accent dot.
  //
  // Bucketing instead of a flat append: at 5 Hz topic rate a long
  // soak would otherwise grow unbounded, and a noisy oscillation at
  // the same altitude shouldn't pile up duplicate points. 200 buckets
  // across the layer span is more than the SVG can render distinctly
  // at 96 px wide.

  import type { AtmExperimentState } from '../../telemetry/nova-topics';
  import { untrack } from 'svelte';

  interface Props {
    atm: AtmExperimentState;
  }
  const { atm }: Props = $props();

  const W = 96;
  const H = 40;
  const PAD_L = 2;
  const PAD_R = 2;
  const PAD_T = 2;
  const PAD_B = 2;
  const PLOT_W = W - PAD_L - PAD_R;
  const PLOT_H = H - PAD_T - PAD_B;
  const BUCKETS = 200;

  const ATM_SURFACE_FLOOR_M = 1_000;

  interface Sample { alt: number; tempK: number; }

  // Plain (non-reactive) buffer — we trigger reactivity manually with
  // `revision`. Mutating a $state Map inside the same $effect that
  // reads it would loop; this side-steps the dep entirely.
  let bucket = new Map<number, Sample>();
  let activeRegime = '';
  let revision = $state(0);

  // Y-axis range — held steady once set, expanded only when a sample
  // falls outside. Continuous auto-fit while a transit is in a steep
  // gradient (e.g. troposphere descent) makes the curve "shrink" on
  // each frame as the extremes update; latching avoids that visual
  // churn. On expansion we pad by 5% of the new span (min 2 K) so the
  // line doesn't glue to the edge and the next few samples don't
  // re-trigger immediately.
  let displayMin = $state<number>(NaN);
  let displayMax = $state<number>(NaN);
  const RESCALE_PAD_FRAC = 0.05;
  const RESCALE_PAD_MIN_K = 2;

  // Current layer's altitude span (m). null when no layer is active —
  // surface / above-atm / no-atm all bail out.
  const layerBounds = $derived.by(() => {
    if (!atm.currentLayerName || atm.currentLayerName === 'surface') return null;
    const idx = atm.layers.findIndex((l) => l.name === atm.currentLayerName);
    if (idx < 0) return null;
    const top = atm.layers[idx].top;
    const bottom = idx === 0 ? ATM_SURFACE_FLOOR_M : atm.layers[idx - 1].top;
    if (top <= bottom) return null;
    return { bottom, top };
  });

  $effect(() => {
    // Capture trigger reads — these establish the dep set.
    const regime = `${atm.bodyName}::${atm.currentLayerName}`;
    const alt    = atm.altitude;
    const tempK  = atm.temperatureK;
    const lb     = layerBounds;

    untrack(() => {
      if (regime !== activeRegime) {
        bucket = new Map();
        activeRegime = regime;
        displayMin = NaN;
        displayMax = NaN;
        revision++;
      }
      if (lb == null) return;
      if (!Number.isFinite(alt) || !Number.isFinite(tempK) || tempK <= 0) return;
      if (alt < lb.bottom || alt > lb.top) return;
      const t = (alt - lb.bottom) / (lb.top - lb.bottom);
      const idx = Math.min(BUCKETS - 1, Math.max(0, Math.floor(t * BUCKETS)));
      bucket.set(idx, { alt, tempK });

      // Threshold-rescale: only expand when the new sample lies
      // outside the current display range, then pad outward so we
      // don't immediately re-trigger on the next sample.
      if (!Number.isFinite(displayMin) || !Number.isFinite(displayMax)) {
        displayMin = tempK - RESCALE_PAD_MIN_K;
        displayMax = tempK + RESCALE_PAD_MIN_K;
      } else if (tempK < displayMin || tempK > displayMax) {
        const span = Math.max(1, displayMax - displayMin);
        const pad  = Math.max(RESCALE_PAD_MIN_K, span * RESCALE_PAD_FRAC);
        if (tempK < displayMin) displayMin = tempK - pad;
        if (tempK > displayMax) displayMax = tempK + pad;
      }
      revision++;
    });
  });

  const sortedSamples = $derived.by<Sample[]>(() => {
    revision;
    return [...bucket.values()].sort((a, b) => a.alt - b.alt);
  });

  // Y-axis range — read from the latched displayMin/Max maintained by
  // the sample-ingest effect. Null until the first valid sample.
  const tempRange = $derived.by(() => {
    if (!Number.isFinite(displayMin) || !Number.isFinite(displayMax)) return null;
    return { min: displayMin, max: displayMax };
  });

  function xFor(alt: number, lb: { bottom: number; top: number }): number {
    const t = (alt - lb.bottom) / (lb.top - lb.bottom);
    return PAD_L + Math.min(1, Math.max(0, t)) * PLOT_W;
  }
  function yFor(tempK: number, tr: { min: number; max: number }): number {
    const t = (tempK - tr.min) / (tr.max - tr.min);
    return PAD_T + (1 - Math.min(1, Math.max(0, t))) * PLOT_H;
  }

  const polylinePoints = $derived.by(() => {
    const lb = layerBounds;
    const tr = tempRange;
    if (lb == null || tr == null) return '';
    return sortedSamples
      .map((s) => `${xFor(s.alt, lb).toFixed(1)},${yFor(s.tempK, tr).toFixed(1)}`)
      .join(' ');
  });

  // Vessel marker position. Hidden when off-curve / outside layer.
  const vesselDot = $derived.by(() => {
    const lb = layerBounds;
    const tr = tempRange;
    if (lb == null || tr == null) return null;
    if (atm.altitude < lb.bottom || atm.altitude > lb.top) return null;
    if (!Number.isFinite(atm.temperatureK) || atm.temperatureK <= 0) return null;
    return { x: xFor(atm.altitude, lb), y: yFor(atm.temperatureK, tr) };
  });

  function fmtK(k: number): string {
    return `${Math.round(k)}K`;
  }
</script>

<div
  class="tplot"
  title={layerBounds
    ? `Atmospheric temperature — ${atm.currentLayerName}`
    : 'No atmospheric regime'}
>
  <svg class="tplot__svg" viewBox="0 0 {W} {H}" aria-hidden="true">
    <rect x="0" y="0" width={W} height={H} class="tplot__well" />

    {#if layerBounds && tempRange && sortedSamples.length > 1}
      <polyline points={polylinePoints} class="tplot__line" fill="none" />
    {/if}

    {#if vesselDot}
      <circle cx={vesselDot.x} cy={vesselDot.y} r="1.6" class="tplot__dot" />
    {/if}

    {#if !layerBounds}
      <text x={W / 2} y={H / 2 + 3} class="tplot__none" text-anchor="middle">—</text>
    {:else if sortedSamples.length === 0}
      <text x={W / 2} y={H / 2 + 3} class="tplot__none" text-anchor="middle">…</text>
    {:else if tempRange}
      <text x={PAD_L + 1} y={PAD_T + 5} class="tplot__axis" text-anchor="start">
        {fmtK(tempRange.max)}
      </text>
      <text x={PAD_L + 1} y={H - 1} class="tplot__axis" text-anchor="start">
        {fmtK(tempRange.min)}
      </text>
    {/if}
  </svg>
</div>

<style>
  .tplot {
    flex: 0 0 auto;
  }
  .tplot__svg {
    width: 96px;
    height: 40px;
    overflow: visible;
  }

  .tplot__well {
    fill: rgba(0, 0, 0, 0.42);
    stroke: var(--line);
    stroke-width: 0.6;
  }

  .tplot__line {
    stroke: var(--accent);
    stroke-width: 1;
    stroke-linejoin: round;
    stroke-linecap: round;
    filter: drop-shadow(0 0 2px var(--accent-glow));
  }

  .tplot__dot {
    fill: var(--warn);
    stroke: var(--warn-glow);
    stroke-width: 0.4;
    filter: drop-shadow(0 0 3px var(--warn-glow));
  }

  .tplot__axis {
    fill: var(--fg-mute);
    font-family: var(--font-mono);
    font-size: 5.5px;
    letter-spacing: 0.04em;
    font-variant-numeric: tabular-nums;
  }

  .tplot__none {
    fill: var(--fg-mute);
    font-family: var(--font-display);
    font-size: 8px;
    letter-spacing: 0.18em;
  }
</style>
