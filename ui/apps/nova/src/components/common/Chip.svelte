<script lang="ts">
  // Console-chip primitive — the single source of truth for every
  // button/toggle that lives inside the flight sidebar. Three
  // behaviours (action / latch / stepper), four intents (idle / ok /
  // warn / alert), three sizes (sm / md / lg). One geometry, one
  // font. The thin LED stripe on the left edge is the family mark:
  // an idle chip's stripe is invisible; an ok/warn/alert chip's
  // stripe glows in its intent colour so the player can scan a row
  // of chips and tell what's running without reading labels.
  //
  // Behaviour decides aria + render shape; intent decides colour.
  // Callers never write their own border/colour CSS — pick the
  // intent that matches what the chip means. The rule:
  //   ok    — the system is doing what you want (deployed, observing,
  //           cooling). Green LED.
  //   warn  — the system is doing what you asked but it's actively
  //           consuming/burning/draining (reactor running, test load
  //           on, engine in burn). Amber LED.
  //   alert — failure / trip / wait-required. Red LED, gentle pulse.
  //   idle  — off / available / opposite-direction action that isn't
  //           dangerous (RET when extended, START when stopped).
  //
  // `pending` (action sent, topic not yet caught up) renders as a
  // <span> with a dashed border + pulsing stripe so the click reads
  // as registered before state catches up. Stops re-clicks during
  // the in-flight window.

  import type { Snippet } from 'svelte';

  type Kind   = 'action' | 'latch' | 'stepper';
  type Intent = 'idle' | 'ok' | 'warn' | 'alert';
  type Size   = 'sm' | 'md' | 'lg';

  interface Props {
    kind?: Kind;
    intent?: Intent;
    size?: Size;
    /** Latch only — current pressed state. Drives aria-pressed. */
    pressed?: boolean;
    /** Action sent, awaiting confirmation. Renders as span, blocks clicks. */
    pending?: boolean;
    /** Display label. */
    label?: string;
    /** Optional leading glyph (▸ ■ etc). Sized like the label. */
    glyph?: Snippet | string;
    /** Required for a11y. */
    'aria-label'?: string;
    title?: string;
    onclick?: (e: MouseEvent) => void;
    /** Optional override slot — use when label needs markup. */
    children?: Snippet;
    /** Pin a minimum width so adjacent rate columns don't drift as
     *  the label changes between OFF / S1 / S2 etc. */
    minWidth?: string;
  }

  let {
    kind = 'action',
    intent = 'idle',
    size = 'md',
    pressed = false,
    pending = false,
    label,
    glyph,
    'aria-label': ariaLabel,
    title,
    onclick,
    children,
    minWidth,
  }: Props = $props();

  // Latch with pressed=true should default to its on-intent visually
  // when the caller didn't override — but we keep this caller-driven:
  // the caller picks intent based on what `pressed` means in their
  // domain (test-load pressed → warn; cooler pressed → ok).
</script>

