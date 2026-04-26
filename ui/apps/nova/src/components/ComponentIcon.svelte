<script lang="ts">
  // Tiny monochrome glyph for a Nova virtual-component kind. Used in
  // every subsystem view's leading row column, so the same shape
  // means the same component across Power, Propulsion, RCS, etc.
  // Inherits color via `currentColor` so the surrounding row class
  // (active/dim/warn) drives the tint without per-icon overrides.
  // Add a new kind here and every consumer picks it up for free.

  export type ComponentKind =
    | 'solar'
    | 'battery'
    | 'wheel'
    | 'light'
    | 'engine';

  let { kind }: { kind: ComponentKind } = $props();
</script>

<svg class="ci" viewBox="0 0 12 12" aria-hidden="true">
  {#if kind === 'solar'}
    <!-- A core dot with eight cardinal/diagonal rays. The dot's radius
         (1.9) plus a 0.4 gap to the ray starts gives the rays room to
         breathe instead of fusing into the disc at small sizes. -->
    <circle cx="6" cy="6" r="1.9" fill="currentColor" />
    <g stroke="currentColor" stroke-width="0.95" stroke-linecap="round">
      <line x1="6"   y1="0.6" x2="6"   y2="2.1" />
      <line x1="6"   y1="9.9" x2="6"   y2="11.4" />
      <line x1="0.6" y1="6"   x2="2.1" y2="6" />
      <line x1="9.9" y1="6"   x2="11.4" y2="6" />
      <line x1="2.3" y1="2.3" x2="3.3" y2="3.3" />
      <line x1="8.7" y1="8.7" x2="9.7" y2="9.7" />
      <line x1="2.3" y1="9.7" x2="3.3" y2="8.7" />
      <line x1="8.7" y1="3.3" x2="9.7" y2="2.3" />
    </g>
  {:else if kind === 'battery'}
    <!-- Cell body + terminal cap. Two interior ticks read as
         "discrete cells" and keep the icon legible at 12 px without
         the thicker single bar that previously read as a glitch. -->
    <rect x="1.4" y="3.5" width="8.2" height="5" rx="0.6"
          stroke="currentColor" fill="none" stroke-width="0.9" />
    <rect x="9.6" y="4.7" width="1.3" height="2.6" fill="currentColor" />
    <line x1="4.2" y1="5.2" x2="4.2" y2="6.8"
          stroke="currentColor" stroke-width="0.9" stroke-linecap="round" />
    <line x1="6.8" y1="5.2" x2="6.8" y2="6.8"
          stroke="currentColor" stroke-width="0.9" stroke-linecap="round" />
  {:else if kind === 'wheel'}
    <!-- Reaction wheel: hub + ring + four short cardinal teeth. -->
    <circle cx="6" cy="6" r="3" stroke="currentColor" fill="none" stroke-width="0.95" />
    <circle cx="6" cy="6" r="0.95" fill="currentColor" />
    <g stroke="currentColor" stroke-width="0.95" stroke-linecap="round">
      <line x1="6"   y1="0.6" x2="6"   y2="2.1" />
      <line x1="6"   y1="9.9" x2="6"   y2="11.4" />
      <line x1="0.6" y1="6"   x2="2.1" y2="6" />
      <line x1="9.9" y1="6"   x2="11.4" y2="6" />
    </g>
  {:else if kind === 'light'}
    <!-- Bulb + two base ribs. Slightly off-axis vertical centering
         (5 instead of 4.5) lets the base read as part of the lamp. -->
    <circle cx="6" cy="4.6" r="2.7" stroke="currentColor" fill="none" stroke-width="0.95" />
    <line x1="4.2" y1="8.5"  x2="7.8" y2="8.5"
          stroke="currentColor" stroke-width="0.95" stroke-linecap="round" />
    <line x1="4.7" y1="10.4" x2="7.3" y2="10.4"
          stroke="currentColor" stroke-width="0.95" stroke-linecap="round" />
  {:else if kind === 'engine'}
    <!-- Bell-shaped nozzle. The horizontal "throat" line at y=3.5
         hints at the combustion chamber/nozzle interface, which makes
         the silhouette read as an engine instead of a generic vase. -->
    <path d="M3.6 1 L8.4 1 L11 11 L1 11 Z"
          stroke="currentColor" fill="none"
          stroke-width="0.95" stroke-linejoin="round" />
    <line x1="4.3" y1="3.5" x2="7.7" y2="3.5"
          stroke="currentColor" stroke-width="0.95" stroke-linecap="round" />
  {/if}
</svg>

<style>
  .ci {
    display: block;
    width: 12px;
    height: 12px;
    flex: 0 0 auto;
    color: var(--fg-mute);
    overflow: visible;
    /* Strokes stay 0.95 viewBox-units wide regardless of any future
       up-scaling — the icons keep a hairline weight in larger
       contexts instead of growing into bold blobs. */
    vector-effect: non-scaling-stroke;
  }
</style>
