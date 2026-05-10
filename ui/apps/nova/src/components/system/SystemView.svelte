<script lang="ts">
  // Systems tree: high-level subsystems that don't fit Power / Thermal /
  // Resource / Science. First node is COMMUNICATIONS, with two
  // sub-sections:
  //
  //   COMMAND POD — every Probe / Command part on the vessel, listing
  //                 each probe's StoredCommands ledger (live bytes,
  //                 capacity, refill, decay, net rate).
  //   KSC LINK   — vessel↔KSC connectivity from the per-vessel
  //                 NovaComms topic: link state, signal strength bars,
  //                 SNR in dB, direct edge rate, plus a per-channel
  //                 activity breakdown ("receiving StoredCommands at
  //                 +X B/s") summed from the probe roster.
  //
  // The two sub-sections live at the same indent under COMMUNICATIONS
  // because they describe the same subsystem from different sides
  // (sink vs link). Per the UI hierarchy memory, deeper ledger detail
  // nests under the per-pod row rather than racing alongside it.

  import { useNovaPartsByTag } from '../../telemetry/use-nova-parts-by-tag.svelte';
  import { useComms } from '../../telemetry/use-comms.svelte';
  import { useStageOps } from '@dragonglass/telemetry/svelte';
  import { onDestroy, untrack } from 'svelte';
  import ComponentIcon from '../ComponentIcon.svelte';
  import SegmentGauge from '../SegmentGauge.svelte';
  import { siPrefix, fmtMag, fmtBytes } from '../../util/units';

  interface Props {
    vesselId: string;
  }
  const { vesselId }: Props = $props();

  const RATE_EPSILON = 0.005;
  const isZero = (v: number): boolean => Math.abs(v) < RATE_EPSILON;

  // Subscribe to every command-source-tagged part (Command + Probe)
  // and the per-vessel comms summary in one shot.
  const cmdParts = untrack(() => useNovaPartsByTag(() => vesselId, 'command-source'));
  const comms    = useComms(() => vesselId);

  // Accordion state. The top-level COMMUNICATIONS node defaults open;
  // each Probe ledger row stays collapsed initially because its detail
  // is verbose (capacity / refill / decay / net all on separate lines).
  type NodeKey = 'comms';
  let expanded = $state<Record<NodeKey, boolean>>({ comms: true });
  let probeOpen = $state<Record<string, boolean>>({});
  function toggleNode(k: NodeKey): void {
    expanded[k] = !expanded[k];
  }
  function toggleProbe(key: string): void {
    probeOpen[key] = !(probeOpen[key] ?? false);
  }
  function isProbeOpen(key: string): boolean {
    return probeOpen[key] ?? false;
  }

  // Hover highlight reuses the stage topic — same channel PowerView /
  // Thermal use, so the in-game part lights up under the cursor.
  const stageOps = useStageOps();
  function highlightOn(ids: readonly string[]): void {
    stageOps.setHighlightParts(ids);
  }
  function highlightOff(): void {
    stageOps.setHighlightParts([]);
  }
  onDestroy(() => stageOps.setHighlightParts([]));

  // ── Probe roster ─────────────────────────────────────────────
  // Flatten command-source-tagged parts down to one entry per Probe
  // component. `command-source` also includes crewed Command parts
  // (no command-byte ledger), so filter to parts whose decoded state
  // carries a `probe` entry. Each probe gets a stable key
  // `${partId}:${i}` so multi-probe parts (none today) render
  // independently.
  const probeEntries = $derived.by(() => {
    const out: {
      key: string;
      partId: string;
      partTitle: string;
      bytes: number;
      capacity: number;
      refill: number;
      decay: number;
      sasLevel: number;
    }[] = [];
    for (const p of cmdParts.current) {
      if (!p.state) continue;
      for (let i = 0; i < p.state.probe.length; i++) {
        const pr = p.state.probe[i];
        if (pr.commandCapacityBytes <= 0) continue;
        out.push({
          key:        `${p.struct.id}:${i}`,
          partId:     p.struct.id,
          partTitle:  p.struct.title,
          bytes:      pr.commandBytes,
          capacity:   pr.commandCapacityBytes,
          refill:     pr.commandRefillBps,
          decay:      pr.commandDecayBps,
          sasLevel:   pr.sasLevel,
        });
      }
    }
    return out;
  });

  // Aggregate "currently receiving" rate for the activity readout.
  // Sums every probe's commandRefillBps; today only the primary probe
  // is allocated bandwidth, so the sum equals one probe's rate. Stays
  // correct once multi-probe summation lands.
  const totalCommandRefill = $derived(
    probeEntries.reduce((a, p) => a + p.refill, 0),
  );

  // ── Comms readouts ───────────────────────────────────────────
  // Linear bar count over the achievable/ceiling fraction. Matches the
  // adjacent percentage label so a 10%-rate link reads as "1/5 bars,
  // 10%" rather than the decade-log alternative ("4/5 bars, 10%") that
  // the FlightTopBar uses. The dB SNR readout below carries the
  // technical detail; this gauge is an intuitive at-a-glance.
  function rateBars(rateBps: number, maxRateBps: number): number {
    if (!Number.isFinite(rateBps) || rateBps <= 0) return 0;
    if (!Number.isFinite(maxRateBps) || maxRateBps <= 0) return 0;
    const lit = Math.round(5 * Math.min(1, rateBps / maxRateBps));
    return Math.max(0, Math.min(5, lit));
  }
  function formatSnrDb(snr: number): string {
    if (!Number.isFinite(snr) || snr <= 0) return '—';
    const db = 10 * Math.log10(snr);
    return (db >= 0 ? '+' : '') + db.toFixed(1) + ' dB';
  }
  function fmtRateBps(value: number): string {
    const abs = Math.abs(value);
    const p = siPrefix(abs);
    return `${value >= 0 ? '+' : '−'}${fmtMag(abs / p.div)} ${p.letter}B/s`;
  }
  function fmtPlainRateBps(value: number): string {
    const abs = Math.abs(value);
    const p = siPrefix(abs);
    return `${fmtMag(abs / p.div)} ${p.letter}B/s`;
  }

  const linkUp        = $derived(comms.current?.hasPathToKsc ?? false);
  const directRate    = $derived(comms.current?.directRateBps ?? 0);
  const directMaxRate = $derived(comms.current?.directMaxRateBps ?? 0);
  const bottleneck    = $derived(comms.current?.bottleneckBps ?? 0);
  const directSnr     = $derived(comms.current?.directSnr ?? 0);
  const bars          = $derived(linkUp ? rateBars(directRate, directMaxRate) : 0);
  const linkLabel     = $derived(linkUp ? 'LINKED' : 'DARK');
