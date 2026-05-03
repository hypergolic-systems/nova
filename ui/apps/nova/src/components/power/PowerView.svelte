<script lang="ts">
  // Power tree: Generation / Consumption / Storage as collapsible
  // top-level nodes, each with totals on the right. Solar collapses
  // into its own sub-group inside Generation when more than one
  // panel is present. Storage shows an aggregate gauge between its
  // header and its expanded children, and per-battery rows go
  // double-height so the per-cell gauge gets full row width.
  //
  // Expand/collapse state is in-memory: it resets when the panel is
  // remounted (vessel switch, hud reload). Persisting later is a
  // settings concern, not a view concern.

  import { useNovaPartsByTag } from '../../telemetry/use-nova-parts-by-tag.svelte';
  import type { NovaTaggedPart } from '../../telemetry/use-nova-parts-by-tag.svelte';
  import { NovaPartTopic } from '../../telemetry/nova-topics';
  import type { SolarState, CommandState, WheelState } from '../../telemetry/nova-topics';
  import { getKsp, useStageOps } from '@dragonglass/telemetry/svelte';
  import { onDestroy } from 'svelte';
  import ComponentIcon, { type ComponentKind } from '../ComponentIcon.svelte';
  import SegmentGauge from '../SegmentGauge.svelte';
  import { siPrefix, fmtMag } from '../../util/units';

  // Anything magnitudinally below this is "zero" for color purposes.
  // Same threshold fmtRate uses to floor display to "0.00" so the
  // color and the digits agree.
  const RATE_EPSILON = 0.005;
  const isZero = (v: number): boolean => Math.abs(v) < RATE_EPSILON;

  interface Props {
    vesselId: string;
  }
  const { vesselId }: Props = $props();

  const generators = useNovaPartsByTag(() => vesselId, 'power-gen');
  const consumers  = useNovaPartsByTag(() => vesselId, 'power-consume');
  const storage    = useNovaPartsByTag(() => vesselId, 'power-store');

  type NodeKey = 'gen' | 'gen_solar' | 'consume' | 'store';
  let expanded = $state<Record<NodeKey, boolean>>({
    gen: true,
    gen_solar: true,
    consume: true,
    store: true,
  });
  function toggle(k: NodeKey): void {
    expanded[k] = !expanded[k];
  }

  // Per-row expansion in the consumption section. Wheel rows expand
  // into Torque + Buffer sub-detail; other rows aren't expandable.
  let expandedRow = $state<Record<string, boolean>>({});
  function toggleRow(key: string): void {
    expandedRow[key] = !(expandedRow[key] ?? false);
  }
  function isRowExpanded(key: string): boolean {
    return expandedRow[key] ?? false;
  }

  function generationRate(p: NovaTaggedPart): number {
    if (!p.state) return 0;
    let total = 0;
    for (const s of p.state.solar) total += s.rate;
    for (const fc of p.state.fuelCell) total += fc.currentOutput;
    return total;
  }

  // Per-cell sums for the fuel-cell row. A part should only ever
  // hold one fuel cell, but iterate to stay symmetric with the rest
  // of the helpers.
  function fuelCellOutput(p: NovaTaggedPart): { current: number; max: number } {
    let current = 0;
    let max = 0;
    if (p.state) {
      for (const fc of p.state.fuelCell) {
        current += fc.currentOutput;
        max += fc.maxOutput;
      }
    }
    return { current, max };
  }

  // Solar current+max in one pass — every solar component on a part
  // contributes both. Drives the per-panel and subgroup current/max
  // readout (e.g. "0.50/1.20 kW").
  function solarRates(p: NovaTaggedPart): { current: number; max: number } {
    let current = 0;
    let max = 0;
    if (p.state) {
      for (const s of p.state.solar) {
        current += s.rate;
        max += s.maxRate;
      }
    }
    return { current, max };
  }

  function setCommandTestLoad(partId: string, active: boolean): void {
    ksp.send(NovaPartTopic(partId), 'setCommandTestLoad', active);
  }

  // Per-component consumer row. Each Command / ReactionWheel / Light
  // on a part becomes its own row at the top consumption level —
  // the bus-facing rate is what the row reports as its "draw", so
  // a wheel that's idling on a full buffer reads `0 W` (no bus
  // inflow), even when the motor is being driven from buffered
  // energy. The wheel's expandable detail lines surface that
  // separately so the energy balance stays visible.
  type ConsumerRowKind = 'command' | 'wheel' | 'light';
  interface ConsumerRow {
    key: string;
    partId: string;
    partTitle: string;
    kind: ConsumerRowKind;
    /** Bus-facing W. For Command this is idleRate + testLoadRate; for
     *  Wheel it is the refill device's busRate (motor draw is shown
     *  in the wheel-detail Torque sub-row, not here); for Light it's
     *  light.rate. */
    busRate: number;
    command?: CommandState;
    wheel?: WheelState;
  }

  function buildConsumerRows(p: NovaTaggedPart): ConsumerRow[] {
    const rows: ConsumerRow[] = [];
    if (!p.state) return rows;
    const partId = p.struct.id;
    const partTitle = p.struct.title;
    for (let i = 0; i < p.state.command.length; i++) {
      const c = p.state.command[i];
      // Don't filter on the live rate — `idleRate` is the post-LP
      // throttle (idleDraw × idleActivity), so a transiently starved
      // bus would otherwise make the row vanish entirely. The C#
      // side already gates emission for parts without a configured
      // IdleDraw / TestLoadRate, so any frame that arrives here
      // represents a real flight-computer load worth displaying.
      rows.push({
        key: `${partId}:c${i}`, partId, partTitle,
        kind: 'command',
        busRate: c.idleRate + c.testLoadRate,
        command: c,
      });
    }
    for (let i = 0; i < p.state.wheel.length; i++) {
      const w = p.state.wheel[i];
      rows.push({
        key: `${partId}:w${i}`, partId, partTitle,
        kind: 'wheel',
        busRate: w.busRate,
        wheel: w,
      });
    }
    for (let i = 0; i < p.state.light.length; i++) {
      const l = p.state.light[i];
      rows.push({
        key: `${partId}:l${i}`, partId, partTitle,
        kind: 'light',
        busRate: l.rate,
      });
    }
    return rows;
  }

  function rowKindIcon(kind: ConsumerRowKind): ComponentKind {
    if (kind === 'command') return 'command';
    if (kind === 'wheel') return 'wheel';
    return 'light';
  }

  const consumerRows = $derived.by((): ConsumerRow[] =>
    consumers.current.flatMap(buildConsumerRows));

  // Update the section total to reflect each component's bus-facing
  // rate (matches the per-row totals, so summing rows = consumption
  // total). Wheel rows now contribute `busRate` instead of motor
  // power.
  function consumptionRate(p: NovaTaggedPart): number {
    if (!p.state) return 0;
    let total = 0;
    for (const w of p.state.wheel) total += w.busRate;
    for (const l of p.state.light) total += l.rate;
    for (const c of p.state.command) total += c.idleRate + c.testLoadRate;
    return total;
  }

  function batteryStored(p: NovaTaggedPart): number {
    if (!p.state) return 0;
    let total = 0;
    for (const b of p.state.battery) total += b.soc * b.capacity;
    return total;
  }

  function batteryCapacity(p: NovaTaggedPart): number {
    if (!p.state) return 0;
    let total = 0;
    for (const b of p.state.battery) total += b.capacity;
    return total;
  }

  function batteryRate(p: NovaTaggedPart): number {
    if (!p.state) return 0;
    let total = 0;
    for (const b of p.state.battery) total += b.rate;
    return total;
  }

  // Per-row icon choice. State-driven when loaded; falls back to the
  // section's dominant kind so first-frame rows aren't iconless.
  function genKind(p: NovaTaggedPart): ComponentKind {
    if (p.state && p.state.fuelCell.length > 0) return 'fuelCell';
    return 'solar';
  }
  function isSolarPart(p: NovaTaggedPart): boolean {
    return !!p.state && p.state.solar.length > 0;
  }
  function isFuelCellPart(p: NovaTaggedPart): boolean {
    return !!p.state && p.state.fuelCell.length > 0;
  }

  const genGroups = $derived.by(() => {
    const solar: NovaTaggedPart[] = [];
    const other: NovaTaggedPart[] = [];
    for (const p of generators.current) {
      (isSolarPart(p) ? solar : other).push(p);
    }
    const groupSolar = solar.length > 1;
    let solarCurrent = 0;
    let solarMax = 0;
    for (const p of solar) {
      const r = solarRates(p);
      solarCurrent += r.current;
      solarMax += r.max;
    }
    return {
      solar,
      other,
      groupSolar,
      // When solar isn't grouped, fold it back into the inline list
      // so render order matches the original (solar parts first).
      inline: groupSolar ? other : [...solar, ...other],
      solarTotal: solarCurrent,
      solarMaxTotal: solarMax,
    };
  });

  const totals = $derived({
    gen: generators.current.reduce((a, p) => a + generationRate(p), 0),
    consume: consumers.current.reduce((a, p) => a + consumptionRate(p), 0),
    stored: storage.current.reduce((a, p) => a + batteryStored(p), 0),
    capacity: storage.current.reduce((a, p) => a + batteryCapacity(p), 0),
    netStorage: storage.current.reduce((a, p) => a + batteryRate(p), 0),
  });

  // Pre-formatted node-head totals. `{@const}` is only valid inside
  // flow blocks, not inside `<button>` / `<li>` markup, so the head
  // rows' format work has to land here instead of next to the spans
  // it feeds.
  const genFmt          = $derived(fmtRate(totals.gen));
  const consumeFmt      = $derived(fmtRate(totals.consume));
  const storedFmt       = $derived(fmtCapPair(totals.stored, totals.capacity));
  const netStorageFmt   = $derived(fmtRate(totals.netStorage));
  const solarSubgroupFmt = $derived(
    fmtRatePair(genGroups.solarTotal, genGroups.solarMaxTotal),
  );

  // Unsigned flow-rate readout. Sign is conveyed entirely by colour
  // (green = power flowing OUT / provider; amber = power flowing IN /
  // consumer); the magnitude is always shown without a leading `-`.
  // Unit is the SI-prefixed Watt — energy flow rate, J/s.
  function fmtRate(value: number): { mag: string; unit: string } {
    const abs = Math.abs(value);
    const p = siPrefix(abs);
    return { mag: fmtMag(abs / p.div), unit: p.letter + 'W' };
  }

  // Pair scaling for current/max: both share the prefix selected from
  // the larger absolute value, so the divisor and dividend share units
  // ("0.00/1.50 kW" rather than "0 W/1.50 kW").
  function fmtRatePair(current: number, max: number): { cMag: string; mMag: string; unit: string } {
    const p = siPrefix(Math.max(Math.abs(current), Math.abs(max)));
    return {
      cMag: fmtMag(current / p.div),
      mMag: fmtMag(max / p.div),
      unit: p.letter + 'W',
    };
  }

  // Stored / capacity pair, in SI-prefixed Joules. Same shared-prefix
  // rule as the rate pair so the slash sits between commensurable values.
  function fmtCapPair(stored: number, capacity: number): { sMag: string; cMag: string; unit: string } {
    const p = siPrefix(Math.max(Math.abs(stored), Math.abs(capacity)));
    return {
      sMag: fmtMag(stored / p.div),
      cMag: fmtMag(capacity / p.div),
      unit: p.letter + 'J',
    };
  }

  function batteryFraction(stored: number, capacity: number): number {
    return capacity > 0 ? stored / capacity : 0;
  }

  // Hover highlight — reuses Dragonglass's StageTopic.setHighlightParts
  // (same channel the stock staging stack uses) so the 3-D part lights
  // up in-game when the cursor lands on its row. Clear on leave; clear
  // on unmount so the highlight doesn't ghost on after the panel
  // detaches.
  const stageOps = useStageOps();
  function highlightOn(ids: readonly string[]): void {
    stageOps.setHighlightParts(ids);
  }
  function highlightOff(): void {
    stageOps.setHighlightParts([]);
  }
  onDestroy(() => stageOps.setHighlightParts([]));

  // Solar deploy controls. Per-part NovaPartTopic exposes
  // setSolarDeployed(bool) — single-panel only on the mod side, so
  // the bulk subgroup button has to fan out one op per panel. The
  // pending map masks the click→animation-finish window: clicking
  // sets a per-part deadline, the button reads as "moving", and the
  // entry clears either when the topic state matches the requested
  // direction or 4 s after the click (animation slack + a beat).
  const ksp = getKsp();
  const PENDING_TIMEOUT_MS = 4000;

  function solarOf(p: NovaTaggedPart): SolarState | undefined {
    return p.state?.solar?.[0];
  }

  let pending = $state<Record<string, { target: boolean; deadline: number }>>({});
  let now = $state(performance.now());
  // Drive the pending-expiry recompute. Cheap — only runs while an
  // entry is live, and the visible state cuts over within ~120 ms.
  $effect(() => {
    if (Object.keys(pending).length === 0) return;
    const id = window.setInterval(() => (now = performance.now()), 120);
    return () => window.clearInterval(id);
  });

  function isPending(p: NovaTaggedPart): boolean {
    const e = pending[p.struct.id];
    if (!e) return false;
    if (now >= e.deadline) return false;
    const s = solarOf(p);
    // Topic confirmed the requested state — clear the pending mark.
    if (s && s.deployed === e.target) return false;
    return true;
  }

  function setSolarDeployed(partId: string, deployed: boolean): void {
    ksp.send(NovaPartTopic(partId), 'setSolarDeployed', deployed);
    pending[partId] = { target: deployed, deadline: performance.now() + PENDING_TIMEOUT_MS };
  }

  // Bulk action across the SOLAR subgroup. Fans out one op per panel
  // matching the source state — extend retracts, retract deployeds.
  function bulkSetSolarDeployed(parts: NovaTaggedPart[], deployed: boolean): void {
    for (const p of parts) {
      const s = solarOf(p);
      if (!s) continue;
      if (s.deployed === deployed) continue;
      if (!deployed && !s.retractable) continue;
      setSolarDeployed(p.struct.id, deployed);
    }
  }

  // Subgroup-level state — drives the bulk button label and which of
  // EXT-ALL / RET-ALL is offered.
  function subgroupAnyRetracted(parts: NovaTaggedPart[]): boolean {
    return parts.some((p) => { const s = solarOf(p); return !!s && !s.deployed; });
  }
  function subgroupAnyExtendedRetractable(parts: NovaTaggedPart[]): boolean {
    return parts.some((p) => {
      const s = solarOf(p);
      return !!s && s.deployed && s.retractable;
    });
  }
