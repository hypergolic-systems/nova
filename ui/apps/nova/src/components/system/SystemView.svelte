<script lang="ts">
  // Vessel-level subsystem panel for the SYS tab. Two top-level
  // accordions:
  //
  //   CONTROL         per-probe StoredCommands ledger as a rate-balance
  //                   readout — buffer fill is the focal element, with
  //                   three flow lanes (refill / consume / decay) and a
  //                   signed NET rollup beneath. Lit chevrons in each
  //                   lane communicate magnitude on a decade-log scale
  //                   so a 0.17 B/s decay reads as a thin pulse and a
  //                   50 B/s spend reads as a heavy lane. Built for
  //                   OSR CEF — every value sits on-screen, no tooltip.
  //
  //   COMMUNICATIONS  KSC link state — status lamp + signal bars in a
  //                   strip header, then SNR / direct-rate / bottleneck
  //                   in a tight readout column. Strictly the comms
  //                   graph view; per-probe activity moved to CONTROL.

  import { useNovaPartsByTag } from '../../telemetry/use-nova-parts-by-tag.svelte';
  import { useComms } from '../../telemetry/use-comms.svelte';
  import { useStageOps } from '@dragonglass/telemetry/svelte';
  import { onDestroy, untrack } from 'svelte';
  import ComponentIcon from '../ComponentIcon.svelte';
  import SegmentGauge from '../SegmentGauge.svelte';
  import { siPrefix, fmtMag, fmtBytes } from '../../util/units';

  interface Props { vesselId: string; }
  const { vesselId }: Props = $props();

  const RATE_EPSILON = 0.005;
  const isZero = (v: number): boolean => Math.abs(v) < RATE_EPSILON;

  const cmdParts = untrack(() => useNovaPartsByTag(() => vesselId, 'command-source'));
  const comms    = useComms(() => vesselId);

  type NodeKey = 'control' | 'comms';
  let expanded = $state<Record<NodeKey, boolean>>({ control: true, comms: true });
  function toggleNode(k: NodeKey): void { expanded[k] = !expanded[k]; }

  const stageOps = useStageOps();
  function highlightOn(ids: readonly string[]): void { stageOps.setHighlightParts(ids); }
  function highlightOff(): void { stageOps.setHighlightParts([]); }
  onDestroy(() => stageOps.setHighlightParts([]));

  // ── CONTROL — per-probe ledger ───────────────────────────────
  const probeEntries = $derived.by(() => {
    const out: {
      key: string;
      partId: string;
      partTitle: string;
      bytes: number;
      capacity: number;
      refill: number;
      consume: number;
      decay: number;
    }[] = [];
    for (const p of cmdParts.current) {
      if (!p.state) continue;
      for (let i = 0; i < p.state.probe.length; i++) {
        const pr = p.state.probe[i];
        if (pr.commandCapacityBytes <= 0) continue;
        out.push({
          key:       `${p.struct.id}:${i}`,
          partId:    p.struct.id,
          partTitle: p.struct.title,
          bytes:     pr.commandBytes,
          capacity:  pr.commandCapacityBytes,
          refill:    pr.commandRefillBps,
          consume:   pr.commandConsumeBps,
          decay:     pr.commandDecayBps,
        });
      }
    }
    return out;
  });

  // Vessel-level NET — the at-a-glance "filling vs draining" tell that
  // also drives the accordion-head summary so a collapsed CONTROL still
  // shows the player whether they're in the green.
  const totalNet = $derived(
    probeEntries.reduce((a, p) => a + (p.refill - p.consume - p.decay), 0),
  );

  // Decade-log magnitude → 0..5 lit chevrons. Caps at 5; the numeric
  // readout to the right of the chevron lane carries the full value.
  // The decay tier (0.17 B/s) lands at 1, a 50 B/s spend at 3, a saturated
  // 100 B/s receive at 4 — readable at a glance without needing a legend.
  function flowMagnitude(rate: number): number {
    const abs = Math.abs(rate);
    if (abs < 1e-3) return 0;
    if (abs < 1)    return 1;
    if (abs < 10)   return 2;
    if (abs < 100)  return 3;
    if (abs < 1000) return 4;
    return 5;
  }

  function fmtRateBps(value: number): { mag: string; unit: string } {
    const abs = Math.abs(value);
    const p = siPrefix(abs);
    return { mag: fmtMag(abs / p.div), unit: p.letter + 'B/s' };
  }

  function fmtRateBpsSigned(value: number): { sign: string; mag: string; unit: string } {
    const r = fmtRateBps(value);
    const sign = value > RATE_EPSILON ? '+'
              : value < -RATE_EPSILON ? '−'
              : ' ';
    return { sign, mag: r.mag, unit: r.unit };
  }

  // ── COMMUNICATIONS — link readout ────────────────────────────
  function rateBars(rateBps: number, maxRateBps: number): number {
    if (!Number.isFinite(rateBps) || rateBps <= 0) return 0;
    if (!Number.isFinite(maxRateBps) || maxRateBps <= 0) return 0;
    const lit = Math.round(5 * Math.min(1, rateBps / maxRateBps));
    return Math.max(0, Math.min(5, lit));
  }
  function formatSnrDb(snr: number): string {
    if (!Number.isFinite(snr) || snr <= 0) return '— dB';
    const db = 10 * Math.log10(snr);
    return (db >= 0 ? '+' : '') + db.toFixed(1) + ' dB';
  }

  const linkUp        = $derived(comms.current?.hasPathToKsc ?? false);
  const directRate    = $derived(comms.current?.directRateBps ?? 0);
  const directMaxRate = $derived(comms.current?.directMaxRateBps ?? 0);
  const bottleneck    = $derived(comms.current?.bottleneckBps ?? 0);
  const directSnr     = $derived(comms.current?.directSnr ?? 0);
  const bars          = $derived(linkUp ? rateBars(directRate, directMaxRate) : 0);
  const linkPct       = $derived(linkUp && directMaxRate > 0
      ? Math.round(100 * Math.min(1, directRate / directMaxRate))
      : 0);
