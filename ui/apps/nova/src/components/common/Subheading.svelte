<script lang="ts">
  // Non-collapsible labelled section — sits inside an
  // AccordionSection body to group a set of related items under
  // one caps heading. Same visual register as Subsection (small
  // caps title + right-aligned summary slot) but without the
  // chevron, click handler, or fold state. Used where the data
  // underneath is small enough that folding it away would be more
  // friction than scrolling past it, and where the heading exists
  // primarily to *label* the content rather than gate access to
  // it.
  //
  // No body indent — content sits flush with the heading text on
  // the left margin. This keeps the readable column wide; nested
  // groupings get a heading row but no left-bracket rule.

  import type { Snippet } from 'svelte';

  interface Props {
    title: string;
    summary?: Snippet;
    children?: Snippet;
  }

  let { title, summary, children }: Props = $props();
</script>

<div class="sh">
  <div class="sh__head">
    <span class="sh__title">{title}</span>
    <span class="sh__spacer"></span>
    {#if summary}
      <span class="sh__summary">{@render summary()}</span>
    {/if}
  </div>
  {#if children}
    <div class="sh__body">
      {@render children()}
    </div>
  {/if}
</div>

<style>
  .sh {
    display: flex;
    flex-direction: column;
  }

  /* Caps heading row. Same typographic register as Subsection so
     the two read as siblings in the visual hierarchy, just without
     the interactive chevron/hover treatment. */
  .sh__head {
    display: flex;
    align-items: center;
    gap: 6px;
    padding: 4px 2px 5px 2px;
    color: var(--fg-dim);
    font-family: var(--font-display);
    font-size: 9.5px;
    letter-spacing: 0.20em;
    text-transform: uppercase;
  }
  .sh__title {
    flex: 0 0 auto;
    font-variant-numeric: tabular-nums;
  }
  .sh__spacer {
    flex: 1 1 auto;
  }
  .sh__summary {
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
  }

  /* Body sits flush — no indent, no left rule. The heading
     typography alone carries the group label; reserving extra
     horizontal space would defeat the purpose of using a heading
     instead of a fold. */
  .sh__body {
    padding-top: 2px;
  }
</style>