</script>

{#snippet chev(open: boolean)}
  <svg class="pwr__chev" class:pwr__chev--open={open}
       viewBox="0 0 8 8" aria-hidden="true">
    <path d="M2.2 1.4 L5.8 4 L2.2 6.6" fill="none" stroke="currentColor"
          stroke-width="1.25" stroke-linecap="round" stroke-linejoin="round" />
  </svg>
{/snippet}

{#snippet emptyMsg(text: string)}
  <p class="pwr__empty">
    <span class="pwr__empty-rule"></span>
    <span class="pwr__empty-text">{text}</span>
    <span class="pwr__empty-rule"></span>
  </p>
{/snippet}

<!-- Flat consumer row: Command / Light. The whole row is the part title
     with an icon and a single rate readout. Command also carries the
     LOAD test-load toggle when configured. -->
{#snippet consumerFlatRow(row: ConsumerRow)}
  {@const r = fmtRate(row.busRate)}
  <li class="pwr__row"
      onmouseenter={() => highlightOn([row.partId])}
      onmouseleave={highlightOff}>
    <span class="pwr__row-chev-spacer" aria-hidden="true"></span>
    <span class="pwr__row-icon">
      <ComponentIcon kind={rowKindIcon(row.kind)} />
    </span>
    <span class="pwr__row-name">{row.partTitle}</span>
    {#if row.kind === 'command' && row.command && row.command.testLoadRate > 0}
      <button type="button"
              class="pwr__deploy-btn pwr__test-load-btn"
              class:pwr__test-load-btn--on={row.command.testLoadActive}
              aria-label={row.command.testLoadActive ? 'Disable test load' : 'Enable test load'}
              title={`Toggle ${row.command.testLoadRate} W debug load`}
              onclick={(e) => { e.stopPropagation(); setCommandTestLoad(row.partId, !row.command!.testLoadActive); }}>
        <span>LOAD</span>
      </button>
    {/if}
    <span class="pwr__row-rate"
          class:pwr__row-rate--neg={!isZero(row.busRate)}
          class:pwr__row-rate--zero={isZero(row.busRate)}>
      {r.mag}<em>{r.unit}</em>
    </span>
  </li>
{/snippet}

<!-- Reaction-wheel row. The collapsed head shows the bus-facing draw
     (refill device's actual delivery) — `0 W` when refill is off,
     positive when on. Expanding splits the wheel's energy balance
     into two sub-lines:
       Torque  — instantaneous W into the motor (sourced from buffer
                 + bus combined). Always non-negative; amber.
       Buffer  — signed W in/out of the energy buffer (= busRate −
                 motorRate). Positive when filling (buffer consuming
                 bus power, amber); negative when draining (buffer
                 providing motor power, accent green).
     The buffer sub-line includes the fill gauge. The energy balance
     `motorRate + bufferRate = busRate` always holds. -->
{#snippet wheelRow(row: ConsumerRow, w: WheelState)}
  {@const isOpen = isRowExpanded(row.key)}
  {@const busFmt = fmtRate(row.busRate)}
  {@const motorFmt = fmtRate(w.motorRate)}
  {@const bufferRate = w.busRate - w.motorRate}
  {@const bufferFmt = fmtRate(bufferRate)}
  <li class="pwr__row pwr__row--wheel"
      class:pwr__row--wheel-open={isOpen}
      onmouseenter={() => highlightOn([row.partId])}
      onmouseleave={highlightOff}>
    <button type="button" class="pwr__wheel-head"
            aria-expanded={isOpen}
            onclick={() => toggleRow(row.key)}>
      {@render chev(isOpen)}
      <span class="pwr__row-icon">
        <ComponentIcon kind="wheel" />
      </span>
      <span class="pwr__row-name">{row.partTitle}</span>
      <span class="pwr__row-rate"
            class:pwr__row-rate--neg={!isZero(row.busRate)}
            class:pwr__row-rate--zero={isZero(row.busRate)}>
        {busFmt.mag}<em>{busFmt.unit}</em>
      </span>
    </button>
    {#if isOpen}
      <div class="pwr__wheel-detail">
        <div class="pwr__wheel-detail-line">
          <span class="pwr__wheel-detail-label">Torque</span>
          <span class="pwr__row-rate"
                class:pwr__row-rate--neg={!isZero(w.motorRate)}
                class:pwr__row-rate--zero={isZero(w.motorRate)}>
            {motorFmt.mag}<em>{motorFmt.unit}</em>
          </span>
        </div>
        <div class="pwr__wheel-detail-line pwr__wheel-detail-line--gauge"
             class:pwr__wheel-gauge--refilling={w.refillActive}
             title={w.refillActive
               ? 'Buffer refilling from bus'
               : 'Buffer (drains during use, refills below 10%)'}>
          <SegmentGauge fraction={w.bufferFraction} />
          <span class="pwr__row-rate"
                class:pwr__row-rate--neg={bufferRate > RATE_EPSILON}
                class:pwr__row-rate--zero={isZero(bufferRate)}>
            {bufferFmt.mag}<em>{bufferFmt.unit}</em>
          </span>
        </div>
      </div>
    {/if}
  </li>
{/snippet}

<!-- Per-row solar deploy control. Renders nothing until the part's
     solar state lands. Once it does:
       deployed=false              → "EXT" button (extend this panel)
       deployed=true, retractable  → "RET" button (retract this panel)
       deployed=true, !retractable → no control (locked open)
     A pending click — between the op send and the animation-end
     state confirmation — renders as a dimmed "..." placeholder so
     the user gets immediate feedback even though the topic state
     hasn't flipped yet.
     Click stops propagation so the row's hover-highlight handlers
     stay coherent. -->
{#snippet solarControl(p: NovaTaggedPart)}
  {@const s = solarOf(p)}
  {#if s}
    {@const busy = isPending(p)}
    {#if busy}
      <span class="pwr__deploy-btn pwr__deploy-btn--busy"
            aria-label="Solar panel moving">
        <span>···</span>
      </span>
    {:else if !s.deployed}
      <button type="button" class="pwr__deploy-btn pwr__deploy-btn--ext"
              aria-label="Extend solar panel"
              title="Extend solar panel"
              onclick={(e) => { e.stopPropagation(); setSolarDeployed(p.struct.id, true); }}>
        <span>EXT</span>
      </button>
    {:else if s.deployed && s.retractable}
      <button type="button" class="pwr__deploy-btn pwr__deploy-btn--ret"
              aria-label="Retract solar panel"
              title="Retract solar panel"
              onclick={(e) => { e.stopPropagation(); setSolarDeployed(p.struct.id, false); }}>
        <span>RET</span>
      </button>
    {/if}
  {/if}
{/snippet}

<!-- Subgroup-level bulk control. Shown when more than one panel is
     present (the SOLAR subgroup is the only render path for this
     snippet). Offers EXT-ALL when at least one panel is retracted,
     otherwise RET-ALL when at least one panel is extended-and-
     retractable. Mixed states (some retracted, some extended) get
     EXT-ALL — finishing the deploy first matches the typical "I
     need power, deploy everything" intent. -->
{#snippet solarBulkControl(parts: NovaTaggedPart[])}
  {#if subgroupAnyRetracted(parts)}
    <button type="button" class="pwr__deploy-btn pwr__deploy-btn--ext pwr__deploy-btn--bulk"
            aria-label="Extend all retracted solar panels"
            title="Extend all retracted solar panels"
            onclick={(e) => { e.stopPropagation(); bulkSetSolarDeployed(parts, true); }}>
      <span>EXT ALL</span>
    </button>
  {:else if subgroupAnyExtendedRetractable(parts)}
    <button type="button" class="pwr__deploy-btn pwr__deploy-btn--ret pwr__deploy-btn--bulk"
            aria-label="Retract all retractable solar panels"
            title="Retract all retractable solar panels"
            onclick={(e) => { e.stopPropagation(); bulkSetSolarDeployed(parts, false); }}>
      <span>RET ALL</span>
    </button>
  {/if}
{/snippet}

<section class="pwr">
  <!-- Generation -------------------------------------------------- -->
  <div class="pwr__node">
    <button type="button" class="pwr__node-head"
            aria-expanded={expanded.gen}
            onclick={() => toggle('gen')}>
      {@render chev(expanded.gen)}
      <span class="pwr__node-title">GENERATION</span>
      <span class="pwr__total"
            class:pwr__total--zero={totals.gen <= 0}
            class:pwr__total--hot={totals.gen > 0}>
        {genFmt.mag}<em>{genFmt.unit}</em>
      </span>
    </button>
    {#if expanded.gen}
      {#if generators.current.length === 0}
        {@render emptyMsg('NO GENERATORS')}
      {:else}
        <ul class="pwr__rows">
          {#if genGroups.groupSolar}
            <li class="pwr__subgroup">
              <div class="pwr__subgroup-head-wrap"
                   onmouseenter={() => highlightOn(genGroups.solar.map(p => p.struct.id))}
                   onmouseleave={highlightOff}
                   role="presentation">
                <button type="button" class="pwr__subgroup-head"
                        aria-expanded={expanded.gen_solar}
                        onclick={() => toggle('gen_solar')}>
                  {@render chev(expanded.gen_solar)}
                  <span class="pwr__row-icon pwr__row-icon--accent">
                    <ComponentIcon kind="solar" />
                  </span>
                  <span class="pwr__subgroup-title">
                    SOLAR <em>· {genGroups.solar.length}</em>
                  </span>
                  <span class="pwr__row-rate"
                        class:pwr__row-rate--zero={isZero(genGroups.solarTotal)}>
                    <span class="pwr__row-rate-cur">{solarSubgroupFmt.cMag}</span><span
                      class="pwr__row-rate-max">/{solarSubgroupFmt.mMag}</span><em>{solarSubgroupFmt.unit}</em>
                  </span>
                </button>
                {@render solarBulkControl(genGroups.solar)}
              </div>
              {#if expanded.gen_solar}
                <ul class="pwr__rows pwr__rows--nested">
                  {#each genGroups.solar as p (p.struct.id)}
                    {@const s = solarOf(p)}
                    {@const sr = solarRates(p)}
                    {@const sp = fmtRatePair(sr.current, sr.max)}
                    <li class="pwr__row pwr__row--nested"
                        class:pwr__row--closed={s && !s.deployed}
                        onmouseenter={() => highlightOn([p.struct.id])}
                        onmouseleave={highlightOff}>
                      <span class="pwr__row-icon">
                        <ComponentIcon kind="solar" />
                      </span>
                      <span class="pwr__row-name">{p.struct.title}</span>
                      {@render solarControl(p)}
                      <span class="pwr__row-rate"
                            class:pwr__row-rate--zero={isZero(sr.current)}>
                        <span class="pwr__row-rate-cur">{sp.cMag}</span><span
                          class="pwr__row-rate-max">/{sp.mMag}</span><em>{sp.unit}</em>
                      </span>
                    </li>
                  {/each}
                </ul>
              {/if}
            </li>
          {/if}
          {#each genGroups.inline as p (p.struct.id)}
            {#if isFuelCellPart(p)}
              {@const fc = p.state!.fuelCell[0]}
              {@const fco = fuelCellOutput(p)}
              {@const fcr = fmtRatePair(fco.current, fco.max)}
              <li class="pwr__row pwr__row--storage pwr__row--fuel-cell"
                  onmouseenter={() => highlightOn([p.struct.id])}
                  onmouseleave={highlightOff}>
                <span class="pwr__row-icon">
                  <ComponentIcon kind="fuelCell" />
                </span>
                <div class="pwr__row-stack">
                  <div class="pwr__row-line">
                    <span class="pwr__row-name">{p.struct.title}</span>
                    <span class="pwr__fc-tag"
                          class:pwr__fc-tag--on={fc.isActive}
                          class:pwr__fc-tag--off={!fc.isActive}
                          title={fc.isActive ? 'Producing — battery SoC below 80%' : 'Standby — battery SoC above 20%'}>
                      {fc.isActive ? 'ON' : 'OFF'}
                    </span>
                    <span class="pwr__row-rate"
                          class:pwr__row-rate--zero={isZero(fco.current)}>
                      <span class="pwr__row-rate-cur">{fcr.cMag}</span><span
                        class="pwr__row-rate-max">/{fcr.mMag}</span><em>{fcr.unit}</em>
                    </span>
                  </div>
                  <div class="pwr__row-line pwr__row-line--gauge">
                    <span class="pwr__fc-gauge"
                          class:pwr__fc-gauge--refilling={fc.refillActive}
                          title="Fuel-cell manifold (LH₂ + LOx mix)">
                      <SegmentGauge fraction={fc.manifoldFraction} />
                    </span>
                  </div>
                </div>
              </li>
            {:else}
              {@const isSolar = isSolarPart(p)}
              {@const s = isSolar ? solarOf(p) : undefined}
              <li class="pwr__row"
                  class:pwr__row--closed={s && !s.deployed}
                  onmouseenter={() => highlightOn([p.struct.id])}
                  onmouseleave={highlightOff}>
                <span class="pwr__row-icon">
                  <ComponentIcon kind={genKind(p)} />
                </span>
                <span class="pwr__row-name">{p.struct.title}</span>
                {#if isSolar}{@render solarControl(p)}{/if}
                {#if isSolar}
                  {@const sr = solarRates(p)}
                  {@const sp = fmtRatePair(sr.current, sr.max)}
                  <span class="pwr__row-rate"
                        class:pwr__row-rate--zero={isZero(sr.current)}>
                    <span class="pwr__row-rate-cur">{sp.cMag}</span><span
                      class="pwr__row-rate-max">/{sp.mMag}</span><em>{sp.unit}</em>
                  </span>
                {:else}
                  {@const gr = fmtRate(generationRate(p))}
                  <span class="pwr__row-rate"
                        class:pwr__row-rate--zero={isZero(generationRate(p))}>
                    {gr.mag}<em>{gr.unit}</em>
                  </span>
                {/if}
              </li>
            {/if}
          {/each}
        </ul>
      {/if}
    {/if}
  </div>

  <!-- Consumption ------------------------------------------------- -->
  <div class="pwr__node">
    <button type="button" class="pwr__node-head"
            aria-expanded={expanded.consume}
            onclick={() => toggle('consume')}>
      {@render chev(expanded.consume)}
      <span class="pwr__node-title">CONSUMPTION</span>
      <span class="pwr__total"
            class:pwr__total--zero={totals.consume <= 0}
            class:pwr__total--neg={totals.consume > 0}>
        {consumeFmt.mag}<em>{consumeFmt.unit}</em>
      </span>
    </button>
    {#if expanded.consume}
      {#if consumerRows.length === 0}
        {@render emptyMsg('NO CONSUMERS')}
      {:else}
        <ul class="pwr__rows">
          {#each consumerRows as row (row.key)}
            {#if row.kind === 'wheel' && row.wheel}
              {@render wheelRow(row, row.wheel)}
            {:else}
              {@render consumerFlatRow(row)}
            {/if}
          {/each}
        </ul>
      {/if}
    {/if}
  </div>

  <!-- Storage ----------------------------------------------------- -->
  <div class="pwr__node">
    <button type="button" class="pwr__node-head"
            aria-expanded={expanded.store}
            onclick={() => toggle('store')}>
      {@render chev(expanded.store)}
      <span class="pwr__node-title">STORAGE</span>
      <span class="pwr__total"
            class:pwr__total--zero={totals.capacity <= 0}
            class:pwr__total--hot={totals.capacity > 0}>
        {storedFmt.sMag}/{storedFmt.cMag}
        <em>{storedFmt.unit} · {netStorageFmt.mag} {netStorageFmt.unit}</em>
      </span>
    </button>
    <!-- Aggregate gauge stays visible whether or not the children
         are expanded — it's the at-a-glance "vessel power health"
         line and shouldn't disappear behind a collapsed node. -->
    {#if storage.current.length > 0}
      <div class="pwr__node-gauge">
        <SegmentGauge fraction={batteryFraction(totals.stored, totals.capacity)} />
      </div>
    {/if}
    {#if expanded.store}
      {#if storage.current.length === 0}
        {@render emptyMsg('NO BATTERIES')}
      {:else}
        <ul class="pwr__rows">
          {#each storage.current as p (p.struct.id)}
            {@const stored = batteryStored(p)}
            {@const cap = batteryCapacity(p)}
            {@const rate = batteryRate(p)}
            {@const cp = fmtCapPair(stored, cap)}
            {@const rp = fmtRate(rate)}
            <li class="pwr__row pwr__row--storage"
                onmouseenter={() => highlightOn([p.struct.id])}
                onmouseleave={highlightOff}>
              <span class="pwr__row-icon">
                <ComponentIcon kind="battery" />
              </span>
              <div class="pwr__row-stack">
                <div class="pwr__row-line">
                  <span class="pwr__row-name">{p.struct.title}</span>
                  <span class="pwr__row-rate">
                    <span class="pwr__row-rate-stored">{cp.sMag}</span>
                    <span class="pwr__row-rate-cap">/{cp.cMag} {cp.unit}</span>
                    <span class="pwr__row-rate-net"
                          class:pwr__row-rate-net--neg={rate > RATE_EPSILON}
                          class:pwr__row-rate-net--zero={isZero(rate)}>
                      <span class="pwr__row-rate-sep">·</span>{rp.mag} {rp.unit}
                    </span>
                  </span>
                </div>
                <div class="pwr__row-line pwr__row-line--gauge">
                  <SegmentGauge fraction={batteryFraction(stored, cap)} />
                </div>
              </div>
            </li>
          {/each}
        </ul>
      {/if}
    {/if}
  </div>
</section>

<style>
  .pwr {
    display: flex;
    flex-direction: column;
    gap: 0;
    /* Reserve room on the left so the section indicator bars (the
       ::before tab markers on each node head) sit just inside the
       panel padding without clipping. */
    padding-left: 4px;
    margin-left: -4px;
  }

  .pwr__node {
    margin-top: 12px;
  }
  .pwr__node:first-child {
    margin-top: 0;
  }

  /* The clickable header strips button chrome and re-adopts the
     section-head visual rhythm, with the chevron leading. The
     ::before pseudo is a left-edge indicator bar — dim while the
     node is expanded (passive "this is open"), bright on hover or
     focus (active "click me"). The :hover state also lifts a
     right-trailing accent wash across the underline so the section
     reads as a tab being entered, not just a button being pressed. */
  .pwr__node-head {
    appearance: none;
    background: transparent;
    border: none;
    padding: 2px 4px 4px 4px;
    margin: 0 0 4px;
    width: 100%;
    color: inherit;
    font: inherit;
    text-align: left;
    cursor: pointer;
    display: flex;
    align-items: center;
    gap: 8px;
    position: relative;
    font-family: var(--font-display);
    letter-spacing: 0.22em;
    border-bottom: 1px solid var(--line);
    transition: border-color 220ms cubic-bezier(0.4, 0, 0.2, 1);
  }
  /* The double-rule effect — a hairline a couple of pixels below the
     primary border-bottom — recalls the etched lines on real flight
     instruments. Stays at line color so it doesn't compete with
     content; brightens slightly on hover. */
  .pwr__node-head::after {
    content: '';
    position: absolute;
    left: 0;
    right: 0;
    bottom: -3px;
    height: 1px;
    background: linear-gradient(90deg,
      transparent 0%,
      rgba(126, 245, 184, 0.06) 18%,
      rgba(126, 245, 184, 0.06) 82%,
      transparent 100%);
    transition: background 220ms cubic-bezier(0.4, 0, 0.2, 1);
  }
  .pwr__node-head:hover::after,
  .pwr__node-head:focus-visible::after {
    background: linear-gradient(90deg,
      transparent 0%,
      rgba(126, 245, 184, 0.22) 18%,
      rgba(126, 245, 184, 0.22) 82%,
      transparent 100%);
  }

  /* Left-edge indicator bar. */
  .pwr__node-head::before {
    content: '';
    position: absolute;
    left: -4px;
    top: 50%;
    height: 70%;
    width: 2px;
    background: var(--accent);
    opacity: 0;
    transform: translateY(-50%) scaleY(0.4);
    transform-origin: center;
    transition:
      opacity 220ms cubic-bezier(0.4, 0, 0.2, 1),
      transform 320ms cubic-bezier(0.16, 1, 0.3, 1),
      box-shadow 220ms cubic-bezier(0.4, 0, 0.2, 1);
  }
  .pwr__node-head[aria-expanded='true']::before {
    opacity: 0.45;
    transform: translateY(-50%) scaleY(1);
    background: var(--accent-dim);
  }
  .pwr__node-head:hover::before,
  .pwr__node-head:focus-visible::before {
    opacity: 1;
    transform: translateY(-50%) scaleY(1);
    background: var(--accent);
    box-shadow: 0 0 6px var(--accent-glow);
  }

  .pwr__node-head:hover .pwr__node-title,
  .pwr__node-head:focus-visible .pwr__node-title {
    color: var(--accent-soft);
    text-shadow: 0 0 6px var(--accent-glow);
  }
  .pwr__node-head:focus-visible {
    outline: none;
  }
  .pwr__node-head:hover {
    border-bottom-color: var(--accent-dim);
  }
  .pwr__node-title {
    flex: 1 1 auto;
    font-size: 11px;
    color: var(--fg-dim);
    transition:
      color 220ms cubic-bezier(0.4, 0, 0.2, 1),
      text-shadow 220ms cubic-bezier(0.4, 0, 0.2, 1);
  }

  /* Mechanical chevron — quart-out easing on rotation feels snappier
     than the default ease, and a hint of scale on hover makes it
     read as an actual control rather than passive decoration. */
  .pwr__chev {
    flex: 0 0 8px;
    width: 8px;
    height: 8px;
    color: var(--fg-mute);
    transition:
      transform 280ms cubic-bezier(0.16, 1, 0.3, 1),
      color 220ms cubic-bezier(0.4, 0, 0.2, 1),
      filter 220ms cubic-bezier(0.4, 0, 0.2, 1);
  }
  .pwr__chev--open {
    transform: rotate(90deg);
  }
  .pwr__node-head:hover .pwr__chev,
  .pwr__subgroup-head:hover .pwr__chev {
    color: var(--accent);
    filter: drop-shadow(0 0 3px var(--accent-glow));
  }
  .pwr__node-head:hover .pwr__chev--open,
  .pwr__subgroup-head:hover .pwr__chev--open {
    transform: rotate(90deg) scale(1.18);
  }
  .pwr__node-head:hover .pwr__chev:not(.pwr__chev--open),
  .pwr__subgroup-head:hover .pwr__chev:not(.pwr__chev--open) {
    transform: scale(1.18);
  }

  .pwr__node-gauge {
    margin: 4px 0 8px;
    padding: 0 2px;
  }
  /* The aggregate gauge takes a touch more vertical weight — it's
     the at-a-glance "vessel power health" line and benefits from
     standing slightly taller than the per-row gauges underneath. */
  .pwr__node-gauge :global(.sg) {
    height: 12px;
  }

  .pwr__total {
    flex: 0 0 auto;
    font-size: 11px;
    color: var(--accent);
    font-variant-numeric: tabular-nums;
    transition: color 220ms cubic-bezier(0.4, 0, 0.2, 1),
                text-shadow 220ms cubic-bezier(0.4, 0, 0.2, 1);
  }
  .pwr__total--hot {
    color: var(--accent-soft);
    text-shadow: 0 0 6px var(--accent-glow);
  }
  .pwr__total--neg {
    color: var(--warn);
    text-shadow: 0 0 6px var(--warn-glow);
  }
  .pwr__total--zero {
    color: var(--fg-dim);
    text-shadow: none;
  }
  .pwr__total em {
    font-style: normal;
    font-size: 9px;
    color: var(--fg-dim);
    margin-left: 3px;
    letter-spacing: 0.14em;
    text-shadow: none;
  }

  .pwr__rows {
    list-style: none;
    margin: 0;
    padding: 0;
    display: flex;
    flex-direction: column;
  }
  .pwr__row {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 8px;
    padding: 4px 6px;
    margin: 0 -6px;
    position: relative;
    border-bottom: 1px solid rgba(26, 35, 53, 0.55);
    transition: background 200ms cubic-bezier(0.4, 0, 0.2, 1);
  }
  .pwr__row:last-child {
    border-bottom: 0;
  }
  /* Row-level hover: a faint accent wash, plus a leading 2px accent
     bar that grows in. The bar uses a transform-origin top so it
     wipes downward — small detail but it gives each hover a sense
     of direction rather than just "fade in". */
  .pwr__row::before {
    content: '';
    position: absolute;
    left: 0;
    top: 2px;
    bottom: 2px;
    width: 2px;
    background: var(--accent);
    opacity: 0;
    transform: scaleY(0);
    transform-origin: top;
    transition:
      opacity 220ms cubic-bezier(0.4, 0, 0.2, 1),
      transform 280ms cubic-bezier(0.16, 1, 0.3, 1);
  }
  .pwr__row:hover {
    background: rgba(126, 245, 184, 0.04);
  }
  .pwr__row:hover::before {
    opacity: 0.7;
    transform: scaleY(1);
  }

  .pwr__row-icon {
    flex: 0 0 12px;
    display: inline-flex;
    align-items: center;
    color: var(--fg-mute);
    transition: color 220ms cubic-bezier(0.4, 0, 0.2, 1);
  }
  .pwr__row-icon--accent {
    color: var(--accent);
  }
  .pwr__row:hover .pwr__row-icon:not(.pwr__row-icon--accent) {
    color: var(--fg-dim);
  }
  .pwr__row-name {
    flex: 1 1 auto;
    min-width: 0;
    color: var(--fg);
    font-family: var(--font-mono);
    font-size: 11px;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }
  .pwr__row-rate {
    flex: 0 0 auto;
    color: var(--accent);
    font-family: var(--font-mono);
    font-size: 11px;
    font-variant-numeric: tabular-nums;
  }
  .pwr__row-rate--neg {
    color: var(--warn);
  }
  /* Zero rates render in the same dim tone we use for secondary
     metadata — readable, but visibly "off" against the active green
     and warn-orange siblings. */
  .pwr__row-rate--zero {
    color: var(--fg-dim);
  }
  .pwr__row-rate em {
    font-style: normal;
    font-size: 8px;
    color: var(--fg-dim);
    margin-left: 3px;
    letter-spacing: 0.14em;
  }

  /* Storage row rate is composed of three semantically different
     atoms — stored value (primary), capacity divisor (secondary), net
     rate (signed). Splitting them lets each carry its own colour
     instead of inheriting a single tint that misrepresents one of the
     three. The capacity divisor stays at the primary font size — the
     previous `<em>` rendering at 8 px was unreadable next to the
     11 px stored value. */
  .pwr__row-rate-stored {
    color: var(--accent);
  }
  .pwr__row-rate-cap {
    color: var(--fg-dim);
  }
  /* Solar current/max — same primary/secondary split as stored/cap, so
     a panel reading "0.50/1.20 kW" reads the same way a battery reads
     "0.50/1.20 kJ". Current is the live LP-throttled rate; max is the
     orientation-optimal rate the panel could deliver if asked. */
  .pwr__row-rate-cur {
    color: var(--accent);
  }
  .pwr__row-rate-max {
    color: var(--fg-dim);
  }
  .pwr__row-rate-sep {
    color: var(--fg-dim);
    margin: 0 4px 0 6px;
    letter-spacing: 0;
  }
  /* Battery net rate. Default tint (accent / green) is the
     "providing" state — battery draining onto the bus. The `--neg`
     variant flips to warn (amber) for the "consuming" state — battery
     charging from the bus. Mirrors the system-wide rate-colour
     convention: green out, amber in. */
  .pwr__row-rate-net {
    color: var(--accent);
  }
  .pwr__row-rate-net--neg {
    color: var(--warn);
  }
  .pwr__row-rate-net--zero {
    color: var(--fg-dim);
  }

  /* Storage rows: double-height so the gauge gets the full row width
     instead of being squeezed beside the name. Icon stays top-aligned
     with the title line. */
  .pwr__row--storage {
    align-items: flex-start;
    padding: 6px 6px;
  }
  .pwr__row--storage .pwr__row-icon {
    margin-top: 1px;
  }
  .pwr__row-stack {
    flex: 1 1 auto;
    min-width: 0;
    display: flex;
    flex-direction: column;
    gap: 5px;
  }
  .pwr__row-line {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 6px;
    min-width: 0;
  }
  .pwr__row-line--gauge {
    /* Gauge fills the line; SegmentGauge is `width: 100%` so it
       stretches to whatever space the stack column gives it. */
    padding: 0 1px;
  }

  /* Fuel cell rows: borrow the storage row's double-height layout
     (icon left, two-line stack right), but render two manifold gauges
     side-by-side on the second line. The H/O letter tabs ahead of
     each gauge keep the LH₂ vs LOx assignment legible without forcing
     a wider label column. */
  .pwr__row--fuel-cell .pwr__row-name {
    flex: 1 1 auto;
    min-width: 0;
  }
  .pwr__fc-tag {
    flex: 0 0 auto;
    padding: 0 5px;
    margin: 0 4px 0 0;
    border: 1px solid var(--accent-dim);
    color: var(--accent);
    font-family: var(--font-display);
    font-size: 8px;
    line-height: 11px;
    letter-spacing: 0.16em;
    border-radius: 1px;
    user-select: none;
  }
  /* OFF reads as standby — same dim-grey rhythm we use for zero rates,
     so the row visually quiets down when the cell isn't producing. */
  .pwr__fc-tag--off {
    color: var(--fg-dim);
    border-color: var(--line);
  }
  /* Single mix-manifold gauge spans the full row width. */
  .pwr__fc-gauge {
    flex: 1 1 auto;
    display: flex;
    align-items: center;
    min-width: 0;
  }
  .pwr__fc-gauge :global(.sg) {
    flex: 1 1 auto;
  }
  /* While refill is filling the manifold, brighten the gauge with a
     soft accent glow — the gauge fill already encodes fraction; the
     glow is the "actively refilling from main tanks" cue. */
  .pwr__fc-gauge--refilling :global(.sg) {
    filter: drop-shadow(0 0 2px var(--accent-glow));
  }

  /* Empty leading slot used by flat consumer rows so their icons sit
     in the same horizontal column as the wheel-head chevron's icon
     (chev-width + flex gap). Without this the same-row alignment
     drifts visibly when a wheel and a command row sit adjacent. */
  .pwr__row-chev-spacer {
    flex: 0 0 8px;
    width: 8px;
  }

  /* Reaction-wheel row. The head is a button that takes the standard
     row layout (chevron + icon + title + rate); the detail block sits
     below it as a stack of small label/value lines, indented under
     the icon. When closed the detail is unrendered, so the row reads
     identically to a flat consumer row but with a leading chevron. */
  .pwr__row--wheel {
    flex-direction: column;
    align-items: stretch;
    padding: 0;
  }
  .pwr__row--wheel-open {
    padding-bottom: 6px;
  }
  /* Head row inherits the per-row hover/highlight rhythm by reusing
     the standard row paddings. The button is a transparent shell —
     all visual state comes from .pwr__row up top. */
  .pwr__wheel-head {
    appearance: none;
    background: transparent;
    border: none;
    color: inherit;
    font: inherit;
    text-align: left;
    cursor: pointer;
    width: 100%;
    display: flex;
    align-items: center;
    gap: 8px;
    padding: 4px 6px;
    margin: 0;
  }
  .pwr__wheel-head:focus-visible {
    outline: none;
  }
  /* The detail block: indented under the icon column, separated from
     the head by a faint divider that brightens on row hover. The
     label/value rhythm matches the head row's mono font + tabular
     numerals so columns line up vertically. */
  .pwr__wheel-detail {
    display: flex;
    flex-direction: column;
    gap: 4px;
    padding: 2px 6px 0 30px;  /* 30px ≈ chev + icon + gap */
    margin-top: 2px;
    border-top: 1px solid rgba(126, 245, 184, 0.06);
  }
  .pwr__wheel-detail-line {
    display: flex;
    align-items: center;
    gap: 8px;
    min-width: 0;
  }
  .pwr__wheel-detail-label {
    flex: 1 1 auto;
    color: var(--fg-dim);
    font-family: var(--font-mono);
    font-size: 10px;
    letter-spacing: 0.04em;
  }
  /* Buffer gauge sub-row: gauge fills the slack, signed rate readout
     sits to the right. The gauge accent on `--refilling` matches the
     fuel-cell convention — soft glow rather than colour shift, since
     the readout's sign already signals direction. */
  .pwr__wheel-detail-line--gauge {
    gap: 8px;
  }
  .pwr__wheel-detail-line--gauge :global(.sg) {
    flex: 1 1 auto;
  }
  .pwr__wheel-gauge--refilling :global(.sg) {
    filter: drop-shadow(0 0 2px var(--accent-glow));
  }

  /* Solar sub-group: a soft header inside Generation, then panels
     listed beneath at the same row rhythm but indented to advertise
     the hierarchy. The L-bracket left-rule reinforces the tree
     metaphor without needing an explicit connector glyph. */
  .pwr__subgroup {
    list-style: none;
    margin: 0;
    padding: 0;
    border-bottom: 1px solid rgba(26, 35, 53, 0.55);
  }
  .pwr__subgroup-head {
    appearance: none;
    background: transparent;
    border: none;
    color: inherit;
    font: inherit;
    text-align: left;
    cursor: pointer;
    width: 100%;
    display: flex;
    align-items: center;
    gap: 8px;
    padding: 4px 6px;
    margin: 0 -6px;
    position: relative;
    transition: background 200ms cubic-bezier(0.4, 0, 0.2, 1);
  }
  .pwr__subgroup-head::before {
    content: '';
    position: absolute;
    left: 0;
    top: 3px;
    bottom: 3px;
    width: 2px;
    background: var(--accent);
    opacity: 0;
    transform: scaleY(0);
    transform-origin: top;
    transition:
      opacity 220ms cubic-bezier(0.4, 0, 0.2, 1),
      transform 280ms cubic-bezier(0.16, 1, 0.3, 1);
  }
  .pwr__subgroup-head:hover {
    background: rgba(126, 245, 184, 0.04);
  }
  .pwr__subgroup-head:hover::before {
    opacity: 1;
    transform: scaleY(1);
  }
  .pwr__subgroup-head:focus-visible {
    outline: none;
    background: rgba(126, 245, 184, 0.05);
  }
  .pwr__subgroup-title {
    flex: 1 1 auto;
    font-family: var(--font-display);
    font-size: 10px;
    letter-spacing: 0.20em;
    color: var(--fg-dim);
    transition: color 220ms cubic-bezier(0.4, 0, 0.2, 1);
  }
  .pwr__subgroup-head:hover .pwr__subgroup-title {
    color: var(--accent);
  }
  .pwr__subgroup-title em {
    font-style: normal;
    color: var(--fg-dim);
    letter-spacing: 0.14em;
    margin-left: 3px;
  }
  .pwr__rows--nested {
    padding-left: 16px;
    border-left: 1px solid rgba(126, 245, 184, 0.10);
    margin: 0 0 2px 7px;
  }
  .pwr__row--nested {
    padding-left: 4px;
  }
  .pwr__row--nested .pwr__row-name {
    color: var(--fg-dim);
  }

  /* Empty-state line: a tracked-out instrument annotation flanked by
     hairline rules that fade to transparent at the edges. Reads as a
     status callout, not a sentence. */
  .pwr__empty {
    display: flex;
    align-items: center;
    gap: 10px;
    margin: 6px 0;
    padding: 0 4px;
  }
  .pwr__empty-rule {
    flex: 1 1 auto;
    height: 1px;
    background: linear-gradient(90deg,
      transparent 0%,
      rgba(126, 245, 184, 0.10) 50%,
      transparent 100%);
  }
  .pwr__empty-text {
    flex: 0 0 auto;
    font-family: var(--font-display);
    font-size: 9px;
    color: var(--fg-dim);
    letter-spacing: 0.24em;
  }

  /* Per-row solar deploy action. A bordered display-font label
     ("EXT" / "RET") instead of an iconographic chevron — the chevron
     was tiny enough that users didn't recognise it as a button. The
     label sits to the right of the panel name, before the rate
     readout, with letter-spacing matching the tab chips so it reads
     as part of the same visual family. */
  .pwr__deploy-btn {
    appearance: none;
    flex: 0 0 auto;
    padding: 1px 6px;
    margin: 0;
    background: transparent;
    border: 1px solid var(--accent-dim);
    color: var(--accent);
    font-family: var(--font-display);
    font-size: 9px;
    line-height: 12px;
    letter-spacing: 0.16em;
    cursor: pointer;
    display: inline-flex;
    align-items: center;
    justify-content: center;
    border-radius: 1px;
    user-select: none;
    transition:
      color 200ms cubic-bezier(0.4, 0, 0.2, 1),
      border-color 200ms cubic-bezier(0.4, 0, 0.2, 1),
      background 200ms cubic-bezier(0.4, 0, 0.2, 1),
      box-shadow 200ms cubic-bezier(0.4, 0, 0.2, 1);
  }
  .pwr__deploy-btn:hover {
    background: rgba(126, 245, 184, 0.10);
    border-color: var(--accent);
    box-shadow: 0 0 4px var(--accent-glow);
    text-shadow: 0 0 4px var(--accent-glow);
  }
  .pwr__deploy-btn:active {
    background: rgba(126, 245, 184, 0.20);
  }
  .pwr__deploy-btn:focus-visible {
    outline: none;
    border-color: var(--accent);
  }
  /* RET uses the warn palette so the two states read as distinct
     directions (extending = lit-green, retracting = warning-amber)
     rather than two interchangeable "click me" buttons. */
  .pwr__deploy-btn--ret {
    color: var(--warn);
    border-color: color-mix(in srgb, var(--warn) 50%, transparent);
  }
  .pwr__deploy-btn--ret:hover {
    background: rgba(240, 180, 41, 0.10);
    border-color: var(--warn);
    box-shadow: 0 0 4px var(--warn-glow);
    text-shadow: 0 0 4px var(--warn-glow);
  }
  /* Busy: action sent, animation in flight. Dimmed letters + a slow
     pulse on the border so the user sees the click registered even
     before the topic state catches up. Span (not button) so it can't
     be re-clicked while moving. */
  .pwr__deploy-btn--busy {
    color: var(--fg-mute);
    border-color: var(--line);
    cursor: default;
    animation: pwr-deploy-busy 1.2s ease-in-out infinite;
    text-shadow: none;
  }
  @keyframes pwr-deploy-busy {
    0%, 100% { border-color: var(--line); }
    50%      { border-color: var(--accent-dim); box-shadow: 0 0 4px var(--accent-glow); }
  }
  /* Bulk variant — wider so EXT ALL / RET ALL fit. Sits trailing the
     subgroup head, separated by a small gap. */
  .pwr__deploy-btn--bulk {
    flex: 0 0 auto;
    padding: 1px 8px;
  }

  /* Test load toggle — same chip-shape as EXT/RET so the row reads
     consistently. Off state is dim accent (the load is latent); on
     state borrows the warn palette to signal "this is draining the
     bus on purpose." */
  .pwr__test-load-btn {
    color: var(--fg-mute);
    border-color: var(--line);
  }
  .pwr__test-load-btn:hover {
    color: var(--accent);
    border-color: var(--accent-dim);
    background: rgba(126, 245, 184, 0.06);
    box-shadow: none;
    text-shadow: none;
  }
  .pwr__test-load-btn--on {
    color: var(--warn);
    border-color: color-mix(in srgb, var(--warn) 60%, transparent);
    background: rgba(240, 180, 41, 0.06);
  }
  .pwr__test-load-btn--on:hover {
    background: rgba(240, 180, 41, 0.16);
    border-color: var(--warn);
    box-shadow: 0 0 4px var(--warn-glow);
    text-shadow: 0 0 4px var(--warn-glow);
  }

  /* Subgroup head wrapper — places the head button and the bulk
     deploy control on one row. The head fills available space; the
     bulk button sits trailing, full-width-aware via flex-basis auto. */
  .pwr__subgroup-head-wrap {
    display: flex;
    align-items: center;
    gap: 6px;
  }
  .pwr__subgroup-head-wrap .pwr__subgroup-head {
    flex: 1 1 auto;
    min-width: 0;
  }

  /* Closed solar panel row: dim the text/icon/rate per-element so
     the action button can stay at full strength (it's the only
     interactive surface in a non-functional row). The hover wash
     and indicator bar are still active, since the part is real
     and worth highlighting in the 3-D scene. */
  .pwr__row--closed .pwr__row-icon {
    color: var(--fg-mute);
  }
  .pwr__row--closed .pwr__row-name {
    color: var(--fg-dim);
    font-style: italic;
  }
  .pwr__row--closed .pwr__row-rate {
    color: var(--fg-dim);
  }
  .pwr__row--closed .pwr__row-rate em {
    color: var(--fg-mute);
  }
</style>