{#if pending}
  <span
    class="nv-chip nv-chip--{size} nv-chip--{kind} nv-chip--intent-{intent} nv-chip--pending"
    style:min-width={minWidth}
    role="status"
    aria-label={ariaLabel}
    {title}
  >
    <span class="nv-chip__led" aria-hidden="true"></span>
    {#if glyph}
      <span class="nv-chip__glyph" aria-hidden="true">
        {#if typeof glyph === 'string'}{glyph}{:else}{@render glyph()}{/if}
      </span>
    {/if}
    <span class="nv-chip__label">
      {#if children}{@render children()}{:else}{label}{/if}
    </span>
  </span>
{:else}
  <button
    type="button"
    class="nv-chip nv-chip--{size} nv-chip--{kind} nv-chip--intent-{intent}"
    class:nv-chip--pressed={kind === 'latch' && pressed}
    style:min-width={minWidth}
    aria-label={ariaLabel}
    aria-pressed={kind === 'latch' ? pressed : undefined}
    {title}
    {onclick}
  >
    <span class="nv-chip__led" aria-hidden="true"></span>
    {#if glyph}
      <span class="nv-chip__glyph" aria-hidden="true">
        {#if typeof glyph === 'string'}{glyph}{:else}{@render glyph()}{/if}
      </span>
    {/if}
    <span class="nv-chip__label">
      {#if children}{@render children()}{:else}{label}{/if}
    </span>
  </button>
{/if}

<style>
  /* The console chip. One geometry, one font. State lives on the
     LED stripe (left edge) and the intent-driven border/text colour.
     `appearance:none` defeats the browser's default button chrome
     so the chip is visually identical when rendered as <span>
     (pending state) and <button> (normal). */
  .nv-chip {
    /* Per-intent colour tokens — overridden by intent modifiers. */
    --chip-fg: var(--fg-dim);
    --chip-edge: var(--line);
    --chip-fill: transparent;
    --chip-led: transparent;
    --chip-glow: transparent;

    appearance: none;
    flex: 0 0 auto;
    display: inline-flex;
    align-items: center;
    gap: 4px;
    box-sizing: border-box;
    padding: 0 6px 0 8px;
    background: var(--chip-fill);
    border: 1px solid var(--chip-edge);
    border-radius: 1px;
    color: var(--chip-fg);
    font-family: var(--font-display);
    font-weight: 500;
    letter-spacing: 0.18em;
    text-transform: uppercase;
    line-height: 1;
    text-align: center;
    cursor: pointer;
    user-select: none;
    position: relative;
    transition:
      color 160ms ease,
      border-color 160ms ease,
      background 160ms ease;
  }

  /* The LED stripe — 2px bar inside the left edge, slightly inset
     from the border so it reads as a separate light element rather
     than as a thicker border. In idle intent it's transparent; in
     ok/warn/alert it lights with intent colour + glow. This is the
     family signature: every Nova flight-sidebar chip has this
     stripe, and only this kind of chip does. */
  .nv-chip__led {
    position: absolute;
    left: 2px;
    top: 3px;
    bottom: 3px;
    width: 2px;
    background: var(--chip-led);
    box-shadow: 0 0 4px var(--chip-glow);
    border-radius: 1px;
    transition:
      background 200ms ease,
      box-shadow 200ms ease;
    pointer-events: none;
  }

  .nv-chip__glyph {
    font-family: var(--font-mono);
    font-size: 1.05em;
    line-height: 1;
    flex: 0 0 auto;
  }
  .nv-chip__label {
    flex: 1 1 auto;
    line-height: 1;
  }

  /* ── Sizes ─────────────────────────────────────────────────── */
  .nv-chip--sm {
    height: 16px;
    min-width: 28px;
    padding: 0 5px 0 7px;
    font-size: 8.5px;
  }
  .nv-chip--md {
    height: 20px;
    min-width: 40px;
    font-size: 9px;
  }
  .nv-chip--lg {
    height: 24px;
    min-width: 56px;
    padding: 0 8px 0 10px;
    font-size: 10px;
    gap: 5px;
  }

  /* ── Intents ───────────────────────────────────────────────── */

  /* IDLE — the off / available / opposite-direction-not-dangerous
     state. LED is faint fg-mute so the stripe slot is visible but
     unlit (it reads as "the LED could turn on here"). */
  .nv-chip--intent-idle {
    --chip-fg: var(--fg-dim);
    --chip-edge: var(--line);
    --chip-fill: transparent;
    --chip-led: color-mix(in srgb, var(--fg-mute) 35%, transparent);
    --chip-glow: transparent;
  }
  .nv-chip--intent-idle:hover {
    --chip-fg: var(--fg);
    --chip-edge: var(--line-bright, var(--fg-mute));
    --chip-fill: rgba(255, 255, 255, 0.03);
    --chip-led: var(--fg-mute);
  }

  /* OK — nominal operation. Solar deployed, radiator deployed,
     experiment observing, cooler running. Green stripe lit. */
  .nv-chip--intent-ok {
    --chip-fg: var(--accent);
    --chip-edge: var(--accent-dim);
    --chip-fill: rgba(126, 245, 184, 0.06);
    --chip-led: var(--accent);
    --chip-glow: var(--accent-glow);
  }
  .nv-chip--intent-ok:hover {
    --chip-fg: var(--accent-soft, var(--accent));
    --chip-edge: var(--accent);
    --chip-fill: rgba(126, 245, 184, 0.14);
  }

  /* WARN — intentional consumption. Reactor running, engine burning,
     test-load engaged, retract-in-progress. Amber stripe lit. */
  .nv-chip--intent-warn {
    --chip-fg: var(--warn);
    --chip-edge: color-mix(in srgb, var(--warn) 50%, transparent);
    --chip-fill: rgba(240, 180, 41, 0.06);
    --chip-led: var(--warn);
    --chip-glow: var(--warn-glow);
  }
  .nv-chip--intent-warn:hover {
    --chip-fg: #ffd070;
    --chip-edge: var(--warn);
    --chip-fill: rgba(240, 180, 41, 0.14);
  }

  /* ALERT — failure or trip. Pulses gently — same cadence as the
     existing ion-trip pulse so the alert language stays unified. */
  .nv-chip--intent-alert {
    --chip-fg: var(--alert);
    --chip-edge: var(--alert);
    --chip-fill: rgba(255, 82, 82, 0.08);
    --chip-led: var(--alert);
    --chip-glow: rgba(255, 82, 82, 0.55);
    animation: nv-chip-alert-pulse 1.6s ease-in-out infinite;
  }
  @keyframes nv-chip-alert-pulse {
    0%, 100% { opacity: 1; }
    50%      { opacity: 0.62; }
  }

  /* ── States ────────────────────────────────────────────────── */

  .nv-chip:active:not(:disabled):not(.nv-chip--pending) {
    transform: translateY(1px);
  }
  .nv-chip:focus-visible {
    outline: 1px solid var(--accent);
    outline-offset: 1px;
  }
  .nv-chip:disabled {
    cursor: default;
    opacity: 0.55;
  }

  /* PENDING — click registered, awaiting topic confirmation. Render
     is a <span>, so it can't be re-clicked. Border goes dashed +
     LED pulses to make the in-flight window legible. */
  .nv-chip--pending {
    cursor: default;
    border-style: dashed;
  }
  .nv-chip--pending .nv-chip__led {
    animation: nv-chip-pending-pulse 1.2s ease-in-out infinite;
  }
  @keyframes nv-chip-pending-pulse {
    0%, 100% { opacity: 0.35; }
    50%      { opacity: 1; }
  }
</style>
