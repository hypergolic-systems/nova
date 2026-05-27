<script lang="ts">
  // Vessel-level subsystem panel for the SYS tab. Two top-level
  // accordions:
  //
  //   STORED COMMANDS per-probe ledger as a rate-balance readout —
  //                   buffer fill is the focal element, with three
  //                   flow lanes (receive / consume / decay) and a
  //                   signed NET rollup beneath. Lit chevrons in each
  //                   lane communicate magnitude on a decade-log scale
  //                   so a 0.17 B/s decay reads as a thin pulse and a
  //                   50 B/s spend reads as a heavy lane. Built for
  //                   OSR CEF — every value sits on-screen, no tooltip.
  //
  //                   Zero-sum invariant: at capacity, the displayed
  //                   RECEIVE rate is clamped to what the buffer can
  //                   actually absorb (consume + decay), so the three
  //                   flow lanes always balance NET to zero at cap.
  //                   The raw comms allocation lives on a separate
  //                   AVAILABLE info row (gray, no chevrons) so the
  //                   player can see the spendable headroom without
  //                   it lying about a net inflow the gauge can't
  //                   honour.
  //
  //   COMMUNICATIONS  KSC link state — status lamp + signal bars in a
  //                   strip header, then SNR / direct-rate / bottleneck
  //                   in a tight readout column. Strictly the comms
  //                   graph view; per-probe activity moved to STORED
  //                   COMMANDS.
  //
  //                   Antennas live as a sub-list beneath the link
  //                   readout: one row per installed antenna with a
  //                   one-line stats summary and (for deployable parts)
  //                   an EXT/RET button. Rows use a 4-column grid that
  //                   reserves the control slot for every row — so the
  //                   stats line never reflows when a button toggles
  //                   between EXT and RET, and the fixed-antenna case
  //                   (no button) sits in exactly the same geometry.

  import { useNovaParts } from '../../telemetry/use-nova-parts.svelte';
  import { useComms } from '../../telemetry/use-comms.svelte';
  import { NovaPartTopic } from '../../telemetry/nova-topics';
  import { getKsp, useStageOps } from '@dragonglass/telemetry/svelte';
  import { onDestroy, untrack } from 'svelte';
  import ComponentIcon from '../ComponentIcon.svelte';
  import SegmentGauge from '../SegmentGauge.svelte';
  import Subsection from '../common/Subsection.svelte';
  import { siPrefix, fmtMag, fmtBytes } from '../../util/units';

  interface Props { vesselId: string; }
  const { vesselId }: Props = $props();

  const ksp = getKsp();

  const RATE_EPSILON = 0.005;
  const isZero = (v: number): boolean => Math.abs(v) < RATE_EPSILON;

  const cmdParts = untrack(() => useNovaParts(() => vesselId));
  const comms    = useComms(() => vesselId);

  // Subsection open state — in-memory only, defaults open. The
  // parent (System) accordion already gates whether these render
  // at all, so persisting their nested state would be over-
  // remembering.
  let cmdsOpen   = $state(true);
  let commsOpen  = $state(true);

  const stageOps = useStageOps();
  function highlightOn(ids: readonly string[]): void { stageOps.setHighlightParts(ids); }
  function highlightOff(): void { stageOps.setHighlightParts([]); }
  onDestroy(() => stageOps.setHighlightParts([]));

  // ── STORED COMMANDS — per-probe ledger ───────────────────────
  // `receive` here is the *effective* buffer inflow, not the raw comms
  // allocation: at capacity we clamp it to (consume + decay) so the
  // ledger arithmetic is zero-sum — what comes in is what gets used,
  // matching what the byte counter actually does. The raw allocation
  // is still surfaced as `available` (informational), so the player
  // can see how much comms-driven receive headroom they would gain
  // back as soon as the buffer starts to drain.
  const probeEntries = $derived.by(() => {
    const out: {
      key: string;
      partId: string;
      partTitle: string;
      bytes: number;
      capacity: number;
      receive: number;
      available: number;
      consume: number;
      decay: number;
    }[] = [];
    for (const p of cmdParts.current) {
      if (!p.state) continue;
      for (let i = 0; i < p.state.probe.length; i++) {
        const pr = p.state.probe[i];
        if (pr.commandCapacityBytes <= 0) continue;
        const fill = pr.commandBytes / pr.commandCapacityBytes;
        const atCap = fill >= 0.995;
        // At cap, comms can supply more than the buffer can absorb. The
        // effective inflow equals what's leaving, so net is 0 and the
        // bytes counter holds steady. Below cap, comms supplies its
        // full allocation; the buffer rises by (alloc − consume − decay).
        const drain   = pr.commandConsumeBps + pr.commandDecayBps;
        const receive = atCap ? Math.min(pr.commandRefillBps, drain)
                              : pr.commandRefillBps;
        out.push({
          key:       `${p.struct.id}:${i}`,
          partId:    p.struct.id,
          partTitle: p.struct.title,
          bytes:     pr.commandBytes,
          capacity:  pr.commandCapacityBytes,
          receive,
          available: pr.commandRefillBps,
          consume:   pr.commandConsumeBps,
          decay:     pr.commandDecayBps,
        });
      }
    }
    return out;
  });

  // Vessel-level NET — the at-a-glance "filling vs draining" tell that
  // also drives the accordion-head summary so a collapsed STORED COMMANDS
  // still shows the player whether they're in the green.
  const totalNet = $derived(
    probeEntries.reduce((a, p) => a + (p.receive - p.consume - p.decay), 0),
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

  // ── COMMUNICATIONS — link readout (KSC peer) ─────────────────
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

  // ── ANTENNAS — per-antenna roster ────────────────────────────
  // Read the same parts list the STORED COMMANDS section uses and
  // pluck the antenna components. Each part may host multiple
  // antennas (rare — but the wire supports it), so flatten with a
  // stable key per (partId, index). Fixed antennas are kept in the
  // list per [[feedback_show_hardware_always]] — the player sees the
  // installed hardware, and the row simply omits the deploy control.
  interface AntennaEntry {
    key:           string;
    partId:        string;
    partTitle:     string;
    maxRateBps:    number;
    refDistanceM:  number;
    gain:          number;
    txPowerW:      number;
    isDeployed:    boolean;
    isDeployable:  boolean;
    isRetractable: boolean;
  }
  const antennaEntries = $derived.by<AntennaEntry[]>(() => {
    const out: AntennaEntry[] = [];
    for (const p of cmdParts.current) {
      if (!p.state) continue;
      for (let i = 0; i < p.state.antenna.length; i++) {
        const a = p.state.antenna[i];
        out.push({
          key:           `${p.struct.id}:${i}`,
          partId:        p.struct.id,
          partTitle:     p.struct.title,
          maxRateBps:    a.maxRateBps,
          refDistanceM:  a.refDistanceM,
          gain:          a.gain,
          txPowerW:      a.txPowerW,
          isDeployed:    a.isDeployed,
          isDeployable:  a.isDeployable,
          isRetractable: a.isRetractable,
        });
      }
    }
    return out;
  });

  // How many antennas are currently contributing to the network. The
  // accordion-summary chip surfaces this so a collapsed panel still
  // shows the live count without the player drilling in.
  const antennaActiveCount = $derived(
    antennaEntries.reduce((n, e) => n + (e.isDeployed ? 1 : 0), 0),
  );

  // Distance display: same SI-prefix scaling the bps helper uses, but
  // attached to metres. RA-100 at 1.58 Gm vs an integrated antenna at
  // 1 km share a single column without the tiny ones collapsing to 0.
  function fmtDistanceMeters(value: number): { mag: string; unit: string } {
    const p = siPrefix(value);
    return { mag: fmtMag(value / p.div), unit: p.letter + 'm' };
  }
  // Tx power display: same prefix logic, anchored to watts.
  function fmtPowerW(value: number): { mag: string; unit: string } {
    const p = siPrefix(value);
    return { mag: fmtMag(value / p.div), unit: p.letter + 'W' };
  }

  function setAntennaDeployed(partId: string, deployed: boolean): void {
    ksp.send(NovaPartTopic(partId), 'setAntennaDeployed', deployed);
  }

  // ── COMMUNICATIONS — link readout (first-hop peer) ───────────
  // `link*` fields describe the *first hop* on the chosen path: for
  // direct links the vessel→KSC edge, for relayed links the vessel→
  // relay edge. Bars and SNR therefore reflect the link the player's
  // vessel is actually using, not the geometric direct edge to KSC.
  const linkUp        = $derived(comms.current?.hasPathToKsc ?? false);
  const linkRate      = $derived(comms.current?.linkRateBps ?? 0);
  const linkMaxRate   = $derived(comms.current?.linkMaxRateBps ?? 0);
  const bottleneck    = $derived(comms.current?.bottleneckBps ?? 0);
  const linkSnr       = $derived(comms.current?.linkSnr ?? 0);
  const linkSnrFloor  = $derived(comms.current?.linkSnrFloor ?? 0);
  const peerLabel     = $derived(comms.current?.peerLabel ?? '');
  const bars          = $derived(linkUp ? rateBars(linkRate, linkMaxRate) : 0);
  const linkPct       = $derived(linkUp && linkMaxRate > 0
      ? Math.round(100 * Math.min(1, linkRate / linkMaxRate))
      : 0);
</script>

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

<!-- Reference row: same grid as flowLane but no chevrons, no sign,
     muted styling. Used for AVAILABLE — informational reading of the
     comms-allocated ceiling so the player can see receive headroom
     without it pretending to be an in/out flow. -->
{#snippet infoLane(label: string, rate: number)}
  {@const r = fmtRateBps(rate)}
  <li class="ctrl__flow ctrl__flow--info">
    <span class="ctrl__chevs" aria-hidden="true"></span>
    <span class="ctrl__flow-label">{label}</span>
    <span class="ctrl__flow-rate">
      <span class="ctrl__flow-mag">{r.mag}</span><em
        class="ctrl__flow-unit">{r.unit}</em>
    </span>
  </li>
{/snippet}

<section class="sys">

  <!-- STORED COMMANDS ────────────────────────────────────────── -->
  <Subsection title="Stored Commands" bind:open={cmdsOpen}>
    {#snippet summary()}
      {#if probeEntries.length === 0}
        <span class="sys__rate-zero">—</span>
      {:else}
        {@const s = fmtRateBpsSigned(totalNet)}
        <span class="sys__chip"
              class:sys__rate-pos={totalNet > RATE_EPSILON}
              class:sys__rate-neg={totalNet < -RATE_EPSILON}
              class:sys__rate-zero={isZero(totalNet)}>
          {s.sign}{s.mag}<em>{s.unit}</em>
        </span>
      {/if}
    {/snippet}

    <div class="sys__sub">
      {#if probeEntries.length === 0}
        <p class="sys__empty">No probe core on this vessel.</p>
      {:else}
        {#each probeEntries as e (e.key)}
            {@const fill = e.capacity > 0 ? e.bytes / e.capacity : 0}
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

              <!-- Flow lanes — chevrons (intensity), label, rate. All
                   three lanes anchor their lit chevrons to the same x
                   so a glance picks out the heaviest lane; direction
                   is communicated by colour (green in / amber out) and
                   the ▶/◀ glyph itself. AVAILABLE rides at the top as
                   an info row (no chevrons, gray) — the comms-allocated
                   ceiling, distinct from RECEIVE (effective inflow). -->
              <ul class="ctrl__flows">
                {@render infoLane('AVAILABLE', e.available)}
                {@render flowLane('RECEIVE',   e.receive,   'in')}
                {@render flowLane('CONSUME',   e.consume,   'out')}
                {@render flowLane('DECAY',     e.decay,     'out')}
              </ul>
            </div>
        {/each}
      {/if}
    </div>
  </Subsection>

  <!-- COMMUNICATIONS ────────────────────────────────────────── -->
  <Subsection title="Communications" bind:open={commsOpen}>
    {#snippet summary()}
      <span class="sys__chip"
            class:sys__rate-pos={linkUp}
            class:sys__rate-neg={!linkUp}>
        {linkUp ? 'LINKED' : 'DARK'}
      </span>
    {/snippet}

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

        <!-- Spec readout. PEER sits at the top — "KSC" for direct
             links, "KSC (via NAME)" when the chosen path's first hop
             is a relay vessel. SNR / Noise Floor / Link rate describe
             the FIRST HOP (vessel→peer) — for relayed paths that's
             the link the vessel itself manages, which is what the
             player actually controls. Bottleneck is the rate-limiting
             link across the whole path (may be downstream of the
             first hop on a multi-hop relay chain). -->
        <dl class="link__readout">
          <dt>Peer</dt>
          <dd>{linkUp ? (peerLabel || '—') : '—'}</dd>
          <dt>SNR</dt>
          <dd>{formatSnrDb(linkSnr)}</dd>
          <dt>Noise Floor</dt>
          <dd>{formatSnrDb(linkSnrFloor)}</dd>
          <dt>Link</dt>
          <dd>{linkUp ? `${fmtRateBps(linkRate).mag} ${fmtRateBps(linkRate).unit}` : '—'}</dd>
          <dt>Bottleneck</dt>
          <dd>{linkUp ? `${fmtRateBps(bottleneck).mag} ${fmtRateBps(bottleneck).unit}` : '—'}</dd>
        </dl>

        <!-- Antenna roster. Sub-heading + per-antenna rows. Each row is
             a four-column grid: icon | name+stats stack | status pill |
             control slot. The control slot is fixed-width and always
             rendered — when the antenna is fixed (no deploy mechanism)
             or one-shot-and-locked-open, the slot still occupies its
             46-pixel column with a static glyph so neighbouring rows
             don't shift as deploy state changes. The stats line and
             the control slot live in different grid columns, so the
             stats line's right edge stays put when EXT toggles to RET. -->
        <div class="ant">
          <div class="ant__head">
            <span class="ant__head-title">Antennas</span>
            <span class="ant__head-count"
                  title={`${antennaActiveCount} of ${antennaEntries.length} extended`}>
              {antennaActiveCount}<em class="ant__head-of">/</em>{antennaEntries.length}
            </span>
          </div>

          {#if antennaEntries.length === 0}
            <p class="ant__empty">No antennas on this vessel.</p>
          {:else}
            <ul class="ant__list">
              {#each antennaEntries as a (a.key)}
                {@const rate = fmtRateBps(a.maxRateBps)}
                {@const dist = fmtDistanceMeters(a.refDistanceM)}
                {@const pow  = fmtPowerW(a.txPowerW)}
                {@const statsTitle =
                  `${rate.mag} ${rate.unit} at ${dist.mag} ${dist.unit}` +
                  ` · gain ${fmtMag(a.gain)} · tx ${pow.mag} ${pow.unit}`}
                <li class="ant__row"
                    class:ant__row--retracted={a.isDeployable && !a.isDeployed}
                    onmouseenter={() => highlightOn([a.partId])}
                    onmouseleave={highlightOff}
                    role="presentation">

                  <span class="ant__row-icon">
                    <ComponentIcon kind="antenna" />
                  </span>

                  <div class="ant__row-stack">
                    <span class="ant__row-name" title={a.partTitle}>
                      {a.partTitle}
                    </span>
                    <!-- One-line stat description. The three observables
                         the player actually compares between antennas:
                         max data rate, design distance (knee), gain.
                         Tx power is informational and gets carried as
                         the row's hover title only — keeps the line
                         narrow enough to never wrap inside the SYS
                         panel's 270-ish-pixel content column. Tabular
                         numerals and a non-breaking middle-dot sep
                         hold the column rhythm; units in <em> so the
                         eye lands on magnitudes first. -->
                    <span class="ant__row-stats" title={statsTitle}>
                      <span class="ant__stat">{rate.mag}<em>{rate.unit}</em></span>
                      <span class="ant__stat-sep">·</span>
                      <span class="ant__stat">{dist.mag}<em>{dist.unit}</em></span>
                      <span class="ant__stat-sep">·</span>
                      <span class="ant__stat ant__stat--mute">G&nbsp;{fmtMag(a.gain)}</span>
                    </span>
                  </div>

                  <!-- Status pill. Fixed width — "EXT"/"RET"/"FIX" all
                       occupy the same footprint, so the deploy control
                       to the right anchors to a consistent x-position
                       whichever state the antenna is in. -->
                  {#if !a.isDeployable}
                    <span class="ant__status ant__status--fixed"
                          title="Integrated / non-deployable antenna">FIX</span>
                  {:else if a.isDeployed}
                    <span class="ant__status ant__status--on"
                          title="Extended — contributing to the comms graph">EXT</span>
                  {:else}
                    <span class="ant__status ant__status--off"
                          title="Retracted — antenna inactive">RET</span>
                  {/if}

                  <!-- Deploy control slot. Always rendered, fixed
                       46-px column. Four cases:
                         • non-deployable        → static "—" glyph
                         • deployable, retracted → clickable EXT
                         • deployable+retractable, extended → clickable RET
                         • one-shot, already extended → static "lock" glyph
                       Static cases use the same width as the buttons
                       so the row's right edge never shifts. -->
                  {#if !a.isDeployable}
                    <span class="ant__btn ant__btn--placeholder"
                          aria-hidden="true">—</span>
                  {:else if !a.isDeployed}
                    <button type="button"
                            class="ant__btn ant__btn--ext"
                            aria-label={`Extend ${a.partTitle}`}
                            title="Extend antenna"
                            onclick={(e) => { e.stopPropagation();
                                              setAntennaDeployed(a.partId, true); }}>
                      EXT
                    </button>
                  {:else if a.isRetractable}
                    <button type="button"
                            class="ant__btn ant__btn--ret"
                            aria-label={`Retract ${a.partTitle}`}
                            title="Retract antenna"
                            onclick={(e) => { e.stopPropagation();
                                              setAntennaDeployed(a.partId, false); }}>
                      RET
                    </button>
                  {:else}
                    <span class="ant__btn ant__btn--placeholder"
                          title="One-shot deployable — cannot retract">⌖</span>
                  {/if}
                </li>
              {/each}
            </ul>
          {/if}
        </div>
      </div>
  </Subsection>
</section>

<style>
  .sys {
    display: flex;
    flex-direction: column;
    gap: 6px;
  }

  /* Subsection-head summary chip — a small bordered pill the size
     of the head text, mirroring the AccordionSection's right-edge
     summary vocabulary but at the subordinate register. Reads as
     a status indicator regardless of fold state. */
  .sys__chip {
    display: inline-flex;
    align-items: baseline;
    padding: 1px 5px;
    border: 1px solid var(--line);
    font-family: var(--font-mono);
    font-size: 9.5px;
    letter-spacing: 0.04em;
    font-variant-numeric: tabular-nums;
  }
  .sys__chip em {
    font-style: normal;
    color: var(--fg-mute);
    padding-left: 2px;
  }

  .sys__sub {
    display: flex;
    flex-direction: column;
    gap: 10px;
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
  /* All three lanes left-anchor their chevrons in the 56px column so
     the lit-vs-unlit boundary sits at the same x across rows. Direction
     is read from colour (green in / amber out) and the ▶/◀ glyph; the
     row's lit count communicates magnitude irrespective of side. */
  .ctrl__chevs {
    display: inline-flex;
    justify-content: flex-start;
    gap: 1px;
    line-height: 1;
    font-size: 10px;
  }
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

  /* Info row (AVAILABLE) — no chevrons, no sign, fully muted so it
     reads as a reference value rather than an active flow. Sits in
     the same grid as the flow rows so labels and rates line up. */
  .ctrl__flow--info .ctrl__flow-rate,
  .ctrl__flow--info .ctrl__flow-label { color: var(--fg-mute); }

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

  /* ── ANTENNAS — per-antenna roster ───────────────────────────
     Sits below the KSC-link readout inside the COMMUNICATIONS
     accordion. The sub-heading mirrors the typographic register
     of the .link__readout dt labels (display-face caps, wide
     tracking) without re-using their grid — so the eye reads
     "Antennas" as a new sibling group, not another row in the
     spec table. Vertical rhythm: a small gap separates this
     block from the readout above, and rows themselves carry a
     thin baseline rule for scan-ability. */
  .ant {
    display: flex;
    flex-direction: column;
    gap: 4px;
    margin-top: 4px;
  }
  .ant__head {
    display: flex;
    align-items: baseline;
    justify-content: space-between;
    padding: 0 2px 2px;
    border-bottom: 1px solid var(--line);
  }
  .ant__head-title {
    color: var(--fg-mute);
    font-family: var(--font-display);
    font-size: 10px;
    letter-spacing: 0.18em;
    text-transform: uppercase;
  }
  .ant__head-count {
    color: var(--fg-dim);
    font-family: var(--font-mono);
    font-variant-numeric: tabular-nums;
    font-size: 10px;
    letter-spacing: 0.04em;
  }
  .ant__head-of {
    font-style: normal;
    color: var(--fg-mute);
    padding: 0 1px;
  }
  .ant__empty {
    margin: 0;
    padding: 4px 2px;
    color: var(--fg-mute);
    font-style: italic;
    font-size: 11px;
  }

  .ant__list {
    list-style: none;
    margin: 0;
    padding: 0;
    display: flex;
    flex-direction: column;
  }

  /* The four columns are: icon | name+stats stack | status pill
     | control slot. ALL of these are explicit so the stats line
     can't drift when the control toggles between EXT and RET,
     and so a fixed-antenna row aligns perfectly with a deployable
     one above it. Status pill uses `max-content` so the layout
     stays compact, but its content is fixed-width (3-char pill)
     so it doesn't drift either. Control slot is a hard 46px so
     EXT/RET/—/⌖ all sit in identical real estate. */
  .ant__row {
    display: grid;
    grid-template-columns: 16px minmax(0, 1fr) max-content 46px;
    column-gap: 10px;
    align-items: center;
    padding: 5px 2px;
    border-bottom: 1px solid var(--line-faint, rgba(126, 245, 184, 0.06));
  }
  .ant__row:last-child { border-bottom: 0; }
  .ant__row-icon {
    grid-column: 1;
    color: var(--fg-mute);
    display: inline-flex;
    align-self: start;
    padding-top: 1px;
    transition: color 200ms ease;
  }
  .ant__row--retracted .ant__row-icon { color: var(--fg-dim); }

  /* Name + stats stacked vertically in the second column. min-width:0
     lets the name truncate cleanly when the part title is unusually
     long; the stats line has its own min-width:0 so its inline-flex
     items can shrink without bleeding into the control column. */
  .ant__row-stack {
    grid-column: 2;
    display: flex;
    flex-direction: column;
    gap: 1px;
    min-width: 0;
  }
  .ant__row-name {
    color: var(--fg);
    font-family: var(--font-mono);
    font-size: 11px;
    letter-spacing: 0.02em;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
  }
  .ant__row--retracted .ant__row-name { color: var(--fg-mute); }

  .ant__row-stats {
    display: flex;
    align-items: baseline;
    gap: 5px;
    min-width: 0;
    color: var(--fg-dim);
    font-family: var(--font-mono);
    font-variant-numeric: tabular-nums;
    font-size: 10px;
    letter-spacing: 0.02em;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
  }
  .ant__stat { color: var(--fg); }
  .ant__stat em {
    font-style: normal;
    color: var(--fg-mute);
    padding-left: 1px;
  }
  .ant__stat-sep {
    color: var(--fg-mute);
    opacity: 0.6;
  }
  .ant__stat--mute { color: var(--fg-dim); }

  /* Tri-state status pill. Fixed width (3 caps + a smidge of
     tracking) so EXT / RET / FIX all land in the same envelope —
     the eye sees the color change, not a width change. */
  .ant__status {
    grid-column: 3;
    box-sizing: border-box;
    display: inline-flex;
    align-items: center;
    justify-content: center;
    width: 34px;
    padding: 1px 0;
    border: 1px solid var(--line);
    color: var(--fg-mute);
    font-family: var(--font-display);
    font-size: 9px;
    letter-spacing: 0.14em;
    text-align: center;
    font-variant-numeric: tabular-nums;
  }
  .ant__status--on {
    color: var(--accent);
    border-color: var(--accent-dim);
    background: rgba(126, 245, 184, 0.08);
    box-shadow: 0 0 4px rgba(126, 245, 184, 0.10) inset;
  }
  .ant__status--off {
    color: var(--warn);
    border-color: color-mix(in srgb, var(--warn) 50%, transparent);
    background: color-mix(in srgb, var(--warn) 8%, transparent);
  }
  .ant__status--fixed {
    color: var(--fg-mute);
    border-color: var(--line);
    background: transparent;
  }

  /* Deploy control. ALWAYS occupies the 46px control column,
     whether it's an interactive button or a static placeholder.
     The .ant__btn class drives the geometry; modifier classes
     drive the colour/interactivity. EXT (accent) leans visually
     forward — that's the action the player most often wants on
     a freshly-launched vessel. RET sits in the muted register
     because retracting an extended antenna is the rarer move. */
  .ant__btn {
    grid-column: 4;
    box-sizing: border-box;
    width: 46px;
    height: 22px;
    padding: 0;
    display: inline-flex;
    align-items: center;
    justify-content: center;
    background: transparent;
    border: 1px solid var(--line);
    color: var(--fg-dim);
    font-family: var(--font-display);
    font-size: 10px;
    letter-spacing: 0.18em;
    text-align: center;
    cursor: pointer;
    transition:
      color 160ms ease,
      border-color 160ms ease,
      background 160ms ease,
      box-shadow 160ms ease;
  }
  .ant__btn:hover {
    color: var(--accent);
    border-color: var(--accent-dim);
    background: rgba(126, 245, 184, 0.06);
  }
  .ant__btn:active {
    background: rgba(126, 245, 184, 0.14);
  }
  .ant__btn--ext {
    color: var(--accent);
    border-color: var(--accent-dim);
    box-shadow: inset 0 0 0 1px rgba(126, 245, 184, 0.04);
  }
  .ant__btn--ret {
    color: var(--fg-dim);
    border-color: var(--line);
  }
  .ant__btn--placeholder {
    color: var(--fg-mute);
    border-color: transparent;
    background: transparent;
    cursor: default;
    opacity: 0.55;
    font-family: var(--font-mono);
    font-size: 11px;
    letter-spacing: 0;
  }
  .ant__btn--placeholder:hover {
    color: var(--fg-mute);
    border-color: transparent;
    background: transparent;
  }
</style>
