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
    | 'engine'
    | 'tank'
    | 'fuelCell'
    | 'command'
    | 'dataStorage'
    | 'thermometer';

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
  {:else if kind === 'fuelCell'}
    <!-- A short stack of plates, evoking PEM cell membranes. Three
         filled bars with thin gaps between read clearly at 12 px and
         distinguish from the battery's thicker outline + terminal cap. -->
    <rect x="2.4" y="2.4" width="7.2" height="1.4" fill="currentColor" />
    <rect x="2.4" y="5.3" width="7.2" height="1.4" fill="currentColor" />
    <rect x="2.4" y="8.2" width="7.2" height="1.4" fill="currentColor" />
  {:else if kind === 'command'}
    <!-- Avionics / flight computer: a chip outline with four short
         pin stubs on the top and bottom edges. -->
    <rect x="3" y="3.5" width="6" height="5" rx="0.5"
          stroke="currentColor" fill="none" stroke-width="0.95" />
    <g stroke="currentColor" stroke-width="0.85" stroke-linecap="round">
      <line x1="4.4" y1="2.2" x2="4.4" y2="3.4" />
      <line x1="6"   y1="2.2" x2="6"   y2="3.4" />
      <line x1="7.6" y1="2.2" x2="7.6" y2="3.4" />
      <line x1="4.4" y1="8.6" x2="4.4" y2="9.8" />
      <line x1="6"   y1="8.6" x2="6"   y2="9.8" />
      <line x1="7.6" y1="8.6" x2="7.6" y2="9.8" />
    </g>
  {:else if kind === 'tank'}
    <!-- Vertical fuel tank: capsule body with two thin horizontal
         bands suggesting the welded bulkheads at top and bottom of a
         real propellant tank. The top band sits just below the dome
         (y=3) and the bottom just above (y=9), giving the cylinder
         a sense of internal structure at 12 px. -->
    <rect x="3" y="1.5" width="6" height="9" rx="1.1"
          stroke="currentColor" fill="none" stroke-width="0.95" />
    <line x1="3.4" y1="3" x2="8.6" y2="3"
          stroke="currentColor" stroke-width="0.7" stroke-linecap="round" />
    <line x1="3.4" y1="9" x2="8.6" y2="9"
          stroke="currentColor" stroke-width="0.7" stroke-linecap="round" />
  {:else if kind === 'dataStorage'}
    <!-- Stack of three thin platters — recalls a storage drum / data
         drive without committing to a specific era. Each platter is
         a flat ellipse with a hairline mid-line for parallax. -->
    <g stroke="currentColor" fill="none" stroke-width="0.85" stroke-linecap="round">
      <ellipse cx="6" cy="3"   rx="3.6" ry="0.95" />
      <ellipse cx="6" cy="6"   rx="3.6" ry="0.95" />
      <ellipse cx="6" cy="9"   rx="3.6" ry="0.95" />
      <line x1="2.4" y1="3" x2="2.4" y2="9" />
      <line x1="9.6" y1="3" x2="9.6" y2="9" />
    </g>
  {:else if kind === 'thermometer'}
    <!-- Bulb + stem with a narrow column. The bulb sits at the bottom
         and the stem rises through the icon center; a single tick
         mark at mid-stem keeps it readable at 12 px. -->
    <g stroke="currentColor" fill="none" stroke-width="0.95" stroke-linecap="round">
      <circle cx="6" cy="9.4" r="1.7" fill="currentColor" stroke="none" />
      <line x1="6" y1="1.4" x2="6" y2="8" stroke-width="1.3" />
      <line x1="7.5" y1="4.2" x2="8.6" y2="4.2" />
      <line x1="7.5" y1="6.4" x2="8.6" y2="6.4" />
    </g>
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
