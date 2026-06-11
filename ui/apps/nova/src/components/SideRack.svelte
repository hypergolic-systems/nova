<script lang="ts">
  // Docked vertical instrument rack. Pins to the right edge of the
  // viewport, top-to-bottom. A drawer-pull-style tab on the left
  // edge — vertically centred — doubles as a click-to-collapse and
  // drag-to-resize handle:
  //
  //   • click (no movement)    → toggle collapsed
  //   • drag past threshold    → resize to follow pointer
  //   • drag from collapsed    → expand + resize in one motion
  //
  // Width and collapsed state both persist to localStorage, so the
  // player's rack disposition survives scene reloads.
  //
  // Animation model: the whole chassis (col + body) slides as a
  // single unit. `transform: translateX(width - TAB_W)` when
  // collapsed parks the body off-screen to the right while leaving
  // the tab column visible at the viewport's right edge. That way
  // the close gesture reads as a drawer being pushed in — body
  // content slides off with the chassis — rather than the body
  // popping out of existence while the tab flies separately.
  //
  // `--nova-rack-w` on <html> reports the effective visible width
  // (TAB_W when collapsed) for any future external consumers.

  import type { Snippet } from 'svelte';
  import { onMount } from 'svelte';

  interface Props {
    children: Snippet;
    /** Inset from the viewport top, in px. Flight docks below its
     *  48-px FlightTopBar; the editor passes a larger value to clear
     *  KSP's launch/save/load button row in the top-right. */
    top?: number;
    /** localStorage key prefix for width + collapsed state. Distinct
     *  per scene so the editor rack's disposition is independent of
     *  the flight rack's. Section open-state is persisted separately
     *  by the panel that fills the rack. */
    storageKey?: string;
  }

  let { children, top = 48, storageKey = 'nova.rack' }: Props = $props();

  const DEFAULT_W = 320;
  const MIN_W = 280;
  /** Width of `.sr__col` — the strip that stays in-view when collapsed. */
  const TAB_W = 8;
  const DRAG_THRESHOLD = 4;
  const STORAGE_W = $derived(`${storageKey}.w`);
  const STORAGE_C = $derived(`${storageKey}.collapsed`);

  let width = $state(DEFAULT_W);
  let collapsed = $state(false);
  let dragging = $state(false);

  function maxW(): number {
    return Math.min(640, Math.round(window.innerWidth * 0.5));
  }

  function clamp(w: number): number {
    return Math.max(MIN_W, Math.min(maxW(), w));
  }

  function publish(): void {
    document.documentElement.style.setProperty(
      '--nova-rack-w',
      collapsed ? `${TAB_W}px` : `${width}px`,
    );
  }

  onMount(() => {
    const rawW = localStorage.getItem(STORAGE_W);
    const parsedW = rawW ? Number(rawW) : NaN;
    width = clamp(Number.isFinite(parsedW) ? parsedW : DEFAULT_W);
    collapsed = localStorage.getItem(STORAGE_C) === '1';
    publish();
  });

  // ---- Tab pointer handling --------------------------------------

  let active = false;
  let moved = false;
  let downX = 0;
  let startW = 0;

  function onPointerDown(e: PointerEvent): void {
    if (e.button !== 0) return;
    active = true;
    moved = false;
    downX = e.clientX;
    startW = width;
    (e.target as Element).setPointerCapture?.(e.pointerId);
    window.addEventListener('pointermove', onPointerMove);
    window.addEventListener('pointerup', onPointerUp);
    e.preventDefault();
  }

  function onPointerMove(e: PointerEvent): void {
    if (!active) return;
    const delta = downX - e.clientX;
    if (!moved && Math.abs(delta) < DRAG_THRESHOLD) return;
    moved = true;
    dragging = true;
    if (collapsed) {
      collapsed = false;
    }
    width = clamp(startW + delta);
    publish();
  }

  function onPointerUp(): void {
    if (!active) return;
    active = false;
    window.removeEventListener('pointermove', onPointerMove);
    window.removeEventListener('pointerup', onPointerUp);

    if (!moved) {
      collapsed = !collapsed;
      publish();
    }
    dragging = false;
    try {
      localStorage.setItem(STORAGE_W, String(width));
      localStorage.setItem(STORAGE_C, collapsed ? '1' : '0');
    } catch {
      /* quota / disabled storage — silently ignore */
    }
  }

  // Transform value is set inline so the initial paint snaps to the
  // hydrated collapsed state instead of animating from open to
  // closed on first mount.
  const transform = $derived(
    collapsed ? `translateX(${width - TAB_W}px)` : 'translateX(0)',
  );
</script>

<aside
  class="sr"
  class:sr--collapsed={collapsed}
  class:sr--dragging={dragging}
  style:width="{width}px"
  style:top="{top}px"
  style:transform={transform}
  aria-label="Vessel rack"
