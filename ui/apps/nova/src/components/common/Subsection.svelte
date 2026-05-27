<script lang="ts">
  // Subordinate collapsible block — sits INSIDE an AccordionSection
  // body to fold sub-content (e.g. System's Stored Commands and
  // Communications). Visually distinct from the parent section:
  // smaller header type, no full-width separator, a thin left-edge
  // bracket so the body reads as a child of the title above it.
  //
  // Same shape as AccordionSection (bindable `open`, optional
  // `summary` snippet rendered both open and closed), so callers
  // don't have to relearn an API for nested folds.
  //
  // Pure visual primitive — owns no persistence. Parents decide
  // whether to bind `open` to a store or keep it in-memory.

  import type { Snippet } from 'svelte';

  interface Props {
    /** Display title — sentence-case or caps; the head will uppercase. */
    title: string;
    /** Two-way bound open state. Defaults open. */
    open?: boolean;
    /** Right-edge always-rendered summary snippet. */
    summary?: Snippet<[{ open: boolean }]>;
    /** Body, rendered only when `open` is true. */
    children?: Snippet;
  }

  let {
    title,
    open = $bindable(true),
    summary,
    children,
  }: Props = $props();

  function toggle(): void {
    open = !open;
  }
</script>

<div class="ss" class:ss--open={open}>
  <button
    type="button"
    class="ss__head"
    aria-expanded={open}
    onclick={toggle}
  >
    <svg
      class="ss__chev"
      class:ss__chev--open={open}
      viewBox="0 0 8 8"
      aria-hidden="true"
    >
      <path
        d="M2.5 1.6 L5.6 4 L2.5 6.4"
        fill="none"
        stroke="currentColor"
        stroke-width="1.1"
        stroke-linecap="round"
        stroke-linejoin="round"
      />
    </svg>
    <span class="ss__title">{title}</span>
    <span class="ss__spacer"></span>
    {#if summary}
      <span class="ss__summary">{@render summary({ open })}</span>
    {/if}
  </button>

  {#if open && children}
    <div class="ss__body">
      {@render children()}
    </div>
  {/if}
</div>

<style>
  /* The subsection deliberately ducks beneath the parent
     AccordionSection in every visual register:
       • smaller header (9.5 px vs 11 px)
       • muted colour (no accent glow even when open)
       • lighter weight chevron
       • no full-width separator rule between siblings
       • body lives inside a faint left-edge bracket that
         connects it visually to the title above it
     The intent is that an open subsection reads as "still inside
     the parent's territory", while the parent's full-width head +
     accent edge + glow remains the dominant fold control. */
  .ss {
    display: flex;
    flex-direction: column;
  }

  .ss__head {
    display: flex;
    align-items: center;
    gap: 6px;
    padding: 4px 2px 5px 2px;
    background: transparent;
    border: 0;
    color: var(--fg-mute);
    font-family: var(--font-display);
    font-size: 9.5px;
    letter-spacing: 0.20em;
    text-transform: uppercase;
    text-align: left;
    cursor: pointer;
    transition: color 160ms ease;
  }
  .ss__head:hover {
    color: var(--fg-dim);
  }
  .ss__head:focus-visible {
    outline: none;
    color: var(--accent-soft, var(--accent));
  }
  .ss--open .ss__head {
    color: var(--fg-dim);
  }
  .ss--open .ss__head:hover {
    color: var(--fg);
  }

  .ss__chev {
    flex: 0 0 7px;
    width: 7px;
    height: 7px;
    color: currentColor;
    opacity: 0.55;
    transition:
      transform 220ms cubic-bezier(0.16, 1, 0.3, 1),
      opacity 200ms ease;
  }
  .ss__chev--open {
    transform: rotate(90deg);
    opacity: 0.85;
  }

  .ss__title {
    flex: 0 0 auto;
    font-variant-numeric: tabular-nums;
  }
  .ss__spacer {
    flex: 1 1 auto;
  }

  /* Summary mirrors the parent AccordionSection's vocabulary but
     ducks a register (smaller, looser tracking, fg-mute). Open
     state nudges to fg-dim — same "wake up on open" gesture as
     the parent, just at a lower brightness. */
  .ss__summary {
    flex: 0 1 auto;
    min-width: 0;
    color: var(--fg-mute);
    font-family: var(--font-mono);
    font-size: 9.5px;
    letter-spacing: 0.04em;
    text-transform: none;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
    transition: color 180ms ease;
  }
  .ss--open .ss__summary {
    color: var(--fg-dim);
  }
  .ss__head:hover .ss__summary {
    color: var(--fg);
  }

  /* Body: indented by the chevron column so the body's text
     left-aligns with the title characters above it. A single
     hairline on the left edge connects body content to title,
     fading out top and bottom so it reads as an inscribed
     bracket rather than a hard rule. The colour stays faint so
     it doesn't compete with the parent section's accent edge. */
  .ss__body {
    padding: 4px 0 6px 13px;
    margin-left: 3px;
    position: relative;
  }
  .ss__body::before {
    content: '';
    position: absolute;
    left: 0;
    top: 2px;
    bottom: 4px;
    width: 1px;
    background:
      linear-gradient(
        to bottom,
        transparent 0%,
        color-mix(in srgb, var(--line-accent) 50%, transparent) 18%,
        color-mix(in srgb, var(--line-accent) 50%, transparent) 82%,
        transparent 100%
      );
    pointer-events: none;
  }
</style>
