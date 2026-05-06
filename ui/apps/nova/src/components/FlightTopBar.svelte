<script lang="ts">
  // Top-of-screen status bar: MET, timewarp, orbit, comms.
  //
  // Four fixed-width cells separated by etched verticals. Subscribes to:
  //   • NovaTimewarp (singleton)            — warp rate + mode
  //   • NovaOrbit/<vesselId> (per-vessel)   — apA/peA/period + missionTime
  //   • NovaComms/<vesselId> (per-vessel)   — KSC link + active xmit
  //
  // Renders nothing while flight data hasn't resolved a vessel id;
  // individual sections degrade independently if their per-vessel
  // topic hasn't sent a frame yet. Cell widths are fixed so chips and
  // values appearing/disappearing don't reflow neighbours.

  import { Tween } from 'svelte/motion';
  import { cubicOut } from 'svelte/easing';
  import { useFlightData } from '@dragonglass/telemetry/svelte';
  import { useTimewarp } from '../telemetry/use-timewarp.svelte';
  import { useOrbit } from '../telemetry/use-orbit.svelte';
  import { useComms } from '../telemetry/use-comms.svelte';

  const flight = useFlightData();
  const tw = useTimewarp();
  const orbit = useOrbit(() => flight.vesselId);
  const comms = useComms(() => flight.vesselId);

  // ── Mission clock (T+/T-/T0) ─────────────────────────────────
  function formatMet(missionTime: number): { sign: string; text: string } {
    if (!Number.isFinite(missionTime)) return { sign: 'T+', text: '00:00:00' };
    const sign = missionTime < 0 ? 'T-' : 'T+';
    const t = Math.floor(Math.abs(missionTime));
    const days = Math.floor(t / 86400);
    const hours = Math.floor((t % 86400) / 3600);
    const mins = Math.floor((t % 3600) / 60);
    const secs = t % 60;
    const pad = (n: number) => n.toString().padStart(2, '0');
    const text = days > 0
      ? `${days}d ${pad(hours)}:${pad(mins)}:${pad(secs)}`
      : `${pad(hours)}:${pad(mins)}:${pad(secs)}`;
    return { sign, text };
  }

  // ── Timewarp segmented gauge ─────────────────────────────────
  // KSP has discrete warp rates: rails {1, 5, 10, 50, 100, 1000,
  // 10000, 100000} and physics {1, 2, 3, 4}. Map current rate → an
  // index into a 7-segment bar. Index 0 means realtime (no segments
  // lit); higher indices light progressively. Cap at 7 so the bar
  // length is fixed regardless of mode.
  const RAILS_RATES = [1, 5, 10, 50, 100, 1000, 10000, 100000];
  const PHYS_RATES  = [1, 2, 3, 4];
  function warpIndex(rate: number, mode: 'rails' | 'physics'): number {
    const table = mode === 'rails' ? RAILS_RATES : PHYS_RATES;
    let idx = 0;
    for (let i = 0; i < table.length; i++) {
      if (rate >= table[i] - 0.0001) idx = i;
    }
    return idx;
  }
  function formatWarp(rate: number): string {
    if (rate >= 1) return `×${Math.round(rate).toLocaleString('en-US')}`;
    return `×${rate.toFixed(2).replace(/0+$/, '').replace(/\.$/, '')}`;
  }

  // ── Orbit altitude (km / Mm / Gm with 3 decimals) ───────────
  // Stays in km up to 1 Mm (1000 km), then climbs in 1000× steps.
  // Three decimals everywhere so the readout is unambiguous about
  // scale and never collapses to integer-looking values.
  function formatOrbitAlt(meters: number): { value: string; unit: string } {
    if (!Number.isFinite(meters)) return { value: '—', unit: 'km' };
    const m = Math.abs(meters);
    if (m >= 1e9) return { value: (meters / 1e9).toFixed(3), unit: 'Gm' };
    if (m >= 1e6) return { value: (meters / 1e6).toFixed(3), unit: 'Mm' };
    return { value: (meters / 1e3).toFixed(3), unit: 'km' };
  }

  // ── Orbital period (compact) ─────────────────────────────────
  function formatPeriod(seconds: number): string {
    if (!Number.isFinite(seconds) || seconds <= 0) return '';
    const t = Math.floor(seconds);
    const days = Math.floor(t / 86400);
    const hours = Math.floor((t % 86400) / 3600);
    const mins = Math.floor((t % 3600) / 60);
    const secs = t % 60;
    if (days > 0) return `${days}d ${hours}h`;
    if (hours > 0) return `${hours}h ${mins.toString().padStart(2, '0')}m`;
    return `${mins}:${secs.toString().padStart(2, '0')}`;
  }

  // ── Rate fraction → number of lit bars (0..5) ───────────────
  // Bars track achievable bandwidth as a fraction of the antenna
  // pair's hardware ceiling. Right next to KSC the rate saturates at
  // MaxRate, fraction = 1.0, all 5 bars lit, regardless of whether
  // that's 100 bps or 100 Mbps in the underlying units. Decade steps
  // below saturation: each 10× rate drop loses one bar.
  function rateBars(rateBps: number, maxRateBps: number): number {
    if (!Number.isFinite(rateBps) || rateBps <= 0) return 0;
    if (!Number.isFinite(maxRateBps) || maxRateBps <= 0) return 0;
    const f = rateBps / maxRateBps;
    if (f >= 0.5)    return 5;
    if (f >= 0.05)   return 4;
    if (f >= 0.005)  return 3;
    if (f >= 5e-4)   return 2;
    if (f >= 5e-5)   return 1;
    return 0;
  }

  // SNR in dB. 10·log₁₀(linear). Returns "—" when blocked / no link.
  function formatSnrDb(snr: number): string {
    if (!Number.isFinite(snr) || snr <= 0) return '—';
    const db = 10 * Math.log10(snr);
    return (db >= 0 ? '+' : '') + db.toFixed(1);
  }

  // ── Reactive readouts ────────────────────────────────────────
  const met = $derived(formatMet(orbit.current?.missionTime ?? NaN));
  const orbitReady = $derived(orbit.current !== undefined);
  const apFmt = $derived(formatOrbitAlt(orbit.current?.apA ?? NaN));
  const peFmt = $derived(formatOrbitAlt(orbit.current?.peA ?? NaN));
  const isSubOrbital = $derived(
    orbit.current !== undefined && orbit.current.eccentricity >= 1.0,
  );
  const periodText = $derived(
    isSubOrbital ? '' : formatPeriod(orbit.current?.period ?? 0),
  );
  const incText = $derived(
    orbit.current ? orbit.current.inclination.toFixed(1) + '°' : '',
  );

  const linkUp = $derived(comms.current?.hasPathToKsc ?? false);
  const bars = $derived(
    linkUp
      ? rateBars(comms.current?.directRateBps ?? 0, comms.current?.directMaxRateBps ?? 0)
      : 0,
  );
  const snrDb = $derived(formatSnrDb(comms.current?.directSnr ?? 0));
  const warpIdx = $derived(
    tw.current ? warpIndex(tw.current.rate, tw.current.mode) : 0,
  );
  const warpMode = $derived(tw.current?.mode ?? 'rails');

  // Short client-side tween on the displayed rate. The wire value
  // snaps instantly when the player triggers a warp change (the C#
  // topic publishes target rate, not KSP's animated curr_rate); we
  // re-introduce a 280 ms ease so the digits feel alive rather than
  // popping. The gauge segments still snap with the wire — only the
  // text readout slides.
  const warpTween = new Tween(1, { duration: 280, easing: cubicOut });
  $effect(() => {
    warpTween.target = tw.current?.rate ?? 1;
  });
  const warpDisplayRate = $derived(warpTween.current);
  const warpVisible = $derived(
    Math.abs(warpDisplayRate - 1) > 0.01 || (tw.current?.rate ?? 1) !== 1,
  );
