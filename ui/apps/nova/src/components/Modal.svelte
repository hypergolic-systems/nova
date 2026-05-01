<script lang="ts">
  // Generic modal primitive: a dimmed, blurred backdrop above the rest
  // of the HUD, a centered panel below the cursor, Esc / outside-click
  // to dismiss. Title slot + body slot. The panel inherits Nova's
  // chrome — frosted glass, 1px accent border, mono type — without
  // re-declaring it; CSS variables come from Dragonglass tokens.

  import type { Snippet } from 'svelte';
  import { onMount } from 'svelte';

  interface Props {
    /** Render the modal? Bound by the caller — Esc / click outside
     *  call `onClose` to flip it back to false. */
    open: boolean;
    /** Player-facing title in the header bar. Plain text. */
    title?: string;
    /** Subtitle line, smaller, dim. Used for context like "12 files". */
    subtitle?: string;
    onClose: () => void;
    children?: Snippet;
  }
  const { open, title = '', subtitle = '', onClose, children }: Props = $props();

  let panel = $state<HTMLDivElement | null>(null);

  function onKeydown(e: KeyboardEvent): void {
    if (e.key === 'Escape' && open) {
      e.stopPropagation();
      onClose();
    }
  }

  function onBackdropClick(e: MouseEvent): void {
    if (e.target === e.currentTarget) onClose();
  }

  // Focus the panel on open so keystrokes hit our keydown handler.
  $effect(() => {
    if (open && panel) panel.focus();
  });

  onMount(() => {
    window.addEventListener('keydown', onKeydown);
    return () => window.removeEventListener('keydown', onKeydown);
  });

  // Portal: pop the backdrop out of the in-tree stacking context (the
  // FloatingWindow uses backdrop-filter, which makes its descendants'
  // `position: fixed` resolve relative to it instead of the viewport).
  // Re-parent to document.body so the modal genuinely covers the page.
  function portalToBody(node: HTMLElement) {
    document.body.appendChild(node);
    return {
      destroy() {
        node.remove();
      },
    };
  }
</script>

{#if open}
  <div
    use:portalToBody
    class="md__backdrop"
    role="presentation"
    onclick={onBackdropClick}
  >
    <div
      bind:this={panel}
      class="md__panel"
      role="dialog"
      aria-modal="true"
      aria-label={title}
      tabindex="-1"
    >
      <header class="md__head">
        <div class="md__head-text">
          <span class="md__title">{title}</span>
          {#if subtitle}<span class="md__subtitle">{subtitle}</span>{/if}
        </div>
        <button
          type="button"
          class="md__close"
          aria-label="Close"
          onclick={onClose}
        >×</button>
      </header>
      <div class="md__body">
        {@render children?.()}
      </div>
    </div>
  </div>
{/if}

<style>
  /* Backdrop covers the FloatingWindow's stack and the rest of the
     HUD. The blur is heavier than the panel's own backdrop so the
     modal reads as foregrounded; the dim is light enough to keep
     the underlying dashboard legible (this is a player who's still
     "in mission", not a context-disrupting modal). */
  .md__backdrop {
    position: fixed;
    inset: 0;
    z-index: 10000;
    display: flex;
    align-items: center;
    justify-content: center;
    background: rgba(0, 0, 0, 0.32);
    backdrop-filter: blur(4px) saturate(110%);
    -webkit-backdrop-filter: blur(4px) saturate(110%);
    animation: md-backdrop-in 180ms cubic-bezier(0.16, 1, 0.3, 1);
  }

  .md__panel {
    width: min(1400px, 96vw);
    height: min(900px, 92vh);
    display: flex;
    flex-direction: column;
    background: var(--bg-panel-strong);
    border: 1px solid var(--line-accent);
    color: var(--fg);
    font-family: var(--font-mono);
    font-size: 11px;
    letter-spacing: 0.02em;
    box-shadow:
      inset 0 0 0 1px rgba(126, 245, 184, 0.06),
      0 24px 60px -8px rgba(0, 0, 0, 0.6),
      0 0 0 1px rgba(0, 0, 0, 0.4);
    backdrop-filter: blur(14px) saturate(125%);
    -webkit-backdrop-filter: blur(14px) saturate(125%);
    outline: none;
    animation: md-panel-in 240ms cubic-bezier(0.16, 1, 0.3, 1);
  }

  .md__head {
    flex: 0 0 auto;
    display: flex;
    align-items: baseline;
    gap: 12px;
    padding: 8px 10px 6px 12px;
    border-bottom: 1px solid var(--line);
    background: var(--bg-elev);
  }
  .md__head-text {
    flex: 1 1 auto;
    display: flex;
    align-items: baseline;
    gap: 12px;
    min-width: 0;
  }
  .md__title {
    font-family: var(--font-display);
    font-size: 13px;
    letter-spacing: 0.18em;
    color: var(--accent);
    text-shadow: 0 0 6px var(--accent-glow);
    text-transform: uppercase;
  }
  .md__subtitle {
    font-family: var(--font-mono);
    font-size: 10px;
    color: var(--fg-mute);
    letter-spacing: 0.12em;
    text-transform: uppercase;
    white-space: nowrap;
  }
  .md__close {
    flex: 0 0 auto;
    appearance: none;
    background: transparent;
    border: 1px solid transparent;
    color: var(--fg-dim);
    font-family: var(--font-display);
    font-size: 16px;
    line-height: 1;
    width: 22px;
    height: 22px;
    cursor: pointer;
    transition:
      color 160ms ease,
      border-color 160ms ease,
      background 160ms ease;
  }
  .md__close:hover,
  .md__close:focus-visible {
    color: var(--accent);
    border-color: var(--accent-dim);
    background: rgba(126, 245, 184, 0.06);
    outline: none;
  }

  .md__body {
    flex: 1 1 auto;
    min-height: 0;
    overflow: auto;
    padding: 10px 12px;
  }

  .md__body::-webkit-scrollbar {
    width: 8px;
  }
  .md__body::-webkit-scrollbar-track {
    background: rgba(0, 0, 0, 0.25);
    border-left: 1px solid var(--line);
  }
  .md__body::-webkit-scrollbar-thumb {
    background: rgba(126, 245, 184, 0.18);
    border: 1px solid transparent;
    background-clip: padding-box;
  }
  .md__body::-webkit-scrollbar-thumb:hover {
    background: rgba(126, 245, 184, 0.42);
    background-clip: padding-box;
  }

  @keyframes md-backdrop-in {
    from { opacity: 0; }
    to   { opacity: 1; }
  }
  @keyframes md-panel-in {
    from { opacity: 0; transform: translateY(-6px) scale(0.985); }
    to   { opacity: 1; transform: none; }
  }
</style>