</script>

{#snippet chev(open: boolean)}
  <svg class="sys__chev" class:sys__chev--open={open}
       viewBox="0 0 8 8" aria-hidden="true">
    <path d="M2.2 1.4 L5.8 4 L2.2 6.6" fill="none" stroke="currentColor"
          stroke-width="1.25" stroke-linecap="round" stroke-linejoin="round" />
  </svg>
{/snippet}

{#snippet flowLane(label: string, rate: number, kind: 'in' | 'out')}
  {@const mag = flowMagnitude(rate)}
  {@const r = fmtRateBps(rate)}
  {@const idle = isZero(rate)}
  <li class="ctrl__flow ctrl__flow--{kind}"
      class:ctrl__flow--idle={idle}
      class:ctrl__flow--active={!idle}>
    <span class="ctrl__chevs" aria-hidden="true">
      {#each [0, 1, 2, 3, 4] as i (i)}
        <span class="ctrl__chevron"
              class:ctrl__chevron--lit={i < mag}
              style:--ctrl-chev-i={i}>
          {kind === 'in' ? '▶' : '◀'}
        </span>
      {/each}
    </span>
    <span class="ctrl__flow-label">{label}</span>
    <span class="ctrl__flow-rate">
      <span class="ctrl__flow-sign">{kind === 'in' ? '+' : '−'}</span><span
        class="ctrl__flow-mag">{r.mag}</span><em
        class="ctrl__flow-unit">{r.unit}</em>
    </span>
  </li>
{/snippet}

<section class="sys">

  <!-- CONTROL ───────────────────────────────────────────────── -->
  <div class="sys__node">
    <button type="button" class="sys__node-head"
            aria-expanded={expanded.control}
            onclick={() => toggleNode('control')}>
      {@render chev(expanded.control)}
      <span class="sys__node-title">CONTROL</span>
      {#if probeEntries.length === 0}
        <span class="sys__node-summary sys__rate-zero">—</span>
      {:else}
        {@const s = fmtRateBpsSigned(totalNet)}
        <span class="sys__node-summary"
              class:sys__rate-pos={totalNet > RATE_EPSILON}
              class:sys__rate-neg={totalNet < -RATE_EPSILON}
              class:sys__rate-zero={isZero(totalNet)}>
          {s.sign}{s.mag}<em>{s.unit}</em>
        </span>
      {/if}
    </button>

    {#if expanded.control}
      <div class="sys__sub">
        {#if probeEntries.length === 0}
          <p class="sys__empty">No probe core on this vessel.</p>
        {:else}
          {#each probeEntries as e (e.key)}
            {@const fill = e.capacity > 0 ? e.bytes / e.capacity : 0}
            {@const net = e.refill - e.consume - e.decay}
            {@const netFmt = fmtRateBpsSigned(net)}
            {@const atCap = fill >= 0.995}
            {@const netState =
                atCap && net > RATE_EPSILON ? 'MARGIN'
              : net > RATE_EPSILON ? 'FILLING'
              : net < -RATE_EPSILON ? 'DRAINING'
              : 'BALANCED'}
            <div class="ctrl"
                 onmouseenter={() => highlightOn([e.partId])}
                 onmouseleave={highlightOff}
                 role="presentation">

              <!-- Pod identity strip — minimal so the buffer reads as the
                   focal element, but distinct from a bare row. -->
              <header class="ctrl__head">
                <span class="ctrl__icon"><ComponentIcon kind="command" /></span>
                <span class="ctrl__name">{e.partTitle}</span>
                <span class="ctrl__pct"
                      class:ctrl__pct--low={fill < 0.10}
                      class:ctrl__pct--mid={fill >= 0.10 && fill < 0.30}>
                  {(fill * 100).toFixed(0)}%
                </span>
              </header>

              <!-- Buffer gauge: 12-segment for finer-grained read than the
                   PWR-row's 6-segment summary. The bytes line sits below
                   the gauge as paired digits with the slash separator
                   styled muted, so the eye reads "current" first. -->
              <div class="ctrl__buffer">
                <SegmentGauge fraction={fill} segments={12} />
                <div class="ctrl__bytes">
                  <span class="ctrl__bytes-cur">{fmtBytes(e.bytes)}</span>
                  <span class="ctrl__bytes-sep">/</span>
                  <span class="ctrl__bytes-max">{fmtBytes(e.capacity)}</span>
                </div>
              </div>

              <!-- Flow lanes — left chevrons for direction + intensity,
                   centre label, right rate. Inflow is green & rightward;
                   outflows (consume + decay) are amber & leftward. The
                   lit chevrons pulse subtly to suggest live motion. -->
              <ul class="ctrl__flows">
                {@render flowLane('REFILL',  e.refill,  'in')}
                {@render flowLane('CONSUME', e.consume, 'out')}
                {@render flowLane('DECAY',   e.decay,   'out')}
              </ul>

              <!-- NET rollup — large signed digits with a glow on the
                   sign. The single number that answers "is this probe
                   keeping up?". Filled / draining / idle annotations
                   spell out the answer for slow reads. -->
              <div class="ctrl__net"
                   class:ctrl__net--pos={net > RATE_EPSILON && !atCap}
                   class:ctrl__net--margin={atCap && net > RATE_EPSILON}
                   class:ctrl__net--neg={net < -RATE_EPSILON}
                   class:ctrl__net--zero={isZero(net)}>
                <span class="ctrl__net-label">{atCap && net > RATE_EPSILON ? 'MARGIN' : 'NET'}</span>
                <span class="ctrl__net-state">{netState}</span>
                <span class="ctrl__net-rate">
                  <span class="ctrl__net-sign">{netFmt.sign}</span><span
                    class="ctrl__net-mag">{netFmt.mag}</span><em
                    class="ctrl__net-unit">{netFmt.unit}</em>
                </span>
              </div>
            </div>
          {/each}
        {/if}
      </div>
    {/if}
  </div>

  <!-- COMMUNICATIONS ────────────────────────────────────────── -->
  <div class="sys__node">
    <button type="button" class="sys__node-head"
            aria-expanded={expanded.comms}
            onclick={() => toggleNode('comms')}>
      {@render chev(expanded.comms)}
      <span class="sys__node-title">COMMUNICATIONS</span>
      <span class="sys__node-summary"
            class:sys__rate-pos={linkUp}
            class:sys__rate-neg={!linkUp}>
        {linkUp ? 'LINKED' : 'DARK'}
      </span>
    </button>

    {#if expanded.comms}
      <div class="sys__sub">
        <!-- Status strip: lamp + state label + signal bars + percent.
             A single horizontal cluster reads as a transponder face;
             the lamp colour drives the eye, the bars carry magnitude. -->
        <div class="link__status" class:link__status--up={linkUp}>
          <span class="link__lamp" aria-hidden="true"></span>
          <span class="link__state">{linkUp ? 'LINKED' : 'DARK'}</span>
          <span class="link__bars" aria-label={`${bars} of 5 bars`}>
            {#each [0, 1, 2, 3, 4] as i (i)}
              <span class="link__bar" class:link__bar--lit={bars > i}></span>
            {/each}
          </span>
          <span class="link__pct">{linkUp ? `${linkPct}%` : '—'}</span>
        </div>

        <!-- Spec readout. Three tightly-set numerics; tabular nums and
             a fixed label gutter line them up like a printed datasheet. -->
        <dl class="link__readout">
          <dt>SNR</dt>
          <dd>{formatSnrDb(directSnr)}</dd>
          <dt>Direct</dt>
          <dd>{linkUp ? `${fmtRateBps(directRate).mag} ${fmtRateBps(directRate).unit}` : '—'}</dd>
          <dt>Bottleneck</dt>
          <dd>{linkUp ? `${fmtRateBps(bottleneck).mag} ${fmtRateBps(bottleneck).unit}` : '—'}</dd>
        </dl>
      </div>
    {/if}
  </div>
</section>

<style>
  .sys {
    display: flex;
    flex-direction: column;
    gap: 10px;
  }

  /* ── Top-level node + accordion head ─────────────────────── */
  .sys__node { display: flex; flex-direction: column; }
  .sys__node-head {
    display: flex; align-items: center; gap: 6px;
    padding: 4px 4px 5px 0;
    background: transparent; border: 0;
    border-bottom: 1px solid var(--line);
    color: var(--fg);
    font-family: var(--font-display);
    font-size: 11px;
    letter-spacing: 0.18em;
    cursor: pointer;
    transition: color 160ms ease, border-color 160ms ease;
  }
  .sys__node-head:hover { color: var(--accent); }
  .sys__node-title { flex: 1 1 auto; text-align: left; }
  .sys__node-summary {
    flex: 0 0 auto;
    font-family: var(--font-mono);
    font-size: 11px;
    letter-spacing: 0.04em;
    font-variant-numeric: tabular-nums;
    padding: 1px 6px;
    border: 1px solid var(--line);
  }
  .sys__node-summary em {
    font-style: normal;
    color: var(--fg-mute);
    padding-left: 2px;
  }
  .sys__chev {
    flex: 0 0 auto;
    width: 8px; height: 8px;
    color: var(--fg-mute);
    transition: transform 160ms ease, color 160ms ease;
  }
  .sys__chev--open { transform: rotate(90deg); color: var(--accent); }

  .sys__sub {
    padding: 8px 0 0 0;
    display: flex; flex-direction: column; gap: 10px;
  }
  .sys__empty {
    margin: 0;
    color: var(--fg-mute);
    font-style: italic;
  }

  .sys__rate-pos  { color: var(--accent); border-color: var(--accent-dim); }
  .sys__rate-neg  { color: var(--warn);   border-color: color-mix(in srgb, var(--warn) 60%, transparent); }
  .sys__rate-zero { color: var(--fg-mute); }

  /* ── CONTROL — per-probe ledger panel ───────────────────────
     Reads as a single instrument cell: a faint green-tinted top edge
     (suggests the panel is "on"), a darkened well that contains the
     buffer + flow lanes + NET, and a thin baseline. Every internal
     element shares the same horizontal padding so the bar gauge,
     chevron clusters and NET rollup track a shared left/right edge. */
  .ctrl {
    display: flex;
    flex-direction: column;
    gap: 8px;
    padding: 8px 10px 10px;
    border: 1px solid var(--line);
    background:
      linear-gradient(180deg,
        rgba(126, 245, 184, 0.04) 0%,
        rgba(126, 245, 184, 0.00) 22%,
        rgba(0, 0, 0, 0.18) 100%);
    box-shadow: inset 0 1px 0 rgba(126, 245, 184, 0.06);
  }

  /* Pod identity. Title is body, percent is tertiary — the gauge below
     is the actual fill display, this is a backup read. */
  .ctrl__head {
    display: flex;
    align-items: center;
    gap: 6px;
    color: var(--fg);
    font-family: var(--font-mono);
    font-size: 11px;
  }
  .ctrl__icon { flex: 0 0 auto; display: inline-flex; }
  .ctrl__name {
    flex: 1 1 auto;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }
  .ctrl__pct {
    flex: 0 0 auto;
    color: var(--fg-mute);
    font-variant-numeric: tabular-nums;
    letter-spacing: 0.04em;
  }
  .ctrl__pct--mid { color: var(--warn); }
  .ctrl__pct--low {
    color: var(--warn);
    text-shadow: 0 0 4px var(--warn-glow);
  }

  /* Buffer block: gauge + bytes line, no extras. The gauge takes the
     full available width and reads as the section's focal element. */
  .ctrl__buffer { display: flex; flex-direction: column; gap: 4px; }
  .ctrl__bytes {
    display: flex;
    justify-content: flex-end;
    align-items: baseline;
    color: var(--fg-dim);
    font-family: var(--font-mono);
    font-variant-numeric: tabular-nums;
    font-size: 10px;
    letter-spacing: 0.04em;
  }
  .ctrl__bytes-cur { color: var(--fg); }
  .ctrl__bytes-sep { color: var(--fg-mute); padding: 0 4px; }
  .ctrl__bytes-max { color: var(--fg-dim); }

  /* Flow lanes — a 3-column grid keeps the chevron lane, label, and
     rate stacked exactly under each other across all three rows. The
     chevron column is fixed-width so a 1-chevron decay row aligns its
     label with a 4-chevron refill row above it. */
  .ctrl__flows {
    list-style: none;
    margin: 0; padding: 0;
    display: flex;
    flex-direction: column;
    gap: 1px;
  }
  .ctrl__flow {
    display: grid;
    grid-template-columns: 56px max-content 1fr;
    align-items: center;
    column-gap: 10px;
    padding: 2px 0;
    font-family: var(--font-mono);
    font-size: 11px;
  }
  .ctrl__chevs {
    display: inline-flex;
    gap: 1px;
    line-height: 1;
    font-size: 10px;
  }
  /* Inflow chevrons cluster against the buffer end (right);
     outflow chevrons cluster against the source end (left). The
     visual asymmetry communicates direction without needing a label. */
  .ctrl__flow--in  .ctrl__chevs { justify-content: flex-end; }
  .ctrl__flow--out .ctrl__chevs { justify-content: flex-start; }
  .ctrl__chevron {
    color: rgba(255, 255, 255, 0.07);
    transition: color 200ms ease, text-shadow 200ms ease;
  }
  .ctrl__flow--in  .ctrl__chevron--lit {
    color: var(--accent);
    text-shadow: 0 0 4px var(--accent-glow);
    animation: ctrlChevPulse 1.8s ease-in-out infinite;
  }
  .ctrl__flow--out .ctrl__chevron--lit {
    color: var(--warn);
    text-shadow: 0 0 4px var(--warn-glow);
    animation: ctrlChevPulse 2.4s ease-in-out infinite;
  }
  /* Stagger the pulse across chevrons so the lane reads as moving in
     direction — phase advances toward the buffer for inflow, away for
     outflow. var(--ctrl-chev-i) is the index 0..4 set inline. */
  .ctrl__flow--in .ctrl__chevron--lit {
    animation-delay: calc(var(--ctrl-chev-i) * -120ms);
  }
  .ctrl__flow--out .ctrl__chevron--lit {
    animation-delay: calc((4 - var(--ctrl-chev-i)) * -120ms);
  }
  @keyframes ctrlChevPulse {
    0%, 100% { opacity: 1.0; }
    50%      { opacity: 0.55; }
  }
  .ctrl__flow--idle .ctrl__chevron {
    animation: none;
  }

  .ctrl__flow-label {
    color: var(--fg-mute);
    font-family: var(--font-display);
    letter-spacing: 0.18em;
    font-size: 10px;
  }
  .ctrl__flow-rate {
    text-align: right;
    font-variant-numeric: tabular-nums;
  }
  .ctrl__flow-sign {
    display: inline-block;
    width: 0.65em;
    text-align: right;
    color: var(--fg-mute);
    padding-right: 2px;
  }
  .ctrl__flow-mag { letter-spacing: 0.02em; }
  .ctrl__flow-unit {
    font-style: normal;
    color: var(--fg-mute);
    padding-left: 4px;
    font-size: 10px;
  }
  .ctrl__flow--in  .ctrl__flow-rate,
  .ctrl__flow--in  .ctrl__flow-sign { color: var(--accent); }
  .ctrl__flow--out .ctrl__flow-rate,
  .ctrl__flow--out .ctrl__flow-sign { color: var(--warn); }
  .ctrl__flow--idle .ctrl__flow-rate,
  .ctrl__flow--idle .ctrl__flow-sign,
  .ctrl__flow--idle .ctrl__flow-label { color: var(--fg-mute); }

  /* NET rollup: the section's headline. Larger digits, signed glow on
     the leading character, and a state word ("FILLING" / "DRAINING")
     in the gutter so a slow read of the panel still parses. The thick
     accent border at the top separates it visually from the flow lanes. */
  .ctrl__net {
    display: grid;
    grid-template-columns: max-content 1fr max-content;
    align-items: baseline;
    column-gap: 12px;
    margin-top: 2px;
    padding-top: 8px;
    border-top: 1px solid var(--line-accent, var(--line));
    font-family: var(--font-mono);
    font-variant-numeric: tabular-nums;
  }
  .ctrl__net-label {
    color: var(--fg-mute);
    font-family: var(--font-display);
    letter-spacing: 0.20em;
    font-size: 10px;
  }
  .ctrl__net-state {
    color: var(--fg-mute);
    font-family: var(--font-display);
    font-size: 9px;
    letter-spacing: 0.22em;
    text-align: left;
  }
  .ctrl__net-rate {
    text-align: right;
    font-size: 14px;
    letter-spacing: 0.02em;
  }
  .ctrl__net-sign {
    display: inline-block;
    width: 0.7em;
    text-align: right;
    padding-right: 2px;
  }
  .ctrl__net-unit {
    font-style: normal;
    font-size: 10px;
    color: var(--fg-mute);
    padding-left: 4px;
  }
  .ctrl__net--pos .ctrl__net-rate,
  .ctrl__net--pos .ctrl__net-sign { color: var(--accent); }
  .ctrl__net--pos .ctrl__net-sign { text-shadow: 0 0 6px var(--accent-glow); }
  .ctrl__net--pos .ctrl__net-state { color: var(--accent); }
  /* MARGIN — the buffer is full and refill outpaces drain, so the */
  /* extra rate is headroom for spending, not active accumulation. */
  /* Quieter than --pos: same hue family but no glow, so the eye  */
  /* parks on FILLING/DRAINING for transitions and reads MARGIN as  */
  /* a steady-state advisory. */
  .ctrl__net--margin .ctrl__net-rate,
  .ctrl__net--margin .ctrl__net-sign { color: var(--accent); opacity: 0.78; }
  .ctrl__net--margin .ctrl__net-state { color: var(--accent); opacity: 0.78; letter-spacing: 0.18em; }
  .ctrl__net--margin .ctrl__net-label { color: var(--accent); opacity: 0.65; }
  .ctrl__net--neg .ctrl__net-rate,
  .ctrl__net--neg .ctrl__net-sign { color: var(--warn); }
  .ctrl__net--neg .ctrl__net-sign { text-shadow: 0 0 6px var(--warn-glow); }
  .ctrl__net--neg .ctrl__net-state { color: var(--warn); }
  .ctrl__net--zero .ctrl__net-rate,
  .ctrl__net--zero .ctrl__net-sign { color: var(--fg-mute); }

  /* ── COMMUNICATIONS — link readout ────────────────────────── */
  .link__status {
    display: grid;
    grid-template-columns: max-content max-content 1fr max-content;
    align-items: center;
    column-gap: 10px;
    padding: 6px 10px;
    border: 1px solid var(--line);
    background:
      linear-gradient(180deg,
        rgba(255, 255, 255, 0.02) 0%,
        rgba(0, 0, 0, 0.20) 100%);
    box-shadow: inset 0 1px 0 rgba(255, 255, 255, 0.03);
  }
  .link__lamp {
    width: 10px; height: 10px;
    border-radius: 50%;
    background: var(--warn);
    box-shadow: 0 0 6px var(--warn-glow), inset 0 -1px 2px rgba(0, 0, 0, 0.4);
    animation: linkLampPulse 2.0s ease-in-out infinite;
  }
  .link__status--up .link__lamp {
    background: var(--accent);
    box-shadow: 0 0 8px var(--accent-glow), inset 0 -1px 2px rgba(0, 0, 0, 0.4);
    animation: linkLampPulse 3.0s ease-in-out infinite;
  }
  @keyframes linkLampPulse {
    0%, 100% { opacity: 1.0; }
    50%      { opacity: 0.72; }
  }
  .link__state {
    font-family: var(--font-display);
    font-size: 12px;
    letter-spacing: 0.22em;
    color: var(--warn);
  }
  .link__status--up .link__state { color: var(--accent); }
  .link__bars {
    display: inline-flex;
    gap: 2px;
    justify-content: flex-end;
  }
  .link__bar {
    width: 4px; height: 12px;
    background: rgba(126, 245, 184, 0.10);
    border: 1px solid var(--line);
  }
  .link__bar--lit {
    background: var(--accent);
    border-color: var(--accent);
    box-shadow: 0 0 4px var(--accent-glow);
  }
  .link__pct {
    font-family: var(--font-mono);
    font-variant-numeric: tabular-nums;
    color: var(--fg-dim);
    font-size: 11px;
    min-width: 32px;
    text-align: right;
  }

  .link__readout {
    display: grid;
    grid-template-columns: max-content 1fr;
    column-gap: 12px;
    row-gap: 4px;
    margin: 0;
    padding: 0 2px;
  }
  .link__readout dt {
    color: var(--fg-mute);
    font-family: var(--font-display);
    font-size: 10px;
    letter-spacing: 0.18em;
  }
  .link__readout dd {
    margin: 0;
    text-align: right;
    color: var(--fg);
    font-family: var(--font-mono);
    font-variant-numeric: tabular-nums;
    font-size: 11px;
    letter-spacing: 0.04em;
  }
</style>