</script>

{#if flight.vesselId}
  <div class="ftb">
    <!-- ── MET + Timewarp gauge ──────────────────────────── -->
    <section class="ftb__cell ftb__cell--clock">
      <div class="ftb__clock-row">
        <span class="ftb__met-sign">{met.sign}</span>
        <span class="ftb__met-text">{met.text}</span>
      </div>
      <div class="ftb__warp-bar" class:ftb__warp-bar--phys={warpMode === 'physics'} title={warpVisible && tw.current ? formatWarp(tw.current.rate) + ' ' + (warpMode === 'rails' ? 'RAILS' : 'PHYS') : '×1 realtime'}>
        {#each [0, 1, 2, 3, 4, 5, 6] as i (i)}
          <span class="ftb__warp-seg" class:ftb__warp-seg--lit={warpIdx > i}></span>
        {/each}
        <span class="ftb__warp-readout">
          {warpVisible ? formatWarp(warpDisplayRate) : '×1'}
        </span>
      </div>
    </section>

    <!-- ── Orbit ─────────────────────────────────────────── -->
    <section class="ftb__cell ftb__cell--orbit" class:ftb__cell--dim={!orbitReady}>
      <span class="ftb__label">
        ORBIT
        <span class="ftb__body">
          {orbit.current?.bodyName ? '· ' + orbit.current.bodyName.toUpperCase() : ''}
        </span>
      </span>
      <div class="ftb__orbit">
        <span class="ftb__orbit-tag">AP</span>
        <span class="ftb__orbit-val">{apFmt.value}</span>
        <span class="ftb__orbit-unit">{apFmt.unit}</span>
        <span class="ftb__orbit-aux ftb__orbit-aux--top" title="Orbital period">
          {periodText ? 'T ' + periodText : ''}
        </span>
        <span class="ftb__orbit-tag">PE</span>
        <span class="ftb__orbit-val">{peFmt.value}</span>
        <span class="ftb__orbit-unit">{peFmt.unit}</span>
        <span class="ftb__orbit-aux" title="Inclination">
          {incText ? 'I ' + incText : ''}
        </span>
      </div>
    </section>

    <!-- ── Link ──────────────────────────────────────────── -->
    <section class="ftb__cell ftb__cell--link">
      <div class="ftb__link-row">
        <span class="ftb__bars" aria-label="Signal strength" title="Signal strength">
          {#each [1, 2, 3, 4, 5] as i (i)}
            <span class="ftb__bar-pip" class:ftb__bar-pip--lit={bars >= i} style="--h: {i * 18}%"></span>
          {/each}
        </span>
        <span
          class="ftb__chip"
          class:ftb__chip--up={linkUp}
          class:ftb__chip--down={!linkUp}
        >
          {linkUp ? 'KSC' : 'DARK'}
        </span>
      </div>
      <div class="ftb__link-stats">
        <span class="ftb__stat">
          <span class="ftb__stat-tag">SNR</span>
          <span class="ftb__stat-val">{snrDb}</span>
          <span class="ftb__stat-unit">dB</span>
        </span>
      </div>
    </section>
  </div>
{/if}

<style>
  /* Fixed-position bar pinned to the top of the viewport. The flight
     hud cluster lives bottom-anchored, so a 56 px top strip never
     overlaps. */
  .ftb {
    position: fixed;
    top: 0;
    left: 50%;
    transform: translateX(-50%);
    display: flex;
    align-items: stretch;
    gap: 0;
    height: 48px;
    background: var(--bg-panel-strong);
    border: 1px solid var(--line-accent);
    border-top: none;
    border-bottom-left-radius: 4px;
    border-bottom-right-radius: 4px;
    box-shadow: 0 0 18px rgba(0, 0, 0, 0.55);
    color: var(--fg);
    font-family: var(--font-mono);
    user-select: none;
    z-index: 50;
    pointer-events: auto;
  }

  /* Fixed widths per cell so chips/values appearing or disappearing
     never reflow neighbours. Widths chosen to fit max-content of each
     cell (e.g., MET supports up to "365d 23:59:59"). */
  .ftb__cell {
    display: flex;
    flex-direction: column;
    justify-content: center;
    gap: 2px;
    padding: 5px 14px;
    box-sizing: border-box;
    flex: 0 0 auto;
    border-left: 1px solid var(--line);
  }
  .ftb__cell:first-child {
    border-left: none;
  }
  .ftb__cell--clock { width: 188px; }
  .ftb__cell--orbit { width: 224px; }
  .ftb__cell--link  { width: 132px; }
  .ftb__cell--dim {
    opacity: 0.55;
  }

  .ftb__label {
    display: inline-flex;
    align-items: center;
    gap: 6px;
    font-family: var(--font-display);
    font-size: 9px;
    letter-spacing: 0.22em;
    color: var(--fg-mute, var(--fg-dim));
    line-height: 1;
    height: 10px;
  }

  .ftb__body {
    font-family: var(--font-display);
    letter-spacing: 0.18em;
    color: var(--accent-dim, var(--fg-dim));
    min-width: 1ch;
  }

  /* ── MET + Warp gauge ────────────────────────────────── */
  /* MET intentionally subdued: smaller text, no accent glow. The
     player's eye should land on orbit/comms first; mission time is
     supporting context, not the headline. */
  .ftb__clock-row {
    display: inline-flex;
    align-items: baseline;
    gap: 4px;
    font-variant-numeric: tabular-nums;
    line-height: 1;
  }
  .ftb__met-sign {
    font-family: var(--font-display);
    font-size: 9px;
    letter-spacing: 0.18em;
    color: var(--accent-dim, var(--fg-dim));
  }
  .ftb__met-text {
    font-size: 13px;
    color: var(--fg-dim);
    font-weight: 400;
  }

  /* Segmented warp gauge — 7 cells + an inline rate readout. Lit
     segments use accent colour for rails warp and warn for physics
     warp so the mode reads at a glance without a chip. */
  .ftb__warp-bar {
    display: grid;
    grid-template-columns: repeat(7, 1fr) auto;
    align-items: center;
    gap: 2px;
    height: 8px;
    margin-top: 1px;
  }
  .ftb__warp-seg {
    height: 100%;
    background: var(--line);
    border: 1px solid transparent;
    transition: background 120ms ease, box-shadow 120ms ease;
  }
  .ftb__warp-seg--lit {
    background: var(--accent);
    box-shadow: 0 0 4px var(--accent-glow);
  }
  .ftb__warp-bar--phys .ftb__warp-seg--lit {
    background: var(--warn, #f0b040);
    box-shadow: 0 0 4px rgba(240, 176, 64, 0.6);
  }
  .ftb__warp-readout {
    margin-left: 6px;
    font-family: var(--font-mono);
    font-size: 10px;
    color: var(--fg-dim);
    font-variant-numeric: tabular-nums;
    min-width: 4ch;
    text-align: right;
  }

  /* Status chips (link only after the warp merge) */
  .ftb__chip {
    display: inline-flex;
    align-items: center;
    padding: 1px 6px;
    border: 1px solid var(--line);
    border-radius: 1px;
    font-family: var(--font-display);
    font-size: 9px;
    letter-spacing: 0.18em;
    line-height: 1.5;
    color: var(--fg-dim);
    background: transparent;
  }
  .ftb__chip--up {
    color: var(--accent);
    border-color: var(--line-accent);
    text-shadow: 0 0 4px var(--accent-glow);
  }
  .ftb__chip--down {
    color: var(--alert, var(--warn, #d05050));
    border-color: var(--alert, var(--warn, #d05050));
  }

  /* ── Orbit ───────────────────────────────────────────── */
  /* Four columns: tag (AP/PE), value (right-aligned for unit
     alignment across rows), unit (fixed width), period (spans both
     rows). The value column is `1fr` so it flexes; right-justify
     keeps the rightmost digit anchored next to the unit. */
  .ftb__orbit {
    display: grid;
    grid-template-columns: 14px 1fr 22px auto;
    grid-template-rows: auto auto;
    align-items: baseline;
    column-gap: 4px;
    row-gap: 0;
    line-height: 1.05;
    font-variant-numeric: tabular-nums;
  }
  .ftb__orbit-tag {
    font-family: var(--font-display);
    font-size: 9px;
    letter-spacing: 0.18em;
    color: var(--accent-dim, var(--fg-dim));
  }
  .ftb__orbit-val {
    font-size: 13px;
    color: var(--fg);
    text-align: right;
    white-space: nowrap;
  }
  .ftb__orbit-unit {
    font-size: 9px;
    color: var(--fg-dim);
    text-align: left;
  }
  /* Period (row 1) and inclination (row 2) share the rightmost
     column. Each cell gets its own left etched border; with row-gap
     0 they touch and read as one continuous divider. */
  .ftb__orbit-aux {
    grid-column: 4;
    align-self: stretch;
    justify-self: end;
    display: inline-flex;
    align-items: center;
    justify-content: flex-end;
    font-family: var(--font-mono);
    font-size: 11px;
    color: var(--fg-dim);
    border-left: 1px solid var(--line);
    padding-left: 10px;
    margin-left: 8px;
    min-width: 64px;
    text-align: right;
  }

  /* ── Link + XMIT ─────────────────────────────────────── */
  .ftb__link-row {
    display: flex;
    align-items: center;
    gap: 8px;
    line-height: 1;
  }
  .ftb__link-stats {
    display: flex;
    align-items: baseline;
    gap: 14px;
    line-height: 1;
  }
  .ftb__stat {
    display: inline-flex;
    align-items: baseline;
    gap: 4px;
    font-variant-numeric: tabular-nums;
  }
  .ftb__stat-tag {
    font-family: var(--font-display);
    font-size: 9px;
    letter-spacing: 0.18em;
    color: var(--accent-dim, var(--fg-dim));
  }
  .ftb__stat-val {
    font-size: 12px;
    color: var(--fg);
  }
  .ftb__stat-unit {
    font-size: 9px;
    color: var(--fg-dim);
    margin-left: 1px;
  }

  /* Five vertical pips, ascending heights — classic phone bars. */
  .ftb__bars {
    display: inline-flex;
    align-items: flex-end;
    gap: 2px;
    height: 14px;
    width: 24px;
  }
  .ftb__bar-pip {
    flex: 1 1 0;
    height: var(--h, 20%);
    background: var(--line);
    border: 1px solid transparent;
    transition: background 120ms ease, box-shadow 120ms ease;
  }
  .ftb__bar-pip--lit {
    background: var(--accent);
    box-shadow: 0 0 4px var(--accent-glow);
  }

</style>
