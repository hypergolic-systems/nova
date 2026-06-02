<script lang="ts">
  // Brand badge for the KSP main menu. A player-facing acknowledgement
  // that Nova is installed. Sits above the KSP wordmark with the
  // ignitor riding the same vertical column as KSP's rocket-icon
  // spire — so the orange bar reads as ignition rising out of the
  // KSP rocket and bursting into the Nova mark. Pointer-events:
  // none, doesn't intercept clicks. Sitting above (rather than
  // below) keeps the badge clear of the menu column, which occupies
  // the band beneath SPACE PROGRAM in both the root and Start Game
  // menu states.
  //
  // The mount animation is the message: a vertical accent bar ignites
  // from bottom to top (hypergolic — instant brilliance), the wordmark
  // resolves under it, and a hairline draws across to close the mark.
  // ~1.5s cascade, then a steady breathing glow.

  import { onMount } from 'svelte';

  let mounted = $state(false);

  onMount(() => {
    requestAnimationFrame(() => { mounted = true; });
  });
</script>

<aside class="nova-badge" class:is-mounted={mounted} aria-label="Nova">
  <span class="ignitor" aria-hidden="true"></span>
  <div class="mark">
    <span class="eyebrow">Hypergolic&nbsp;·&nbsp;Systems</span>
    <span class="wordmark">N<i>O</i>V<i>A</i></span>
    <span class="rule" aria-hidden="true"></span>
  </div>
</aside>

