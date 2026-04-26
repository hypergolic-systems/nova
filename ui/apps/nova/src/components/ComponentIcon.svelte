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
    <circle cx="6" cy="6" r="2" fill="currentColor" />
    <g stroke="currentColor" stroke-width="1" stroke-linecap="round">
      <line x1="6" y1="0.5" x2="6" y2="2" />
      <line x1="6" y1="10" x2="6" y2="11.5" />
      <line x1="0.5" y1="6" x2="2" y2="6" />
      <line x1="10" y1="6" x2="11.5" y2="6" />
      <line x1="2.1" y1="2.1" x2="3.2" y2="3.2" />
      <line x1="8.8" y1="8.8" x2="9.9" y2="9.9" />
      <line x1="2.1" y1="9.9" x2="3.2" y2="8.8" />
      <line x1="8.8" y1="3.2" x2="9.9" y2="2.1" />
    </g>
  {:else if kind === 'battery'}
    <rect x="1.5" y="3.5" width="8" height="5" rx="0.5"
          stroke="currentColor" fill="none" stroke-width="1" />
    <rect x="9.5" y="4.75" width="1.25" height="2.5" fill="currentColor" />
    <line x1="3.5" y1="6" x2="7.5" y2="6"
          stroke="currentColor" stroke-width="1" stroke-linecap="round" />
  {:else if kind === 'wheel'}
    <circle cx="6" cy="6" r="3" stroke="currentColor" fill="none" stroke-width="1" />
    <circle cx="6" cy="6" r="0.9" fill="currentColor" />
    <g stroke="currentColor" stroke-width="1" stroke-linecap="round">
      <line x1="6" y1="0.5" x2="6" y2="2" />
      <line x1="6" y1="10" x2="6" y2="11.5" />
      <line x1="0.5" y1="6" x2="2" y2="6" />
      <line x1="10" y1="6" x2="11.5" y2="6" />
    </g>
  {:else if kind === 'light'}
    <circle cx="6" cy="4.5" r="2.75" stroke="currentColor" fill="none" stroke-width="1" />
    <line x1="4.25" y1="8.5" x2="7.75" y2="8.5"
          stroke="currentColor" stroke-width="1" stroke-linecap="round" />
    <line x1="4.75" y1="10.5" x2="7.25" y2="10.5"
          stroke="currentColor" stroke-width="1" stroke-linecap="round" />
  {:else if kind === 'engine'}
    <path d="M3.5 1 L8.5 1 L11 11 L1 11 Z"
          stroke="currentColor" fill="none" stroke-width="1" stroke-linejoin="round" />
    <line x1="4.25" y1="3.5" x2="7.75" y2="3.5"
          stroke="currentColor" stroke-width="1" stroke-linecap="round" />
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
  }
</style>
