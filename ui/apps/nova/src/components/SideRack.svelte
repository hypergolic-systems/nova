<script lang="ts">
  // Docked vertical instrument rack. Pins to the right edge of the
  // viewport top-to-bottom; resizable on its left edge only. Hosts
  // the Vessel accordion. Replaces the previous FloatingWindow-based
  // VesselPanel chrome.
  //
  // Width state lives in two places by design:
  //   * --nova-rack-w on <html> — single source of truth, read by
  //     the rack (for `width`) AND by FlightTopBar (for `right`)
  //     so the L-corner alignment resolves in the browser without
  //     a Svelte rerender loop.
  //   * localStorage["nova.rack.w"] — persists user-chosen width
  //     across scene reloads. Written on pointerup, not on every
  //     move (cheap, sufficient).

  import type { Snippet } from 'svelte';
  import { onMount, onDestroy } from 'svelte';

  interface Props {
    children: Snippet;
  }

  let { children }: Props = $props();

  const DEFAULT_W = 320;
  const MIN_W = 280;
  const STORAGE_KEY = 'nova.rack.w';

  let width = $state(DEFAULT_W);
  let dragging = $state(false);

  function maxW(): number {
    return Math.min(640, Math.round(window.innerWidth * 0.5));
  }

  function clamp(w: number): number {
    return Math.max(MIN_W, Math.min(maxW(), w));
  }

  function applyWidth(w: number): void {
    width = clamp(w);
    document.documentElement.style.setProperty('--nova-rack-w', `${width}px`);
  }

  onMount(() => {
    // Hydrate from storage; fall back to DEFAULT_W. The clamp guards
    // against a stored value that's now too wide because the user
    // shrank the window between sessions.
    const raw = localStorage.getItem(STORAGE_KEY);
    const parsed = raw ? Number(raw) : NaN;
    applyWidth(Number.isFinite(parsed) ? parsed : DEFAULT_W);
  });

  onDestroy(() => {
    // Don't strip --nova-rack-w on unmount: if FlightTopBar is still
    // mounted (e.g. during a partial scene swap), zeroing it would
    // glitch the strip to full-width for a frame. Leaving the value
    // in place is harmless — next mount of SideRack overwrites it.
  });

  // ---- Resize handle ---------------------------------------------

  let startX = 0;
  let startW = 0;

  function onPointerDown(e: PointerEvent): void {
    // Only primary button — middle/right click on the handle would
    // otherwise start a drag.
    if (e.button !== 0) return;
    dragging = true;
    startX = e.clientX;
    startW = width;
    (e.target as Element).setPointerCapture?.(e.pointerId);
    window.addEventListener('pointermove', onPointerMove);
    window.addEventListener('pointerup', onPointerUp);
    e.preventDefault();
  }

  function onPointerMove(e: PointerEvent): void {
    if (!dragging) return;
    // Dragging the LEFT edge leftward grows the rack; rightward shrinks.
    const delta = startX - e.clientX;
    applyWidth(startW + delta);
  }

  function onPointerUp(): void {
    if (!dragging) return;
    dragging = false;
    window.removeEventListener('pointermove', onPointerMove);
    window.removeEventListener('pointerup', onPointerUp);
    try {
      localStorage.setItem(STORAGE_KEY, String(width));
    } catch {
      // Quota / disabled storage — silently ignore; the in-memory
      // value still drives the layout this session.
    }
  }
</script>

<aside
  class="sr nova-surface"
  class:sr--dragging={dragging}
  aria-label="Vessel rack"
>
  <!-- Resize handle: 6 px-wide invisible strip at the left edge with
       a 1 px accent pip that lights up on hover/active. Sits OUTSIDE
       the scroll body so dragging the handle doesn't trigger child
       hovers underneath. -->
  <div
    class="sr__handle"
    role="separator"
    aria-orientation="vertical"
    aria-label="Resize vessel rack"
    onpointerdown={onPointerDown}
  >
    <div class="sr__handle-pip"></div>
  </div>

  <div class="sr__body">
    {@render children()}
  </div>
