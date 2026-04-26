<script lang="ts">
  // Discrete N-segment fuel gauge. Color encodes severity:
  //   < 10% alert (red, with a slow pulse on lit segments),
  //   < 30% warn (orange),
  //   otherwise accent (green).
  // Lit segment count rounds the fraction to the nearest 1/N so the
  // viewer reads "about half" rather than scrutinising sub-pixel fills.

  interface Props {
    /** 0..1, clamped. NaN/non-finite renders as empty. */
    fraction: number;
    /** Segment count. Defaults to 10 — the spec's "fuel gauge" feel. */
    segments?: number;
  }
  let { fraction, segments = 10 }: Props = $props();

  const clamped  = $derived(Number.isFinite(fraction) ? Math.max(0, Math.min(1, fraction)) : 0);
  const lit      = $derived(Math.round(clamped * segments));
  const severity = $derived(clamped < 0.10 ? 'alert' : clamped < 0.30 ? 'warn' : 'ok');
</script>

<div
  class="sg sg--{severity}"
  role="meter"
  aria-valuemin={0}
  aria-valuemax={1}
  aria-valuenow={clamped}
  style:--sg-segments={segments}
>
  {#each Array(segments) as _, i (i)}
    <span class="sg__seg" class:sg__seg--lit={i < lit}></span>
  {/each}
</div>

<style>
  /* The gauge body reads as a recessed channel — a thin frame, a
     darkened well behind the cells, and a hairline top-shadow that
     suggests a bezel cut. The cells sit inside as discrete LEDs:
     a light-to-dark vertical gradient and an inner top-highlight on
     each lit cell give them shape against the well. */
  /* Per-resource tinting: callers can set `--sg-color-tint`,
     `--sg-glow-tint`, `--sg-tint-tint` on the gauge wrapper to
     override the OK-state palette without affecting the warn/alert
     fallbacks. The OK class reads them with the accent-green default;
     warn/alert classes ignore them entirely so a low-fill gauge still
     flips to severity tint. */
  .sg {
    display: flex;
    width: 100%;
    min-width: 0;
    align-items: stretch;
    gap: 1.5px;
    height: 10px;
    padding: 1px;
    border: 1px solid var(--line);
    background:
      linear-gradient(180deg, rgba(0, 0, 0, 0.45) 0%, rgba(0, 0, 0, 0.22) 100%);
    box-shadow:
      inset 0 1px 0 rgba(0, 0, 0, 0.55),
      inset 0 -1px 0 rgba(255, 255, 255, 0.015);
    border-radius: 1px;
  }
  .sg--ok {
    --sg-color: var(--sg-color-tint, var(--accent));
    --sg-glow:  var(--sg-glow-tint,  var(--accent-glow));
    --sg-tint:  var(--sg-tint-tint,  rgba(126, 245, 184, 0.05));
  }
  .sg--warn {
    --sg-color: var(--warn);
    --sg-glow:  var(--warn-glow);
    --sg-tint:  rgba(240, 180, 41, 0.06);
  }
  .sg--alert {
    --sg-color: var(--alert);
    --sg-glow:  rgba(255, 82, 82, 0.45);
    --sg-tint:  rgba(255, 82, 82, 0.07);
  }

  .sg__seg {
    flex: 1 1 0;
    min-width: 2px;
    background: rgba(0, 0, 0, 0.35);
    box-shadow: inset 0 0 0 1px var(--sg-tint);
    transition:
      background 220ms cubic-bezier(0.4, 0, 0.2, 1),
      box-shadow 220ms cubic-bezier(0.4, 0, 0.2, 1);
  }
  .sg__seg--lit {
    background:
      linear-gradient(180deg,
        color-mix(in srgb, var(--sg-color) 65%, white 35%) 0%,
        var(--sg-color) 55%,
        color-mix(in srgb, var(--sg-color) 78%, black 22%) 100%);
    box-shadow:
      0 0 5px var(--sg-glow),
      inset 0 0 0 1px rgba(255, 255, 255, 0.10),
      inset 0 1px 0 rgba(255, 255, 255, 0.20);
  }

  /* Critical: lit cells pulse glow at a low frequency. Color stays
     stable — only the glow halo and inner highlight breathe — so the
     reading isn't flickering, just calling for attention. */
  .sg--alert .sg__seg--lit {
    animation: sg-pulse 1.6s ease-in-out infinite;
  }
  @keyframes sg-pulse {
    0%, 100% {
      box-shadow:
        0 0 5px var(--sg-glow),
        inset 0 0 0 1px rgba(255, 255, 255, 0.10),
        inset 0 1px 0 rgba(255, 255, 255, 0.20);
    }
    50% {
      box-shadow:
        0 0 9px var(--sg-glow),
        inset 0 0 0 1px rgba(255, 255, 255, 0.18),
        inset 0 1px 0 rgba(255, 255, 255, 0.28);
    }
  }
</style>