</script>

<section class="sys">
  <!-- COMMUNICATIONS ──────────────────────────────────────────── -->
  <div class="sys__node">
    <button type="button" class="sys__node-head"
            aria-expanded={expanded.comms}
            onclick={() => toggleNode('comms')}>
      <svg class="sys__chev" class:sys__chev--open={expanded.comms}
           viewBox="0 0 8 8" aria-hidden="true">
        <path d="M2.2 1.4 L5.8 4 L2.2 6.6" fill="none" stroke="currentColor"
              stroke-width="1.25" stroke-linecap="round" stroke-linejoin="round" />
      </svg>
      <span class="sys__node-title">COMMUNICATIONS</span>
      <span class="sys__node-state"
            class:sys__node-state--up={linkUp}
            class:sys__node-state--down={!linkUp}>
        {linkLabel}
      </span>
    </button>

    {#if expanded.comms}
      <!-- Command pods sub-section -->
      <div class="sys__sub">
        <div class="sys__sub-title">COMMAND POD <em>· {probeEntries.length}</em></div>

        {#if probeEntries.length === 0}
          <p class="sys__empty">No probe core on this vessel.</p>
        {:else}
          <ul class="sys__rows">
            {#each probeEntries as e (e.key)}
              {@const fill = e.capacity > 0 ? e.bytes / e.capacity : 0}
              {@const net = e.refill - e.decay}
              {@const open = isProbeOpen(e.key)}
              <li class="sys__row sys__row--probe"
                  onmouseenter={() => highlightOn([e.partId])}
                  onmouseleave={highlightOff}>
                <button type="button" class="sys__row-head"
                        aria-expanded={open}
                        onclick={() => toggleProbe(e.key)}>
                  <svg class="sys__chev" class:sys__chev--open={open}
                       viewBox="0 0 8 8" aria-hidden="true">
                    <path d="M2.2 1.4 L5.8 4 L2.2 6.6" fill="none" stroke="currentColor"
                          stroke-width="1.25" stroke-linecap="round" stroke-linejoin="round" />
                  </svg>
                  <span class="sys__row-icon"><ComponentIcon kind="command" /></span>
                  <span class="sys__row-name">{e.partTitle}</span>
                  <span class="sys__row-gauge"><SegmentGauge fraction={fill} segments={6} /></span>
                  <span class="sys__row-bytes">{fmtBytes(e.bytes)} / {fmtBytes(e.capacity)}</span>
                </button>
                {#if open}
                  <dl class="sys__detail">
                    <dt>Refill</dt>
                    <dd class="sys__rate-pos" class:sys__rate-zero={isZero(e.refill)}>
                      +{fmtPlainRateBps(e.refill)}
                    </dd>
                    <dt>Decay</dt>
                    <dd class="sys__rate-neg" class:sys__rate-zero={isZero(e.decay)}>
                      −{fmtPlainRateBps(e.decay)}
                    </dd>
                    <dt>Net</dt>
                    <dd class:sys__rate-pos={net > RATE_EPSILON}
                        class:sys__rate-neg={net < -RATE_EPSILON}
                        class:sys__rate-zero={isZero(net)}>
                      {fmtRateBps(net)}
                    </dd>
                    <dt>SAS tier</dt>
                    <dd>{e.sasLevel}</dd>
                  </dl>
                {/if}
              </li>
            {/each}
          </ul>
        {/if}

        <div class="sys__sub-title">KSC LINK</div>
        <dl class="sys__kv">
          <dt>Status</dt>
          <dd class:sys__rate-pos={linkUp} class:sys__rate-neg={!linkUp}>
            {linkLabel}
          </dd>

          <dt>Signal</dt>
          <dd class="sys__signal">
            <span class="sys__bars" aria-label={`${bars} of 5 bars`}>
              {#each [0, 1, 2, 3, 4] as i (i)}
                <span class="sys__bar" class:sys__bar--lit={bars > i}></span>
              {/each}
            </span>
            <span class="sys__signal-num">
              {linkUp && directMaxRate > 0
                ? `${Math.round(100 * Math.min(1, directRate / directMaxRate))}%`
                : '—'}
            </span>
          </dd>

          <dt>SNR</dt>
          <dd class="sys__num">{formatSnrDb(directSnr)}</dd>

          <dt>Direct rate</dt>
          <dd class="sys__num">{linkUp ? fmtPlainRateBps(directRate) : '—'}</dd>

          <dt>Bottleneck</dt>
          <dd class="sys__num">{linkUp ? fmtPlainRateBps(bottleneck) : '—'}</dd>
        </dl>

        <div class="sys__activity-title">ACTIVITY</div>
        <ul class="sys__activity">
          <li>
            <span class="sys__activity-arrow" aria-hidden="true">↓</span>
            <span class="sys__activity-name">Stored Commands</span>
            <span class="sys__activity-rate"
                  class:sys__rate-pos={totalCommandRefill > RATE_EPSILON}
                  class:sys__rate-zero={isZero(totalCommandRefill)}>
              {totalCommandRefill > RATE_EPSILON
                ? `+${fmtPlainRateBps(totalCommandRefill)}`
                : '— idle'}
            </span>
          </li>
        </ul>
      </div>
    {/if}
  </div>
</section>

<style>
  .sys {
    display: flex;
    flex-direction: column;
    gap: 6px;
  }

  /* ── Top-level node ──────────────────────────────────────── */
  .sys__node {
    display: flex;
    flex-direction: column;
  }
  .sys__node-head {
    display: flex;
    align-items: center;
    gap: 6px;
    padding: 4px 4px 4px 0;
    background: transparent;
    border: 0;
    border-bottom: 1px solid var(--line);
    color: var(--fg);
    font-family: var(--font-display);
    font-size: 11px;
    letter-spacing: 0.14em;
    cursor: pointer;
    transition: color 160ms ease;
  }
  .sys__node-head:hover { color: var(--accent); }
  .sys__node-title { flex: 1 1 auto; text-align: left; }
  .sys__node-state {
    flex: 0 0 auto;
    font-family: var(--font-display);
    font-size: 10px;
    letter-spacing: 0.14em;
    padding: 1px 6px;
    border: 1px solid var(--line);
  }
  .sys__node-state--up   { color: var(--accent); border-color: var(--accent-dim); }
  .sys__node-state--down { color: var(--warn);   border-color: color-mix(in srgb, var(--warn) 60%, transparent); }

  /* Chevrons mirror PowerView's affordance — flips 90° when open. */
  .sys__chev {
    flex: 0 0 auto;
    width: 8px; height: 8px;
    color: var(--fg-mute);
    transition: transform 160ms ease, color 160ms ease;
  }
  .sys__chev--open { transform: rotate(90deg); color: var(--accent); }

  /* ── Sub-section (Command Pod / KSC Link) ────────────────── */
  .sys__sub {
    padding: 6px 0 8px 14px;
    display: flex;
    flex-direction: column;
    gap: 4px;
  }
  .sys__sub-title {
    margin-top: 4px;
    color: var(--fg-mute);
    font-family: var(--font-display);
    font-size: 10px;
    letter-spacing: 0.16em;
  }
  .sys__sub-title em {
    font-style: normal;
    color: var(--fg-mute);
    opacity: 0.6;
  }
  .sys__empty {
    margin: 2px 0 0;
    color: var(--fg-mute);
    font-style: italic;
  }

  /* ── Probe row ───────────────────────────────────────────── */
  .sys__rows {
    list-style: none;
    margin: 0;
    padding: 0;
    display: flex;
    flex-direction: column;
    gap: 2px;
  }
  .sys__row { display: flex; flex-direction: column; }
  .sys__row-head {
    display: flex;
    align-items: center;
    gap: 6px;
    padding: 3px 0;
    background: transparent;
    border: 0;
    color: var(--fg);
    font: inherit;
    text-align: left;
    cursor: pointer;
  }
  .sys__row-head:hover { color: var(--accent); }
  .sys__row-icon { flex: 0 0 auto; display: inline-flex; align-items: center; }
  .sys__row-name { flex: 1 1 auto; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .sys__row-gauge { flex: 0 0 auto; width: 56px; display: inline-flex; align-items: center; }
  .sys__row-bytes {
    flex: 0 0 auto;
    color: var(--fg-dim);
    font-variant-numeric: tabular-nums;
  }

  /* Detail block under an expanded probe row. Two-column DL: label
     left-aligned in the label gutter, value right-aligned. */
  .sys__detail {
    display: grid;
    grid-template-columns: max-content 1fr;
    column-gap: 12px;
    row-gap: 2px;
    margin: 4px 0 2px 22px;
    padding: 4px 6px;
    border-left: 1px solid var(--line);
    color: var(--fg-dim);
    font-variant-numeric: tabular-nums;
  }
  .sys__detail dt { color: var(--fg-mute); }
  .sys__detail dd { margin: 0; text-align: right; }

  /* ── KSC link key/value table ────────────────────────────── */
  .sys__kv {
    display: grid;
    grid-template-columns: max-content 1fr;
    column-gap: 12px;
    row-gap: 2px;
    margin: 0;
    padding: 2px 0 0;
    color: var(--fg-dim);
  }
  .sys__kv dt { color: var(--fg-mute); }
  .sys__kv dd { margin: 0; text-align: right; font-variant-numeric: tabular-nums; }
  .sys__num { color: var(--fg); }

  .sys__signal { display: inline-flex; align-items: center; gap: 8px; justify-content: flex-end; }
  .sys__bars   { display: inline-flex; gap: 2px; }
  .sys__bar    {
    width: 4px; height: 10px;
    background: rgba(126, 245, 184, 0.10);
    border: 1px solid var(--line);
  }
  .sys__bar--lit {
    background: var(--accent);
    border-color: var(--accent);
    box-shadow: 0 0 4px var(--accent-glow);
  }
  .sys__signal-num { min-width: 32px; text-align: right; }

  /* ── Activity list ───────────────────────────────────────── */
  .sys__activity-title {
    margin-top: 6px;
    color: var(--fg-mute);
    font-family: var(--font-display);
    font-size: 10px;
    letter-spacing: 0.16em;
  }
  .sys__activity {
    list-style: none;
    margin: 2px 0 0;
    padding: 0;
    display: flex;
    flex-direction: column;
    gap: 2px;
  }
  .sys__activity li {
    display: flex;
    align-items: center;
    gap: 8px;
    color: var(--fg-dim);
    font-variant-numeric: tabular-nums;
  }
  .sys__activity-arrow { flex: 0 0 auto; color: var(--accent); }
  .sys__activity-name  { flex: 1 1 auto; }
  .sys__activity-rate  { flex: 0 0 auto; }

  /* Shared sign/zero coloring — mirrors PowerView semantics
     (green = filling/up, amber = draining/down, dim = idle). */
  .sys__rate-pos  { color: var(--accent); }
  .sys__rate-neg  { color: var(--warn); }
  .sys__rate-zero { color: var(--fg-mute); }
</style>