</aside>

<style>
  .sr {
    /* `--nova-rack-w` is hydrated on mount and written back on every
       resize. Setting a fallback here too so the rack still draws
       sensibly during the first paint before onMount runs.
       Subordinated to the top strip — the rack starts at y=48 px
       (= strip height) so the strip dominates the L-corner. This
       matches the conventional dashboard layout (Slack, VS Code,
       Linear, etc.) where the header runs full-width and the
       sidebar tucks underneath. */
    --_w: var(--nova-rack-w, 320px);
    position: fixed;
    top: 48px;
    right: 0;
    bottom: 0;
    width: var(--_w);
    z-index: 50;

    display: flex;
    flex-direction: row;
    background: var(--bg-panel-strong);
    border-left: 1px solid var(--line-accent);
    box-shadow: inset 1px 0 0 rgba(126, 245, 184, 0.05);
    color: var(--fg);
    font-family: var(--font-mono);
    font-size: 11px;
    letter-spacing: 0.02em;
    user-select: none;
  }

  /* Dragging swaps body cursor to ew-resize globally so the cursor
     doesn't flicker when the pointer briefly leaves the 6 px strip
     between samples. */
  .sr--dragging {
    cursor: ew-resize;
  }
  :global(body:has(.sr--dragging)) {
    cursor: ew-resize;
  }

  /* ----- Resize handle ----- */
  .sr__handle {
    flex: 0 0 6px;
    width: 6px;
    cursor: ew-resize;
    position: relative;
    /* The handle reaches just past the left border so the pip sits
       *on* the bezel rather than inside the body. */
    margin-left: -3px;
    z-index: 2;
  }
  .sr__handle-pip {
    position: absolute;
    top: 50%;
    left: 50%;
    transform: translate(-50%, -50%);
    width: 1px;
    height: 36px;
    background: transparent;
    transition:
      background 180ms ease,
      box-shadow 180ms ease,
      height 220ms cubic-bezier(0.16, 1, 0.3, 1);
  }
  .sr__handle:hover .sr__handle-pip,
  .sr--dragging .sr__handle-pip {
    background: var(--accent);
    box-shadow: 0 0 6px var(--accent-glow);
    height: 64px;
  }

  /* ----- Body: scrollable column ----- */
  .sr__body {
    flex: 1 1 0;
    min-width: 0;
    min-height: 0;
    overflow-y: scroll;
    overflow-x: hidden;
    /* No top padding — the rack itself starts at y=48 px (below the
       strip), so the body's natural top edge already sits flush with
       the strip's bottom border. */
  }
  /* Themed scrollbar — match the existing instrument-panel gutter. */
  .sr__body::-webkit-scrollbar {
    width: 8px;
    height: 8px;
  }
  .sr__body::-webkit-scrollbar-track {
    background: rgba(0, 0, 0, 0.18);
    border-left: 1px solid var(--line);
  }
  .sr__body::-webkit-scrollbar-thumb {
    background: rgba(126, 245, 184, 0.18);
    border: 1px solid transparent;
    background-clip: padding-box;
    transition: background 200ms ease;
  }
  .sr__body::-webkit-scrollbar-thumb:hover {
    background: rgba(126, 245, 184, 0.42);
    background-clip: padding-box;
  }
  .sr__body::-webkit-scrollbar-corner {
    background: rgba(0, 0, 0, 0.18);
  }

  /* No L-corner pseudo: with the strip dominating the top edge
     full-width and the rack subordinated below, the corner where
     they meet is a clean right-angle T (strip's `border-bottom` +
     rack's `border-left` both in `--line-accent`). The earlier
     chamfer was designed for the inverted "rack-on-top" layout
     and would read as visual noise in the conventional layout. */
</style>
