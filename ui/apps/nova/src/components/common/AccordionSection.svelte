<script lang="ts">
  // One collapsible section in an Accordion. Owns no persistence —
  // open state is `$bindable` so the parent Accordion (or a sibling
  // store) decides whether to persist to localStorage, sidecar, or
  // memory only.
  //
  // The `summary` snippet is rendered ALWAYS — open or collapsed —
  // sitting in the right edge of the header. That's how a collapsed
  // POWER section can still show "−1.4 kW" without expanding. Pass
  // it `{ open }` if the summary content should differ between
  // states (e.g. hide a long detail string when already open).

  import type { Snippet } from 'svelte';

  interface Props {
    /** Stable id for accessibility + (optional) persistence. */
    id: string;
    /** Display caps title — "POWER", "THERMAL", etc. */
    title: string;
    /** Two-way bound open state. Parent reads/writes; defaults open. */
    open?: boolean;
    /** Optional small monogram glyph left of the title. */
    kindIcon?: Snippet;
    /** Right-edge collapsed-state summary; rendered open OR collapsed. */
    summary?: Snippet<[{ open: boolean }]>;
    /** Body, rendered only when `open` is true. */
    children?: Snippet;
  }

  let {
    id,
    title,
    open = $bindable(true),
    kindIcon,
    summary,
    children,
  }: Props = $props();

  const bodyId = $derived(`acs-${id}-body`);
  const headId = $derived(`acs-${id}-head`);

  function toggle(): void {
    open = !open;
  }
</script>

<section class="acs" class:acs--open={open}>
  <button
    type="button"
    id={headId}
    class="acs__head"
    aria-expanded={open}
    aria-controls={bodyId}
    onclick={toggle}
  >
    <svg
      class="acs__chev"
      class:acs__chev--open={open}
      viewBox="0 0 8 8"
      aria-hidden="true"
    >
      <path
        d="M2.2 1.4 L5.8 4 L2.2 6.6"
        fill="none"
        stroke="currentColor"
        stroke-width="1.25"
        stroke-linecap="round"
        stroke-linejoin="round"
      />
    </svg>

    {#if kindIcon}
      <span class="acs__kind" aria-hidden="true">{@render kindIcon()}</span>
    {/if}

    <span class="acs__title">{title}</span>

    <span class="acs__spacer"></span>

    {#if summary}
      <span class="acs__summary">{@render summary({ open })}</span>
    {/if}
  </button>

  {#if open && children}
    <div id={bodyId} class="acs__body" role="region" aria-labelledby={headId}>
      {@render children()}
    </div>
  {/if}
</section>

<style>
  .acs {
    display: flex;
    flex-direction: column;
    border-bottom: 1px solid var(--line);
    /* Open sections get a subtle accent edge so the rack reads as
       active panels stacked between dim dividers. */
  }
  .acs--open {
    background:
      linear-gradient(
        to right,
        rgba(126, 245, 184, 0.04) 0,
        rgba(126, 245, 184, 0) 60%
      );
  }

  .acs__head {
    display: flex;
    align-items: center;
    gap: 8px;
    padding: 8px 12px 8px 10px;
    background: transparent;
    border: none;
    border-left: 2px solid transparent;
    color: var(--fg-dim);
    font-family: var(--font-display);
    font-size: 11px;
    letter-spacing: 0.22em;
    text-transform: uppercase;
    text-align: left;
    cursor: pointer;
    transition:
      color 180ms ease,
      border-left-color 220ms cubic-bezier(0.4, 0, 0.2, 1),
      background 220ms ease;
  }
  .acs__head:focus-visible {
    outline: none;
    border-left-color: var(--accent);
  }
  .acs__head:hover {
    color: var(--accent-soft);
    border-left-color: var(--accent-dim);
  }
  .acs--open .acs__head {
    color: var(--accent);
    border-left-color: var(--accent-dim);
    text-shadow: 0 0 8px var(--accent-glow);
  }
  .acs--open .acs__head:hover {
    border-left-color: var(--accent);
  }

  .acs__chev {
    flex: 0 0 8px;
    width: 8px;
    height: 8px;
    color: currentColor;
    opacity: 0.65;
    transition:
      transform 280ms cubic-bezier(0.16, 1, 0.3, 1),
      opacity 200ms ease;
  }
  .acs__chev--open {
    transform: rotate(90deg);
    opacity: 1;
  }

  .acs__kind {
    flex: 0 0 12px;
    width: 12px;
    height: 12px;
    color: var(--accent-dim);
    display: inline-flex;
    align-items: center;
    justify-content: center;
  }

  .acs__title {
    flex: 0 0 auto;
    /* tabular-nums for any numerics inside the title (rare; reserved
       for future "PWR 1/2" style multi-instance section labels). */
    font-variant-numeric: tabular-nums;
  }

  .acs__spacer {
    flex: 1 1 auto;
  }

  /* Right-edge summary — always rendered. Mono numerics with looser
     letter-spacing than the caps title so they read as data rather
     than as a sub-label. Color drops to --fg-mute by default; the
     summary snippet can override for emphasis (e.g. WARN tint). */
  .acs__summary {
    flex: 0 1 auto;
    min-width: 0;
    color: var(--fg-mute);
    font-family: var(--font-mono);
    font-size: 10px;
    letter-spacing: 0.06em;
    text-transform: none;
    text-shadow: none;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
    transition: color 180ms ease;
  }
  .acs--open .acs__summary {
    color: var(--fg-dim);
  }
  .acs__head:hover .acs__summary {
    color: var(--fg);
  }

  .acs__body {
    padding: 8px 12px 12px;
    color: var(--fg);
    font-family: var(--font-mono);
    font-size: 11px;
  }
</style>
