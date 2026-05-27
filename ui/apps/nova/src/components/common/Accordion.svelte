<script lang="ts">
  // Vertical stack of AccordionSections. Pure visual frame — owns no
  // state, just the wrapper that gives every section a consistent
  // dividing rule and zero-padding flush layout. Sections govern
  // their own open state.

  import type { Snippet } from 'svelte';

  interface Props {
    children: Snippet;
  }

  let { children }: Props = $props();
</script>

<div class="ac" role="group">
  {@render children()}
</div>

<style>
  .ac {
    display: flex;
    flex-direction: column;
    min-height: 0;
    /* Sections own their own borders top/bottom; the wrapper itself
       provides no chrome so a single Accordion can nest cleanly
       inside any container. */
  }
</style>