>
  <!-- Tab column: holds the drawer-pull tab. 32-px wide strip that
       remains in the viewport when the rest of the chassis has
       slid off to the right. The tab itself is absolutely
       positioned and vertically centred relative to the full rack
       height. -->
  <div class="sr__col">
    <button
      type="button"
      class="sr__tab"
      aria-label={collapsed ? 'Expand vessel rack' : 'Collapse vessel rack'}
      aria-expanded={!collapsed}
      aria-controls="sr-body"
      title={collapsed ? 'Expand (drag to resize)' : 'Collapse (drag to resize)'}
      onpointerdown={onPointerDown}
    >
      <span class="sr__tab-grip" aria-hidden="true"></span>
      <svg class="sr__tab-chev" viewBox="0 0 8 12" aria-hidden="true">
        <path
          d="M5.5 1 L1.5 6 L5.5 11"
          fill="none"
          stroke="currentColor"
          stroke-width="1.3"
          stroke-linecap="round"
          stroke-linejoin="round"
        />
      </svg>
      <span class="sr__tab-grip" aria-hidden="true"></span>
    </button>
  </div>

  <div id="sr-body" class="sr__body nova-surface">
    {@render children()}
  </div>
</aside>

<style>
  /* Chassis. Width stays at the user's chosen size (set inline); the
     collapse animation rides on `transform: translateX`. Because the
     body is a child of the aside, it slides with the chassis as
     one unit — closes feel like a drawer being pushed in rather
     than the body vanishing in place. */
  .sr {
    position: fixed;
    /* top inset set inline via style:top (default 48px) */
    right: 0;
    bottom: 0;
    z-index: 50;
    /* width set inline via style:width */
    display: flex;
    flex-direction: row;
    color: var(--fg);
    font-family: var(--font-mono);
    font-size: 11px;
    letter-spacing: 0.02em;
    user-select: none;
    /* Cubic-out so the slide reads as confident travel, not as a
       linear march. Disabled during drag (see below) so resize
       tracks the pointer with no lag. */
    transition: transform 320ms cubic-bezier(0.16, 1, 0.3, 1);
    will-change: transform;
  }
  .sr--collapsed {
    /* Anywhere outside the tab is effectively off-screen — let
       clicks fall through to the underlying scene. */
    pointer-events: none;
  }
  .sr--collapsed .sr__col,
  .sr--collapsed .sr__tab {
    pointer-events: auto;
  }
  .sr--dragging {
    transition: none;
    cursor: ew-resize;
  }
  :global(body:has(.sr--dragging)) {
    cursor: ew-resize;
  }

  /* ----- Tab column ----- */
  .sr__col {
    flex: 0 0 8px;
    width: 8px;
    position: relative;
  }

  /* ----- Industrial pull tab -----
     A thin tall handle (12 × 56, ~5:1 aspect). The 45° edges go
     STRAIGHT from the outer face to the body wall — no horizontal
     top/bottom segments. The polygon therefore has only four
     vertices: two on the body wall (right) and two on the outer
     face (left). The outer face is 32 px tall (the centre half of
     the bounding box), with 12-px diagonal cuts above and below
     that connect it directly to the body's top and bottom
     corners. Vertically centred so the tab sits at the player's
     eye-line regardless of viewport height. */
  .sr__tab {
    position: absolute;
    top: 50%;
    left: -4px; /* small protrusion outside col so the handle reads
                   as something that sticks out from the rack edge */
    width: 12px;
    height: 56px;
    margin-top: -28px;
    box-sizing: border-box;
    padding: 0;
    background:
      linear-gradient(
        180deg,
        rgba(126, 245, 184, 0.20) 0%,
        rgba(126, 245, 184, 0.06) 55%,
        rgba(126, 245, 184, 0.00) 100%
      ),
      var(--bg-panel-strong);
    border: 0;
    border-radius: 0;
    /* Four-vertex polygon: body-top, body-bottom, outer-bottom,
       outer-top. Diagonals connect outer face directly to body
       corners at 45° (Δx = Δy = tab_width = 12). */
    clip-path: polygon(
      100% 0,
      100% 100%,
      0 calc(100% - 12px),
      0 12px
    );
    /* `filter: drop-shadow` (unlike `box-shadow`) follows the
       clipped polygon outline. Layered: a hard offset shadow for
       a faint accent halo on the protruding side, then a soft
       drop for depth. */
    filter:
      drop-shadow(-2px 0 0 rgba(126, 245, 184, 0.18))
      drop-shadow(-3px 1px 6px rgba(0, 0, 0, 0.5));
    display: flex;
    flex-direction: column;
    align-items: center;
    justify-content: center;
    gap: 4px;
    cursor: pointer;
    color: var(--fg-mute);
    transition:
      color 180ms ease,
      background 220ms ease,
      filter 220ms ease;
    z-index: 2;
  }
  .sr--collapsed .sr__tab {
    /* When standalone the tab needs a slightly heavier shadow so it
       reads as floating from the screen edge, not stuck to it. */
    filter:
      drop-shadow(-2px 0 0 rgba(126, 245, 184, 0.22))
      drop-shadow(-3px 1px 8px rgba(0, 0, 0, 0.6));
  }
  .sr--dragging .sr__tab {
    cursor: ew-resize;
  }
  .sr__tab:hover {
    color: var(--accent);
    background:
      linear-gradient(
        180deg,
        rgba(126, 245, 184, 0.32) 0%,
        rgba(126, 245, 184, 0.10) 55%,
        rgba(126, 245, 184, 0.00) 100%
      ),
      var(--bg-panel-strong);
    filter:
      drop-shadow(-2px 0 0 var(--accent-dim))
      drop-shadow(-3px 1px 10px rgba(126, 245, 184, 0.32));
  }
  .sr--collapsed .sr__tab:hover {
    filter:
      drop-shadow(-2px 0 0 var(--accent-dim))
      drop-shadow(-3px 1px 12px rgba(126, 245, 184, 0.38));
  }
  .sr__tab:focus-visible {
    outline: none;
    color: var(--accent);
  }

  /* Grip texture — two vertical etched bars, one ABOVE and one
     BELOW the chevron, stacked in the tab's central outer face
     (the 32-px parallel section between the diagonal cuts). On
     hover/drag they grow taller and brighten to acknowledge the
     interaction; same animation contract throughout. */
  .sr__tab-grip {
    width: 1px;
    height: 5px;
    background: currentColor;
    opacity: 0.4;
    box-shadow: 0 0 3px currentColor;
    transition: opacity 200ms ease, height 220ms cubic-bezier(0.16, 1, 0.3, 1);
  }
  .sr__tab:hover .sr__tab-grip,
  .sr--dragging .sr__tab-grip {
    opacity: 1;
    height: 9px;
  }

  .sr__tab-chev {
    width: 8px;
    height: 12px;
    color: currentColor;
    /* Default state is OPEN. The SVG path draws "<"; rotating 180°
       flips it to ">", which signals "click to push the rack
       rightward". When collapsed we revert to "<" to signal "click
       to pull the rack back leftward". */
    transform: rotate(180deg);
    transition: transform 320ms cubic-bezier(0.16, 1, 0.3, 1);
    filter: drop-shadow(0 0 4px transparent);
  }
  .sr--collapsed .sr__tab-chev {
    transform: rotate(0deg);
  }
  .sr__tab:hover .sr__tab-chev {
    filter: drop-shadow(0 0 4px var(--accent-glow));
  }

  /* ----- Body: chassis + scrollable content -----
     The body keeps its full chrome (background, border, shadow,
     scrollbar) in both states. When the rack is collapsed the
     parent aside translates rightward and the body rides with it,
     getting clipped naturally by the viewport's right edge. No
     `display: none` — the slide is a continuous motion. */
  .sr__body {
    flex: 1 1 0;
    min-width: 0;
    min-height: 0;
    background: var(--bg-panel-strong);
    border-left: 1px solid var(--line-accent);
    box-shadow: inset 1px 0 0 rgba(126, 245, 184, 0.05);
    /* `scroll` (not `auto`) forces the scrollbar slot to render even
       when content fits, so the right gutter doesn't pop in and
       out as sections expand/collapse. */
    overflow-y: scroll;
    overflow-x: hidden;
    scrollbar-gutter: stable;
  }

  /* Themed scrollbar — track always visible (etched gutter), thumb
     always present (idle bar that brightens on hover). */
  .sr__body::-webkit-scrollbar {
    width: 10px;
    height: 10px;
  }
  .sr__body::-webkit-scrollbar-track {
    background:
      linear-gradient(
        90deg,
        rgba(126, 245, 184, 0.04) 0%,
        rgba(0, 0, 0, 0.32) 100%
      );
    border-left: 1px solid var(--line);
    box-shadow: inset 1px 0 0 rgba(0, 0, 0, 0.4);
  }
  .sr__body::-webkit-scrollbar-thumb {
    background: rgba(126, 245, 184, 0.22);
    border: 2px solid transparent;
    background-clip: padding-box;
    border-radius: 0;
    min-height: 36px;
    transition: background 200ms ease;
  }
  .sr__body::-webkit-scrollbar-thumb:hover {
    background: rgba(126, 245, 184, 0.48);
    background-clip: padding-box;
  }
  .sr__body::-webkit-scrollbar-thumb:active {
    background: var(--accent);
    background-clip: padding-box;
  }
  .sr__body::-webkit-scrollbar-corner {
    background: rgba(0, 0, 0, 0.32);
  }
</style>