<style>
  /* Anchored above the KSP wordmark, ignitor on the same vertical    */
  /* column as KSP's rocket-icon spire. Going above (not below)       */
  /* keeps the badge clear of the menu items, which occupy the band   */
  /* directly under SPACE PROGRAM in the root menu and the band       */
  /* under "Start Game" in the submenu. The badge's bottom edge       */
  /* sits just above the KSP logo's top so the ignitor visually       */
  /* continues the rocket-icon's vertical thrust line.                */
  .nova-badge {
    position: fixed;
    left: 22vw;
    top: 9.75vh;
    z-index: 9000;
    display: grid;
    grid-template-columns: 0.14em auto;
    column-gap: 1em;
    pointer-events: none;
    user-select: none;
    /* Single scale axis — everything below is em-relative so the whole */
    /* mark grows and shrinks as one with the viewport. Pure vh: the    */
    /* badge tracks the game's render size 1:1, no pixel floor/ceiling. */
    font-size: 2.34vh;
    /* Neon hypergolic orange — the color of an N2O4/UDMH flame at */
    /* ignition. Overrides DG's mint accent locally because this is */
    /* Nova's brand mark, not generic DG chrome. */
    --signal: #ff7a18;
    --signal-soft: #ffb168;
    --signal-glow: rgba(255, 122, 24, 0.5);
    --ink: var(--fg, #e2e8f2);
    --ink-dim: var(--fg-dim, #6b7a93);
    /* Subtle vertical hover — the whole composition feels suspended */
    /* in space, not pasted on. Long, slow, easy to miss; that's the */
    /* point. */
    animation: badge-float 9s ease-in-out infinite 1600ms;
  }

  /* ── ignitor: the vertical accent bar ──
     Starts at zero height, grows bottom→top in 620ms. A brighter
     leading-edge highlight rides the top during ignition, then
     dissolves into a steady glow. Continuous: breathing 8s. */
  .ignitor {
    position: relative;
    align-self: stretch;
    width: 0.14em;
    background: linear-gradient(
      180deg,
      transparent 0%,
      var(--signal) 12%,
      var(--signal) 88%,
      transparent 100%
    );
    box-shadow:
      0 0 0.43em var(--signal-glow),
      0 0 1em var(--signal-glow);
    transform-origin: bottom;
    transform: scaleY(0);
    opacity: 0;
    transition:
      transform 620ms cubic-bezier(0.16, 1, 0.3, 1),
      opacity 220ms ease;
  }
  .is-mounted .ignitor {
    transform: scaleY(1);
    opacity: 1;
    animation: ignitor-breathe 8s ease-in-out infinite 1800ms;
  }
  /* Leading-edge spark — a bright dot that travels with the top of */
  /* the growing bar, peaks at ~70% of the ignition duration. */
  .ignitor::after {
    content: '';
    position: absolute;
    left: 50%;
    top: 0;
    transform: translate(-50%, -50%);
    width: 0.43em;
    height: 0.43em;
    border-radius: 50%;
    background: #ffffff;
    box-shadow:
      0 0 0.71em var(--signal-soft),
      0 0 1.71em var(--signal-glow);
    opacity: 0;
    transition: opacity 180ms ease;
  }
  .is-mounted .ignitor::after {
    animation: spark-trail 620ms cubic-bezier(0.16, 1, 0.3, 1) forwards;
  }

  /* ── mark column ── */
  .mark {
    display: grid;
    gap: 0.36em;
    padding: 0.14em 0;
    align-content: end;
  }

  /* Brand-parent eyebrow. Uses the display face so it reads as part */
  /* of the same family as NOVA, just smaller. Brighter ink than a */
  /* generic dim-gray eyebrow so it carries weight against the menu */
  /* backdrop. */
  .eyebrow {
    font-family: var(--font-display, 'Unica One', 'Azeret Mono', sans-serif);
    font-size: 1em;
    letter-spacing: 0.24em;
    text-transform: uppercase;
    color: var(--ink);
    text-shadow: 0 0 0.43em rgba(0, 0, 0, 0.6);
    opacity: 0;
    transform: translateX(-0.29em);
    transition:
      opacity 320ms ease 240ms,
      transform 320ms ease 240ms;
  }
  .is-mounted .eyebrow { opacity: 1; transform: translateX(0); }

  /* The wordmark. Big display face, very wide tracking — title-card */
  /* energy. Alternating <i> letters carry a slightly different glow */
  /* so each character feels individually lit rather than the whole */
  /* word being uniformly painted. The clip-path mask wipes the word */
  /* on left → right after the ignitor finishes. */
  .wordmark {
    font-family: var(--font-display, 'Unica One', 'Azeret Mono', sans-serif);
    font-size: 2.71em;
    line-height: 0.92;
    letter-spacing: 0.32em;
    color: var(--signal);
    text-shadow:
      0 0 0.21em var(--signal-glow),
      0 0 0.58em var(--signal-glow);
    /* Pad-right to compensate for the trailing letter-spacing's */
    /* phantom column — otherwise the rule looks shifted. */
    padding-right: 0.32em;
    clip-path: inset(0 100% 0 0);
    transition:
      clip-path 700ms cubic-bezier(0.22, 1, 0.36, 1) 420ms,
      text-shadow 400ms ease;
  }
  .wordmark i {
    font-style: normal;
    color: var(--signal-soft);
    text-shadow:
      0 0 0.16em var(--signal-glow),
      0 0 0.47em var(--signal-glow);
  }
  .is-mounted .wordmark {
    clip-path: inset(0 0 0 0);
    animation: wordmark-breathe 8s ease-in-out infinite 1800ms;
  }

  /* Hairline rule under the wordmark. Grows left→right after the */
  /* wordmark settles. Trailing soft-glow gradient on the right edge */
  /* feels like the rule is "trailing" the wipe. */
  .rule {
    display: block;
    height: 0.07em;
    width: 0;
    background: linear-gradient(
      90deg,
      var(--signal) 0%,
      var(--signal-soft) 55%,
      transparent 100%
    );
    box-shadow: 0 0 0.43em var(--signal-glow);
    transition: width 600ms cubic-bezier(0.22, 1, 0.36, 1) 880ms;
  }
  .is-mounted .rule { width: 100%; }

  /* ── keyframes ── */
  @keyframes spark-trail {
    /* The spark rides the top of the bar as scaleY grows. Bar is */
    /* anchored at the bottom and grows up, so the bar's "top" in */
    /* DOM terms stays at top: 0 — but the spark needs to track */
    /* the visible top of the ignited region. Easier: just fade it */
    /* in early, bright at midpoint, fade out as bar completes. */
    0%   { opacity: 0; }
    25%  { opacity: 1; }
    70%  { opacity: 1; }
    100% { opacity: 0; }
  }
  @keyframes ignitor-breathe {
    0%, 100% { box-shadow: 0 0 0.43em var(--signal-glow), 0 0 1em var(--signal-glow); }
    50%      { box-shadow: 0 0 0.29em var(--signal-glow), 0 0 0.64em var(--signal-glow); }
  }
  @keyframes wordmark-breathe {
    0%, 100% {
      text-shadow:
        0 0 0.21em var(--signal-glow),
        0 0 0.58em var(--signal-glow);
    }
    50% {
      text-shadow:
        0 0 0.16em var(--signal-glow),
        0 0 0.42em var(--signal-glow);
    }
  }
  @keyframes badge-float {
    0%, 100% { transform: translateY(0); }
    50%      { transform: translateY(-0.14em); }
  }

  @media (prefers-reduced-motion: reduce) {
    .nova-badge { animation: none; }
    .ignitor, .ignitor::after,
    .eyebrow, .wordmark, .rule {
      transition-duration: 0ms !important;
      animation: none !important;
    }
    .ignitor { transform: scaleY(1); opacity: 1; }
    .wordmark { clip-path: inset(0 0 0 0); }
    .rule { width: 100%; }
  }
</style>
