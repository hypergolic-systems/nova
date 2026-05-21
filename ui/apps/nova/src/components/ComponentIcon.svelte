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
    | 'nuclear'
    | 'rcs'
    | 'tank'
    | 'fuelCell'
    | 'command'
    | 'probe'
    | 'antenna'
    | 'decoupler'
    | 'docking'
    | 'crew'
    | 'dataStorage'
    | 'thermometer'
    | 'rtg'
    | 'radiator';

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
  {:else if kind === 'radiator'}
    <!-- Heat radiator: a vertical spine with three pairs of fins.
         Reads as parallel cooling fins at 12 px and is unambiguously
         distinct from the battery's plates and the fuel cell's stack. -->
    <line x1="6" y1="1.4" x2="6" y2="10.6"
          stroke="currentColor" stroke-width="1.1" stroke-linecap="round" />
    <g stroke="currentColor" stroke-width="0.95" stroke-linecap="round">
      <line x1="2.2" y1="3"   x2="9.8" y2="3" />
      <line x1="2.2" y1="6"   x2="9.8" y2="6" />
      <line x1="2.2" y1="9"   x2="9.8" y2="9" />
    </g>
  {:else if kind === 'rtg'}
    <!-- Radiation trefoil: solid hub + three 60° blades at 120°
         intervals (one up, two down-flanking). ISO-361 silhouette
         simplified to flat-arc polygons so the shape stays crisp at
         12 px without needing tiny SVG arcs. -->
    <circle cx="6" cy="6" r="1.5" fill="currentColor" />
    <g fill="currentColor">
      <path d="M 3.5 1.67 L 6 1 L 8.5 1.67 L 6 3.5 Z" />
      <path d="M 11 6 L 10.33 8.5 L 8.5 10.33 L 8.17 7.25 Z" />
      <path d="M 1 6 L 1.67 8.5 L 3.5 10.33 L 3.83 7.25 Z" />
    </g>
  {:else if kind === 'nuclear'}
    <!-- Nuclear engine: the engine bell silhouette with a small
         radiation trefoil seated in the combustion chamber. The bell
         is a touch narrower than the plain engine icon so the trefoil
         has room to breathe at the top. -->
    <path d="M4 2.5 L8 2.5 L10.6 11 L1.4 11 Z"
          stroke="currentColor" fill="none"
          stroke-width="0.9" stroke-linejoin="round" />
    <circle cx="6" cy="4.6" r="0.65" fill="currentColor" />
    <g fill="currentColor">
      <path d="M 4.9 3.3 L 6 2.9 L 7.1 3.3 L 6 4.2 Z" />
      <path d="M 8.2 5.4 L 7.7 6.6 L 6.6 5.0 Z" />
      <path d="M 3.8 5.4 L 4.3 6.6 L 5.4 5.0 Z" />
    </g>
  {:else if kind === 'rcs'}
    <!-- RCS thruster cluster: four small outward-facing nozzles in a
         plus pattern around a central manifold. Reads as "multi-axis
         thruster array" — distinct from the single-bell `engine`. -->
    <circle cx="6" cy="6" r="1.1" fill="currentColor" />
    <g stroke="currentColor" fill="none" stroke-width="0.85" stroke-linejoin="round">
      <path d="M5 2.4 L7 2.4 L7.4 0.9 L4.6 0.9 Z" />
      <path d="M5 9.6 L7 9.6 L7.4 11.1 L4.6 11.1 Z" />
      <path d="M2.4 5 L2.4 7 L0.9 7.4 L0.9 4.6 Z" />
      <path d="M9.6 5 L9.6 7 L11.1 7.4 L11.1 4.6 Z" />
    </g>
  {:else if kind === 'probe'}
    <!-- Compact probe core: a small body with a single antenna stub on
         top. Deliberately smaller silhouette than `command` so the
         visual hierarchy reads "probe < pod". -->
    <rect x="3.4" y="4.4" width="5.2" height="4.6" rx="0.5"
          stroke="currentColor" fill="none" stroke-width="0.9" />
    <line x1="6" y1="1.4" x2="6" y2="4.2"
          stroke="currentColor" stroke-width="0.9" stroke-linecap="round" />
    <line x1="4.5" y1="2.1" x2="7.5" y2="2.1"
          stroke="currentColor" stroke-width="0.85" stroke-linecap="round" />
    <line x1="4.6" y1="9.9" x2="7.4" y2="9.9"
          stroke="currentColor" stroke-width="0.7" stroke-linecap="round" />
  {:else if kind === 'antenna'}
    <!-- Parabolic dish on a short mast with a feed horn at the focal
         point. The dish is drawn as a wide-open arc; the feed is a
         tiny perpendicular tick that sells the optics. -->
    <path d="M2 6 Q6 1.5 10 6"
          stroke="currentColor" fill="none"
          stroke-width="0.95" stroke-linecap="round" />
    <line x1="6" y1="3.6" x2="6" y2="5.0"
          stroke="currentColor" stroke-width="0.85" stroke-linecap="round" />
    <circle cx="6" cy="5.2" r="0.55" fill="currentColor" />
    <line x1="6" y1="6"   x2="6" y2="10.6"
          stroke="currentColor" stroke-width="0.95" stroke-linecap="round" />
    <line x1="4.4" y1="10.8" x2="7.6" y2="10.8"
          stroke="currentColor" stroke-width="0.95" stroke-linecap="round" />
  {:else if kind === 'decoupler'}
    <!-- Two stacked rectangles separated by a thin gap with outward
         chevrons on the seam — reads as "separation event". -->
    <rect x="2.2" y="1.6" width="7.6" height="3.6"
          stroke="currentColor" fill="none" stroke-width="0.9" />
    <rect x="2.2" y="6.8" width="7.6" height="3.6"
          stroke="currentColor" fill="none" stroke-width="0.9" />
    <g stroke="currentColor" fill="none" stroke-width="0.85" stroke-linecap="round" stroke-linejoin="round">
      <polyline points="0.8,6 2,5.4 0.8,4.8" />
      <polyline points="11.2,6 10,5.4 11.2,4.8" />
    </g>
  {:else if kind === 'docking'}
    <!-- Docking port: outer ring with four capture petals at cardinal
         positions inside, evoking an APAS-style soft-capture interface. -->
    <circle cx="6" cy="6" r="4" stroke="currentColor" fill="none" stroke-width="0.95" />
    <circle cx="6" cy="6" r="1.5" stroke="currentColor" fill="none" stroke-width="0.85" />
    <g stroke="currentColor" stroke-width="0.95" stroke-linecap="round">
      <line x1="6"   y1="2.6" x2="6"   y2="3.8" />
      <line x1="6"   y1="8.2" x2="6"   y2="9.4" />
      <line x1="2.6" y1="6"   x2="3.8" y2="6" />
      <line x1="8.2" y1="6"   x2="9.4" y2="6" />
    </g>
  {:else if kind === 'crew'}
    <!-- Single torso silhouette: round head over a trapezoidal body.
         Deliberately abstract — reads as "person" without committing
         to a specific suit or pose. -->
    <circle cx="6" cy="3.5" r="1.7" fill="currentColor" />
    <path d="M2.7 11 L3.6 6.6 L8.4 6.6 L9.3 11 Z" fill="currentColor" />
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
