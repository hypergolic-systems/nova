<script lang="ts">
  // ─────────────────────────────────────────────────────────────────
  //  PROPULSION CONSOLE
  //
  //  Hosts every propulsion-class component on the vessel, grouped
  //  by engine family:
  //
  //    ENGINES      — chemical thrusters. Flat rows with an on/off
  //                   rocker so the player can shut a staged engine
  //                   down without un-staging the whole stack, plus
  //                   a state badge (BURN / IDLE / FLAME / OFF) and
  //                   live thrust readout.
  //
  //    REACTORS     — nuclear engines (LV-N class). Tall cards with
  //                   the reactor state-machine badge, calibrated
  //                   fuel-rod temperature bar, draggable throttle
  //                   bar with target marker, and the THERMAL / FLOW
  //                   / THRUST instrument tiles. (Lifted verbatim
  //                   from the prior NukView — only the wrapper
  //                   class names changed.)
  //
  //    ION DRIVES   — NSTAR-class ion thrusters. Cards centred on
  //                   the cross-system coupling story: thermal
  //                   meter normalised to the trip threshold, paired
  //                   EC // Xe satisfaction bars (with a hazard
  //                   tint when EC outpaces Xe — precursor to a
  //                   starvation trip), and the WASTE → REJECT
  //                   thermal handshake. Trip latch surfaces as
  //                   FAULT chrome (red strip, pulsing badge,
  //                   prominent RESET button).
  //
  //  Source of truth: NovaPart frame's `engine`/`nuclear`/`ion`
  //  slots (ui/.../nova-topics.ts), and the matching C# components
  //  in Nova.Core.Components.Propulsion.
  // ─────────────────────────────────────────────────────────────────

  import { useNovaParts } from '../../telemetry/use-nova-parts.svelte';
  import type { NovaPartHandle } from '../../telemetry/use-nova-parts.svelte';
  import { NovaPartTopic, ReactorState, IonTripReason } from '../../telemetry/nova-topics';
  import { getKsp, useStageOps } from '@dragonglass/telemetry/svelte';
  import { onDestroy } from 'svelte';
  import { siPrefix, fmtMag } from '../../util/units';
  import ComponentIcon from '../ComponentIcon.svelte';
  import Subheading from '../common/Subheading.svelte';
  import Chip from '../common/Chip.svelte';

  interface Props {
    vesselId: string;
    /** Bound out: true when the view has hardware to render. */
    hasContent?: boolean;
  }
  let { vesselId, hasContent = $bindable(true) }: Props = $props();

  const vesselParts = useNovaParts(() => vesselId);
  const ksp = getKsp();


  // ── Partitioning ────────────────────────────────────────────────────
  function isChemicalEngine(p: NovaPartHandle): boolean {
    return !!p.state && p.state.engine.length > 0;
  }
  function isReactor(p: NovaPartHandle): boolean {
    return !!p.state && p.state.nuclear.length > 0;
  }
  function isIonDrive(p: NovaPartHandle): boolean {
    return !!p.state && p.state.ion.length > 0;
  }
  const chemEngines = $derived(vesselParts.current.filter(isChemicalEngine));
  const reactors    = $derived(vesselParts.current.filter(isReactor));
  const ionDrives   = $derived(vesselParts.current.filter(isIonDrive));

  $effect(() => {
    hasContent = chemEngines.length > 0
              || reactors.length > 0
              || ionDrives.length > 0;
  });

  function engineOf(p: NovaPartHandle) { return p.state?.engine[0]; }
  function reactorOf(p: NovaPartHandle) { return p.state?.nuclear[0]; }
  function ionOf(p: NovaPartHandle) { return p.state?.ion[0]; }

  // ── Chemical engine helpers ─────────────────────────────────────────
  function setEngineActive(partId: string, active: boolean): void {
    ksp.send(NovaPartTopic(partId), 'setEngineActive', active);
  }

  // Status-byte → label/zone. Aligned with EngineFrameFormatter's
  // emit: 0 burning, 1 flameout, 3 shutdown, 4 idle. (2 "failed" is
  // reserved; we render it as OFF for sanity.)
  type EngineZone = 'cool' | 'normal' | 'caution' | 'danger';
  function engineLabel(status: number, flameout: boolean): string {
    if (status === 0) return 'BURN';
    if (status === 1 || flameout) return 'FLAME';
    if (status === 4) return 'IDLE';
    return 'OFF';
  }
  function engineZone(status: number, flameout: boolean): EngineZone {
    if (status === 0) return 'normal';
    if (status === 1 || flameout) return 'caution';
    if (status === 4) return 'cool';
    return 'cool';
  }

  // ── Reactor helpers (lifted from NukView) ──────────────────────────
  const IDLE_TEMP_K        = 1500;
  const OPERATING_TEMP_K   = 2700;
  const MELTDOWN_TEMP_K    = 3100;
  const TEMP_SCALE_K       = 3200;
  const IDLE_FRAC      = IDLE_TEMP_K      / TEMP_SCALE_K;
  const OPERATING_FRAC = OPERATING_TEMP_K / TEMP_SCALE_K;
  const MELTDOWN_FRAC  = MELTDOWN_TEMP_K  / TEMP_SCALE_K;
  const SPOOL_END_FRAC = 0.25;
  const LH2_KG_PER_L   = 0.07;
  const SPOOL_END_THROTTLE = SPOOL_END_FRAC;
  const IDLE_THROTTLE_THRESHOLD = 1e-3;

  function reactorStateLabel(state: ReactorState, throttleActual: number): string {
    switch (state) {
      case ReactorState.Cold:    return 'COLD';
      case ReactorState.Warming: return 'WARMING';
      case ReactorState.Cooling: return 'COOLING';
      case ReactorState.Throttled:
        if (throttleActual < IDLE_THROTTLE_THRESHOLD)     return 'IDLE';
        if (throttleActual < SPOOL_END_THROTTLE)          return 'SPOOL';
        return 'BURN';
      case ReactorState.Idle:    return 'IDLE';
      default: return '';
    }
  }
  function isReactorRunning(state: ReactorState, shutdownRequested: boolean): boolean {
    if (shutdownRequested) return false;
    return state === ReactorState.Warming
        || state === ReactorState.Throttled
        || state === ReactorState.Idle;
  }
  function setReactorActive(partId: string, active: boolean): void {
    ksp.send(NovaPartTopic(partId), 'setReactorActive', active);
  }
  function setReactorPlayerThrottle(partId: string, frac: number): void {
    ksp.send(NovaPartTopic(partId), 'setReactorPlayerThrottle',
        Math.max(0, Math.min(1, frac)));
  }

  let throttleDrag = $state<{ partId: string; frac: number } | null>(null);
  function throttleBarFrac(bar: HTMLDivElement, clientX: number): number {
    const rect = bar.getBoundingClientRect();
    if (rect.width <= 0) return 0;
    return Math.max(0, Math.min(1, (clientX - rect.left) / rect.width));
  }
  function throttleBarDown(e: PointerEvent, partId: string): void {
    if (e.button !== 0) return;
    e.preventDefault();
    const bar = e.currentTarget as HTMLDivElement;
    bar.setPointerCapture(e.pointerId);
    const frac = throttleBarFrac(bar, e.clientX);
    throttleDrag = { partId, frac };
    setReactorPlayerThrottle(partId, frac);
  }
  function throttleBarMove(e: PointerEvent, partId: string): void {
    if (throttleDrag === null || throttleDrag.partId !== partId) return;
    const bar = e.currentTarget as HTMLDivElement;
    const frac = throttleBarFrac(bar, e.clientX);
    throttleDrag = { partId, frac };
    setReactorPlayerThrottle(partId, frac);
  }
  function throttleBarUp(e: PointerEvent, partId: string): void {
    if (throttleDrag === null || throttleDrag.partId !== partId) return;
    const bar = e.currentTarget as HTMLDivElement;
    const frac = throttleBarFrac(bar, e.clientX);
    setReactorPlayerThrottle(partId, frac);
    throttleDrag = null;
    try { bar.releasePointerCapture(e.pointerId); } catch { /* ignored */ }
  }

  type TempZone = 'cool' | 'normal' | 'caution' | 'danger';
  const ZONE_UPPER_MARGIN = 10;
  function tempZone(k: number): TempZone {
    if (k > MELTDOWN_TEMP_K + ZONE_UPPER_MARGIN)  return 'danger';
    if (k > OPERATING_TEMP_K + ZONE_UPPER_MARGIN) return 'caution';
    if (k >= IDLE_TEMP_K)                         return 'normal';
    return 'cool';
  }

  // ── Ion drive helpers ───────────────────────────────────────────────
  const ION_NORMAL_FRAC  = 0.60;
  const ION_CAUTION_FRAC = 0.85;
  const ION_ASSUMED_AMBIENT_K = 290;

  function ionTempFrac(coreTempK: number, maxOperatingTempK: number): number {
    const span = maxOperatingTempK - ION_ASSUMED_AMBIENT_K;
    if (span <= 0) return 0;
    return Math.max(0, Math.min(1, (coreTempK - ION_ASSUMED_AMBIENT_K) / span));
  }
  function ionZone(frac: number, tripped: boolean,
                    reason: IonTripReason): TempZone {
    if (tripped && reason === IonTripReason.Overtemp) return 'danger';
    if (frac >= 1.0)                return 'danger';
    if (frac >= ION_CAUTION_FRAC)   return 'caution';
    if (frac >= ION_NORMAL_FRAC)    return 'normal';
    return 'cool';
  }
  function ionStateLabel(s: {
    tripped: boolean; tripReason: IonTripReason; active: boolean;
    throttle: number; coreTempK: number; maxOperatingTempK: number;
  }): string {
    if (s.tripped) {
      switch (s.tripReason) {
        case IonTripReason.XeStarvation: return 'TRIP·XE';
        case IonTripReason.Overtemp:     return 'TRIP·HOT';
        default:                          return 'TRIP';
      }
    }
    if (!s.active) return 'OFF';
    const tf = ionTempFrac(s.coreTempK, s.maxOperatingTempK);
    if (tf >= ION_CAUTION_FRAC) return 'HOT';
    if (s.throttle >= 0.005) return 'BURN';
    return 'STBY';
  }
  function resetIonTrip(partId: string): void {
    ksp.send(NovaPartTopic(partId), 'setIonResetTrip');
  }
  function ionTripTooltip(reason: IonTripReason): string {
    switch (reason) {
      case IonTripReason.XeStarvation:
        return 'Xenon supply collapsed — the accelerator was firing into vacuum. '
             + 'Reset the latch; refuel before relighting.';
      case IonTripReason.Overtemp:
        return 'Core temperature exceeded the operating envelope — '
             + 'radiator headroom is short. Reset the latch and add cooling.';
      default:
        return 'Engine tripped';
    }
  }

  // ── Formatters (shared) ─────────────────────────────────────────────
  function fmtPower(w: number): { mag: string; unit: string } {
    const p = siPrefix(w);
    return { mag: fmtMag(w / p.div), unit: p.letter + 'W' };
  }
  function fmtThrust(kn: number): string {
    if (Math.abs(kn) < 0.001) return '0.000';
    if (Math.abs(kn) < 0.1)   return kn.toFixed(3);
    if (Math.abs(kn) < 10)    return kn.toFixed(2);
    return kn.toFixed(0);
  }
  function fmtFlowLs(kgs: number): string {
    const ls = kgs / LH2_KG_PER_L;
    if (ls < 0.05) return '0.0';
    if (ls < 10)   return ls.toFixed(1);
    return ls.toFixed(0);
  }
  function fmtTempK(v: number): string {
    return Math.abs(v) >= 100 ? v.toFixed(0) : v.toFixed(1);
  }
  function fmtPct01(v: number): string {
    return Math.round(Math.max(0, Math.min(1, v)) * 100).toString();
  }

  // ── 3-D highlight wiring ────────────────────────────────────────────
  const stageOps = useStageOps();
  function highlightOn(ids: readonly string[]): void { stageOps.setHighlightParts(ids); }
  function highlightOff(): void { stageOps.setHighlightParts([]); }
  onDestroy(() => stageOps.setHighlightParts([]));
</script>

<section class="prp">
  <!-- ENGINES ─ chemical thrusters ─────────────────────────────────── -->
  {#if chemEngines.length > 0}
  <Subheading title="Engines">
    {#snippet summary()}
      <span class="prp__head-count">{chemEngines.length}</span>
    {/snippet}

      <ul class="prp__rows">
          {#each chemEngines as p (p.struct.id)}
            {@const e = engineOf(p)}
            {#if e}
              {@const zone = engineZone(e.status, e.flameout)}
              {@const label = engineLabel(e.status, e.flameout)}
              <li class="ce"
                  data-zone={zone}
                  class:ce--off={!e.active}
                  onmouseenter={() => highlightOn([p.struct.id])}
                  onmouseleave={highlightOff}>
                <span class="ce__icon" aria-hidden="true">
                  <ComponentIcon kind="engine" />
                </span>
                <span class="ce__name">{p.struct.title}</span>
                <span class="ce__state ce__state--{zone}"
                      title="Engine state — wire-emitted status byte">
                  {label}
                </span>
                <Chip
                  kind="latch"
                  intent={e.active ? 'warn' : 'idle'}
                  pressed={e.active}
                  glyph={e.active ? '■' : '▸'}
                  label={e.active ? 'STOP' : 'START'}
                  aria-label={e.active ? 'Shut down engine' : 'Start engine'}
                  title={e.active
                    ? 'Shut down without un-staging'
                    : 'Start engine — needs propellant in pool'}
                  onclick={(ev) => { ev.stopPropagation();
                    setEngineActive(p.struct.id, !e.active); }}
                />
                <span class="ce__thrust"
                      class:ce__thrust--zero={e.currentThrustKn < 0.001}
                      title="Realised thrust this tick / rated thrust">
                  <b>{fmtThrust(e.currentThrustKn)}</b>
                  <span class="ce__thrust-sep">/</span>{fmtThrust(e.maxThrustKn)}<em>kN</em>
                </span>
              </li>
            {/if}
          {/each}
        </ul>
  </Subheading>
  {/if}

  <!-- REACTORS ─ nuclear engines ───────────────────────────────────── -->
  {#if reactors.length > 0}
  <Subheading title="Reactors">
    {#snippet summary()}
      <span class="prp__head-count">{reactors.length}</span>
    {/snippet}

      <ul class="prp__rows">
          {#each reactors as p (p.struct.id)}
            {@const r = reactorOf(p)}
            {#if r}
              {@const running = isReactorRunning(r.state, r.shutdownRequested)}
              {@const zone = tempZone(r.coreTempK)}
              {@const tempFrac = Math.max(0, Math.min(1, r.coreTempK / TEMP_SCALE_K))}
              {@const tempPct = tempFrac * 100}
              {@const actFrac = Math.max(0, Math.min(1, r.throttleActual))}
              {@const tgtFrac = Math.max(0, Math.min(1, r.throttleSetpoint))}
              {@const slewLagging = r.throttleSetpoint > r.throttleActual + 0.005}
              {@const slewLeading = r.throttleSetpoint < r.throttleActual - 0.005}
              {@const powerFmt = fmtPower(r.thermalPowerW)}
              {@const stateLabel = reactorStateLabel(r.state, r.throttleActual)}
              {@const dragFrac = throttleDrag !== null && throttleDrag.partId === p.struct.id
                  ? throttleDrag.frac
                  : null}
              {@const tgtDisplayFrac = dragFrac ?? tgtFrac}

              <li class="rx"
                  data-state={stateLabel}
                  data-zone={zone}
                  onmouseenter={() => highlightOn([p.struct.id])}
                  onmouseleave={highlightOff}>

                <header class="rx__head">
                  <span class="rx__head-mark" aria-hidden="true">⬢</span>
                  <span class="rx__head-name">{p.struct.title}</span>
                  <span class="rx__head-state rx__head-state--{zone}"
                        class:rx__head-state--shutdown={r.shutdownRequested}
                        title="Reactor state machine phase">
                    {stateLabel}{#if r.shutdownRequested}·SHDN{/if}
                  </span>
                  <Chip
                    kind="latch"
                    intent={running ? 'warn' : 'idle'}
                    size="lg"
                    pressed={running}
                    glyph={running ? '■' : '▸'}
                    label={running ? 'STOP' : 'START'}
                    aria-label={running ? 'Shut down reactor' : 'Start reactor'}
                    title={running
                      ? 'SHUTDOWN — auto-sequences from BURN through IDLE'
                      : 'STARTUP — Cold → Warming → Idle'}
                    onclick={() => setReactorActive(p.struct.id, !running)}
                  />
                </header>

                <div class="rx__meter">
                  <div class="rx__meter-row">
                    <span class="rx__meter-label">CORE TEMP</span>
                    <span class="rx__meter-value rx__meter-value--{zone}">
                      {fmtTempK(r.coreTempK)}<em>K</em>
                    </span>
                  </div>

                  <div class="rx__tempbar">
                    <div class="rx__tempbar-zone rx__tempbar-zone--cool"
                         style:width="{IDLE_FRAC * 100}%"></div>
                    <div class="rx__tempbar-zone rx__tempbar-zone--normal"
                         style:left="{IDLE_FRAC * 100}%"
                         style:width="{(OPERATING_FRAC - IDLE_FRAC) * 100}%"></div>
                    <div class="rx__tempbar-zone rx__tempbar-zone--caution"
                         style:left="{OPERATING_FRAC * 100}%"
                         style:width="{(MELTDOWN_FRAC - OPERATING_FRAC) * 100}%"></div>
                    <div class="rx__tempbar-zone rx__tempbar-zone--danger"
                         style:left="{MELTDOWN_FRAC * 100}%"
                         style:width="{(1 - MELTDOWN_FRAC) * 100}%"></div>
                    <div class="rx__tempbar-fill"
                         style:width="{tempPct}%"></div>
                    <div class="rx__tempbar-needle"
                         style:left="{tempPct}%"
                         aria-hidden="true"></div>
                  </div>

                  <div class="rx__tempbar-ticks" aria-hidden="true">
                    <span class="rx__tick" style:left="0%">
                      <span class="rx__tick-rule"></span>
                      <span class="rx__tick-num">0</span>
                    </span>
                    <span class="rx__tick" style:left="{IDLE_FRAC * 100}%">
                      <span class="rx__tick-rule"></span>
                      <span class="rx__tick-num">{IDLE_TEMP_K}</span>
                      <span class="rx__tick-text">IDLE</span>
                    </span>
                    <span class="rx__tick" style:left="{OPERATING_FRAC * 100}%">
                      <span class="rx__tick-rule"></span>
                      <span class="rx__tick-num">{OPERATING_TEMP_K}</span>
                      <span class="rx__tick-text">OPER</span>
                    </span>
                    <span class="rx__tick rx__tick--danger" style:left="{MELTDOWN_FRAC * 100}%">
                      <span class="rx__tick-rule"></span>
                      <span class="rx__tick-num">{MELTDOWN_TEMP_K}</span>
                      <span class="rx__tick-text">MELT</span>
                    </span>
                  </div>
                </div>

                <div class="rx__meter">
                  <div class="rx__meter-row">
                    <span class="rx__meter-label">THROTTLE</span>
                    <span class="rx__meter-row-pair">
                      <span class="rx__throttle-tgt">
                        TGT&nbsp;<b>{fmtPct01(tgtFrac)}</b><em>%</em>
                      </span>
                      <span class="rx__throttle-sep">/</span>
                      <span class="rx__throttle-act"
                            class:rx__throttle-act--zero={actFrac < 0.005}>
                        ACT&nbsp;<b>{fmtPct01(actFrac)}</b><em>%</em>
                      </span>
                    </span>
                  </div>

                  <div class="rx__thrtbar"
                       role="slider"
                       aria-label="Reactor throttle"
                       aria-valuemin={0}
                       aria-valuemax={100}
                       aria-valuenow={Math.round(tgtDisplayFrac * 100)}
                       tabindex="0"
                       onpointerdown={(e) => throttleBarDown(e, p.struct.id)}
                       onpointermove={(e) => throttleBarMove(e, p.struct.id)}
                       onpointerup={(e) => throttleBarUp(e, p.struct.id)}
                       onpointercancel={(e) => throttleBarUp(e, p.struct.id)}>
                    <div class="rx__thrtbar-min"
                         style:width="{SPOOL_END_FRAC * 100}%"
                         aria-hidden="true"></div>
                    <div class="rx__thrtbar-fill"
                         class:rx__thrtbar-fill--lagging={slewLagging}
                         class:rx__thrtbar-fill--leading={slewLeading}
                         style:width="{actFrac * 100}%"></div>
                    <div class="rx__thrtbar-tgt"
                         class:rx__thrtbar-tgt--hidden={tgtDisplayFrac < 0.005}
                         class:rx__thrtbar-tgt--dragging={dragFrac !== null}
                         style:left="{tgtDisplayFrac * 100}%"
                         aria-hidden="true"></div>
                  </div>
                </div>

                <div class="rx__tiles">
                  <div class="rx__tile rx__tile--thermal"
                       class:rx__tile--zero={r.thermalPowerW < 1}>
                    <span class="rx__tile-label">THERMAL</span>
                    <span class="rx__tile-value">
                      <span class="rx__tile-num">{powerFmt.mag}</span><span
                        class="rx__tile-unit">{powerFmt.unit}</span>
                    </span>
                  </div>
                  <div class="rx__tile rx__tile--flow"
                       class:rx__tile--zero={r.lh2FlowKgs < 0.005}>
                    <span class="rx__tile-label">LH₂ FLOW</span>
                    <span class="rx__tile-value">
                      <span class="rx__tile-num">{fmtFlowLs(r.lh2FlowKgs)}</span><span
                        class="rx__tile-unit">L/s</span>
                    </span>
                  </div>
                  <div class="rx__tile rx__tile--thrust"
                       class:rx__tile--zero={r.currentThrustKn < 0.05}>
                    <span class="rx__tile-label">THRUST</span>
                    <span class="rx__tile-value">
                      <span class="rx__tile-num">{fmtThrust(r.currentThrustKn)}</span><span
                        class="rx__tile-unit">kN</span>
                    </span>
                  </div>
                </div>
              </li>
            {/if}
          {/each}
        </ul>
  </Subheading>
  {/if}

  <!-- ION DRIVES ─ NSTAR-class thrusters ──────────────────────────── -->
  {#if ionDrives.length > 0}
  <Subheading title="Ion Drives">
    {#snippet summary()}
      <span class="prp__head-count">{ionDrives.length}</span>
    {/snippet}

      <ul class="prp__rows">
          {#each ionDrives as p (p.struct.id)}
            {@const d = ionOf(p)}
            {#if d}
              {@const tf = ionTempFrac(d.coreTempK, d.maxOperatingTempK)}
              {@const zone = ionZone(tf, d.tripped, d.tripReason)}
              {@const active = !d.tripped && d.throttle > 0}
              {@const label = ionStateLabel({ tripped: d.tripped, tripReason: d.tripReason,
                                              active, throttle: d.throttle,
                                              coreTempK: d.coreTempK,
                                              maxOperatingTempK: d.maxOperatingTempK })}
              {@const ecPct = fmtPct01(d.ecSatisfaction)}
              {@const xePct = fmtPct01(d.xeSatisfaction)}
              {@const couplingDelta = Math.max(0, d.ecSatisfaction - d.xeSatisfaction)}
              {@const powerFmt = fmtPower(d.currentEcW)}
              {@const wasteFmt = fmtPower(d.wasteHeatW)}
              {@const rejectFmt = fmtPower(d.rejectionW)}
              {@const throttlePct = fmtPct01(d.throttle)}
              {@const normalFracPct = ION_NORMAL_FRAC * 100}
              {@const cautionFracPct = ION_CAUTION_FRAC * 100}

              <li class="ir"
                  data-zone={zone}
                  class:ir--tripped={d.tripped}
                  onmouseenter={() => highlightOn([p.struct.id])}
                  onmouseleave={highlightOff}>

                <header class="ir__head">
                  <span class="ir__head-icon" aria-hidden="true">
                    <ComponentIcon kind="ion" />
                  </span>
                  <span class="ir__head-name">{p.struct.title}</span>
                  <span class="ir__head-state ir__head-state--{zone}"
                        class:ir__head-state--tripped={d.tripped}
                        title={d.tripped ? ionTripTooltip(d.tripReason) : 'Operating envelope'}>
                    {label}
                  </span>
                  {#if d.tripped}
                    <Chip
                      kind="action"
                      intent="alert"
                      glyph="↺"
                      label="RESET"
                      aria-label="Clear trip latch"
                      title="Clear trip latch. Engine stays unstaged — re-stage to relight."
                      onclick={() => resetIonTrip(p.struct.id)}
                    />
                  {/if}
                </header>

                <div class="ir__meter">
                  <div class="ir__meter-row">
                    <span class="ir__meter-label">CORE TEMP</span>
                    <span class="ir__meter-value ir__meter-value--{zone}">
                      {fmtTempK(d.coreTempK)}<em>K</em>
                      <span class="ir__meter-sep">/</span>
                      <span class="ir__meter-cap">
                        {fmtTempK(d.maxOperatingTempK)}<em>K</em>
                      </span>
                    </span>
                  </div>
                  <div class="ir__tempbar">
                    <div class="ir__tempbar-zone ir__tempbar-zone--normal"
                         style:width="{normalFracPct}%"></div>
                    <div class="ir__tempbar-zone ir__tempbar-zone--caution"
                         style:left="{normalFracPct}%"
                         style:width="{cautionFracPct - normalFracPct}%"></div>
                    <div class="ir__tempbar-zone ir__tempbar-zone--danger"
                         style:left="{cautionFracPct}%"
                         style:width="{100 - cautionFracPct}%"></div>
                    <div class="ir__tempbar-fill"
                         style:width="{tf * 100}%"></div>
                    <div class="ir__tempbar-trip" aria-hidden="true">
                      <span class="ir__tempbar-trip-rule"></span>
                    </div>
                  </div>
                  <div class="ir__tempbar-foot" aria-hidden="true">
                    <span class="ir__tempbar-trip-label">TRIP</span>
                  </div>
                </div>

                <div class="ir__couple"
                     class:ir__couple--diverged={!d.tripped && couplingDelta > 0.05}>
                  <div class="ir__couple-row">
                    <span class="ir__couple-tag ir__couple-tag--ec">EC</span>
                    <div class="ir__couple-bar">
                      <div class="ir__couple-fill ir__couple-fill--ec"
                           style:width="{d.ecSatisfaction * 100}%"></div>
                    </div>
                    <span class="ir__couple-pct">
                      <b>{ecPct}</b><em>%</em>
                    </span>
                  </div>
                  <div class="ir__couple-row">
                    <span class="ir__couple-tag ir__couple-tag--xe"
                          class:ir__couple-tag--starved={d.tripped &&
                              d.tripReason === IonTripReason.XeStarvation}>Xe</span>
                    <div class="ir__couple-bar">
                      <div class="ir__couple-fill ir__couple-fill--xe"
                           class:ir__couple-fill--starved={d.tripped &&
                               d.tripReason === IonTripReason.XeStarvation}
                           style:width="{d.xeSatisfaction * 100}%"></div>
                    </div>
                    <span class="ir__couple-pct"
                          class:ir__couple-pct--starved={d.tripped &&
                              d.tripReason === IonTripReason.XeStarvation}>
                      <b>{xePct}</b><em>%</em>
                    </span>
                  </div>
                </div>

                <div class="ir__tiles">
                  <div class="ir__tile ir__tile--power"
                       class:ir__tile--zero={d.currentEcW < 1}>
                    <span class="ir__tile-label">POWER</span>
                    <span class="ir__tile-value">
                      <span class="ir__tile-num">{powerFmt.mag}</span><span
                        class="ir__tile-unit">{powerFmt.unit}</span>
                    </span>
                  </div>
                  <div class="ir__tile ir__tile--throttle"
                       class:ir__tile--zero={d.throttle < 0.005}>
                    <span class="ir__tile-label">THROTTLE</span>
                    <span class="ir__tile-value">
                      <span class="ir__tile-num">{throttlePct}</span><span
                        class="ir__tile-unit">%</span>
                    </span>
                  </div>
                  <div class="ir__tile ir__tile--thermal"
                       class:ir__tile--zero={d.wasteHeatW < 1}>
                    <span class="ir__tile-label">WASTE ▸ REJECT</span>
                    <span class="ir__tile-value">
                      <span class="ir__tile-num">{wasteFmt.mag}</span><span
                        class="ir__tile-unit">{wasteFmt.unit}</span>
                      <span class="ir__tile-arrow" aria-hidden="true">▸</span>
                      <span class="ir__tile-num ir__tile-num--export"
                            class:ir__tile-num--export-short={d.rejectionW < d.wasteHeatW - 1}>{rejectFmt.mag}</span><span
                        class="ir__tile-unit">{rejectFmt.unit}</span>
                    </span>
                  </div>
                </div>
              </li>
            {/if}
          {/each}
        </ul>
  </Subheading>
  {/if}
</section>

<style>
  /* ═══════════════════════════════════════════════════════════════
     PROPULSION CONSOLE — vintage instrument cluster, three sub-
     sections each tuned to its engine family's narrative.
     ═══════════════════════════════════════════════════════════ */

  /* ── Section wrapper ─────────────────────────────────────────── */
  .prp {
    display: flex;
    flex-direction: column;
    gap: 6px;
  }
  /* Subheading summary chip — small bordered count badge that sits
     in the Subheading's right-aligned summary slot. */
  .prp__head-count {
    padding: 0 5px;
    color: var(--fg-mute);
    font-family: var(--font-mono);
    font-size: 10px;
    border: 1px solid var(--line);
    line-height: 1.4;
  }
  .prp__rows { list-style: none; margin: 0; padding: 0; }

  /* ── Chemical engine row ─────────────────────────────────────── */
  .ce {
    display: flex;
    align-items: center;
    gap: 6px;
    padding: 6px 10px 6px 14px;
    border-top: 1px solid var(--line);
    position: relative;
    --ce-zone: var(--fg-dim);
    --ce-zone-glow: transparent;
    min-width: 0;
  }
  .ce:first-child { border-top: 0; }
  .ce[data-zone="cool"]    { --ce-zone: var(--fg-dim); --ce-zone-glow: transparent; }
  .ce[data-zone="normal"]  { --ce-zone: var(--accent); --ce-zone-glow: var(--accent-glow); }
  .ce[data-zone="caution"] { --ce-zone: var(--warn);   --ce-zone-glow: var(--warn-glow); }
  .ce[data-zone="danger"]  { --ce-zone: var(--alert);  --ce-zone-glow: rgba(255, 82, 82, 0.55); }

  .ce::before {
    content: '';
    position: absolute;
    left: 0;
    top: 6px;
    bottom: 6px;
    width: 2px;
    background: var(--ce-zone);
    box-shadow: 0 0 6px var(--ce-zone-glow);
    opacity: 0.85;
    transition: background 240ms ease, box-shadow 240ms ease;
  }

  .ce__icon {
    flex: 0 0 auto;
    width: 12px;
    height: 12px;
    color: var(--ce-zone);
    filter: drop-shadow(0 0 3px var(--ce-zone-glow));
    transition: color 240ms ease, filter 240ms ease;
  }
  .ce--off .ce__icon { color: var(--fg-mute); filter: none; }
  .ce__name {
    flex: 1 1 auto;
    min-width: 0;
    color: var(--fg);
    font-family: var(--font-display);
    font-size: 11px;
    letter-spacing: 0.08em;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }
  .ce--off .ce__name {
    color: var(--fg-dim);
    font-style: italic;
  }
  .ce__state {
    flex: 0 0 auto;
    padding: 1px 5px;
    border: 1px solid currentColor;
    background: rgba(0, 0, 0, 0.28);
    font-family: var(--font-display);
    font-size: 9px;
    letter-spacing: 0.18em;
    line-height: 1.3;
  }
  .ce__state--cool    { color: var(--fg-mute); }
  .ce__state--normal  { color: var(--accent); }
  .ce__state--caution { color: var(--warn);   animation: prp-pulse 1.6s ease-in-out infinite; }
  .ce__state--danger  { color: var(--alert);  animation: prp-pulse 1.6s ease-in-out infinite; }

  .ce__thrust {
    flex: 0 0 auto;
    font-family: var(--font-mono);
    font-variant-numeric: tabular-nums;
    font-size: 11px;
    color: var(--fg-dim);
    white-space: nowrap;
  }
  .ce__thrust b {
    font-weight: 500;
    color: var(--fg);
  }
  .ce__thrust em {
    font-style: normal;
    color: var(--fg-mute);
    margin-left: 2px;
    font-size: 0.82em;
  }
  .ce__thrust-sep {
    color: var(--fg-mute);
    margin: 0 3px;
  }
  .ce[data-zone="normal"] .ce__thrust b {
    color: var(--accent);
    text-shadow: 0 0 3px var(--accent-glow);
  }
  .ce__thrust--zero b { color: var(--fg-mute); text-shadow: none; }

  /* ── Reactor card (lifted from NukView) ──────────────────────── */
  .rx {
    position: relative;
    display: flex;
    flex-direction: column;
    gap: 11px;
    padding: 10px 10px 12px 14px;
    border-top: 1px solid var(--line);
  }
  .rx:first-child { border-top: 0; }
  .rx { --rx-zone: var(--accent); --rx-zone-glow: var(--accent-glow); }
  .rx[data-zone="cool"]    { --rx-zone: var(--fg-dim); --rx-zone-glow: transparent; }
  .rx[data-zone="normal"]  { --rx-zone: var(--accent); --rx-zone-glow: var(--accent-glow); }
  .rx[data-zone="caution"] { --rx-zone: var(--warn);   --rx-zone-glow: var(--warn-glow); }
  .rx[data-zone="danger"]  { --rx-zone: var(--alert);  --rx-zone-glow: rgba(255, 82, 82, 0.55); }
  .rx::before {
    content: '';
    position: absolute;
    left: 0;
    top: 10px;
    bottom: 12px;
    width: 2px;
    background: var(--rx-zone);
    box-shadow: 0 0 6px var(--rx-zone-glow), 0 0 1px var(--rx-zone-glow);
    opacity: 0.9;
    transition: background 280ms ease, box-shadow 280ms ease;
  }
  .rx__head {
    display: flex;
    align-items: center;
    gap: 6px;
    min-width: 0;
  }
  .rx__head-mark {
    flex: 0 0 auto;
    color: var(--rx-zone);
    font-size: 10px;
    line-height: 1;
    text-shadow: 0 0 5px var(--rx-zone-glow);
    transition: color 280ms ease, text-shadow 280ms ease;
  }
  .rx__head-name {
    flex: 1 1 auto;
    min-width: 0;
    color: var(--fg);
    font-family: var(--font-display);
    font-size: 12px;
    letter-spacing: 0.10em;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }
  .rx__head-state {
    flex: 0 0 auto;
    padding: 1px 5px;
    border: 1px solid currentColor;
    color: var(--fg-mute);
    background: rgba(0, 0, 0, 0.28);
    font-family: var(--font-display);
    font-size: 9px;
    letter-spacing: 0.18em;
    line-height: 1.3;
    transition: color 220ms ease, border-color 220ms ease;
  }
  .rx__head-state--cool    { color: var(--fg-mute); }
  .rx__head-state--normal  { color: var(--accent); }
  .rx__head-state--caution { color: var(--warn); }
  .rx__head-state--danger  { color: var(--alert); animation: prp-pulse 1.6s ease-in-out infinite; }
  .rx__head-state--shutdown {
    color: var(--warn);
    border-style: dashed;
  }
  .rx__meter {
    display: flex;
    flex-direction: column;
    gap: 4px;
  }
  .rx__meter-row {
    display: flex;
    align-items: baseline;
    justify-content: space-between;
    gap: 8px;
    min-width: 0;
  }
  .rx__meter-label {
    flex: 0 0 auto;
    color: var(--fg-dim);
    font-family: var(--font-display);
    font-size: 9px;
    letter-spacing: 0.22em;
  }
  .rx__meter-value {
    flex: 0 1 auto;
    font-family: var(--font-mono);
    font-variant-numeric: tabular-nums;
    font-size: 12px;
    color: var(--fg);
    white-space: nowrap;
    transition: color 220ms ease, text-shadow 220ms ease;
  }
  .rx__meter-value em {
    font-style: normal;
    color: var(--fg-mute);
    margin-left: 2px;
    font-size: 0.82em;
  }
  .rx__meter-value--cool    { color: var(--fg-dim); }
  .rx__meter-value--normal  { color: var(--accent); text-shadow: 0 0 4px var(--accent-glow); }
  .rx__meter-value--caution { color: var(--warn);   text-shadow: 0 0 4px var(--warn-glow); }
  .rx__meter-value--danger  { color: var(--alert); animation: prp-pulse 1.6s ease-in-out infinite; }
  .rx__meter-row-pair {
    flex: 0 0 auto;
    display: inline-flex;
    align-items: baseline;
    font-family: var(--font-mono);
    font-variant-numeric: tabular-nums;
    font-size: 11px;
    white-space: nowrap;
  }
  .rx__meter-row-pair b { font-weight: 500; }
  .rx__throttle-tgt { color: var(--fg-dim); }
  .rx__throttle-tgt b { color: var(--fg); }
  .rx__throttle-sep { color: var(--fg-mute); margin: 0 4px; }
  .rx__throttle-act { color: var(--accent); }
  .rx__throttle-act b { color: var(--accent); }
  .rx__throttle-act--zero, .rx__throttle-act--zero b { color: var(--fg-mute); }
  .rx__meter-row-pair em {
    font-style: normal;
    color: var(--fg-mute);
    margin-left: 1px;
    font-size: 0.82em;
  }
  .rx__tempbar {
    position: relative;
    height: 14px;
    background: rgba(0, 0, 0, 0.55);
    border: 1px solid var(--line);
    box-shadow:
      inset 0 1px 0 rgba(0, 0, 0, 0.6),
      inset 0 -1px 0 rgba(255, 255, 255, 0.025);
    overflow: hidden;
  }
  .rx__tempbar::before {
    content: '';
    position: absolute;
    inset: 0;
    background: repeating-linear-gradient(
      90deg,
      transparent 0,
      transparent 4.5px,
      rgba(255, 255, 255, 0.035) 4.5px,
      rgba(255, 255, 255, 0.035) 5px
    );
    pointer-events: none;
    z-index: 1;
  }
  .rx__tempbar-zone {
    position: absolute;
    top: 0;
    bottom: 0;
    z-index: 0;
    opacity: 0.10;
    pointer-events: none;
  }
  .rx__tempbar-zone--cool    { background: var(--fg-dim); opacity: 0.06; }
  .rx__tempbar-zone--normal  { background: var(--accent); }
  .rx__tempbar-zone--caution { background: var(--warn); opacity: 0.16; }
  .rx__tempbar-zone--danger  { background: var(--alert); opacity: 0.22; }
  .rx__tempbar-fill {
    position: absolute;
    top: 0;
    bottom: 0;
    left: 0;
    z-index: 2;
    background: linear-gradient(180deg,
      color-mix(in srgb, var(--rx-zone) 80%, white 20%) 0%,
      var(--rx-zone) 55%,
      color-mix(in srgb, var(--rx-zone) 78%, black 22%) 100%);
    box-shadow:
      0 0 6px var(--rx-zone-glow),
      inset 0 1px 0 rgba(255, 255, 255, 0.18);
    transition: width 220ms cubic-bezier(0.4, 0, 0.2, 1),
                background 280ms ease,
                box-shadow 280ms ease;
  }
  .rx[data-zone="cool"] .rx__tempbar-fill {
    background: linear-gradient(180deg,
      color-mix(in srgb, var(--fg-dim) 65%, var(--fg) 35%) 0%,
      var(--fg-dim) 60%,
      color-mix(in srgb, var(--fg-dim) 70%, black 30%) 100%);
    box-shadow: none;
  }
  .rx__tempbar-needle {
    position: absolute;
    top: -2px;
    bottom: -2px;
    width: 0;
    z-index: 3;
    border-left: 1.5px solid var(--rx-zone);
    transform: translateX(-50%);
    box-shadow: 0 0 4px var(--rx-zone-glow);
    transition: left 220ms cubic-bezier(0.4, 0, 0.2, 1),
                border-color 280ms ease,
                box-shadow 280ms ease;
  }
  .rx__tempbar-ticks {
    position: relative;
    height: 22px;
    margin-top: 2px;
  }
  .rx__tick {
    position: absolute;
    top: 0;
    transform: translateX(-50%);
    display: flex;
    flex-direction: column;
    align-items: center;
    gap: 1px;
    line-height: 1;
  }
  .rx__tick-rule {
    width: 1px;
    height: 3px;
    background: var(--fg-mute);
  }
  .rx__tick-num {
    font-family: var(--font-mono);
    font-size: 9px;
    color: var(--fg-dim);
    font-variant-numeric: tabular-nums;
  }
  .rx__tick-text {
    font-family: var(--font-display);
    font-size: 8px;
    letter-spacing: 0.16em;
    color: var(--fg-mute);
    margin-top: 1px;
  }
  .rx__tick--danger .rx__tick-rule { background: var(--alert); }
  .rx__tick--danger .rx__tick-text { color: var(--alert); }
  .rx__tick--danger .rx__tick-num  { color: color-mix(in srgb, var(--alert) 65%, var(--fg-dim) 35%); }
  .rx__thrtbar {
    position: relative;
    height: 12px;
    background: rgba(0, 0, 0, 0.55);
    border: 1px solid var(--line);
    box-shadow: inset 0 1px 0 rgba(0, 0, 0, 0.6);
    overflow: visible;
    cursor: ew-resize;
    touch-action: none;
  }
  .rx__thrtbar:focus-visible {
    outline: 1px solid var(--accent);
    outline-offset: 2px;
  }
  .rx__thrtbar-min {
    position: absolute;
    left: 0;
    top: 0;
    bottom: 0;
    background: repeating-linear-gradient(
      45deg,
      transparent 0,
      transparent 3px,
      rgba(255, 255, 255, 0.045) 3px,
      rgba(255, 255, 255, 0.045) 4px
    );
    border-right: 1px dashed rgba(255, 255, 255, 0.12);
    pointer-events: none;
    z-index: 0;
  }
  .rx__thrtbar-fill {
    position: absolute;
    left: 0;
    top: 0;
    bottom: 0;
    z-index: 1;
    background: linear-gradient(180deg,
      color-mix(in srgb, var(--accent) 75%, white 25%) 0%,
      var(--accent) 60%,
      color-mix(in srgb, var(--accent) 78%, black 22%) 100%);
    box-shadow:
      0 0 5px var(--accent-glow),
      inset 0 1px 0 rgba(255, 255, 255, 0.25);
    transition: width 180ms linear, background 280ms ease;
  }
  .rx__thrtbar-fill--lagging {
    background: linear-gradient(180deg,
      color-mix(in srgb, var(--accent) 60%, var(--warn) 40%) 0%,
      color-mix(in srgb, var(--accent) 72%, var(--warn) 28%) 60%,
      color-mix(in srgb, var(--accent) 80%, black 20%) 100%);
  }
  .rx__thrtbar-fill--leading {
    background: linear-gradient(180deg,
      color-mix(in srgb, var(--accent) 60%, var(--info) 40%) 0%,
      color-mix(in srgb, var(--accent) 72%, var(--info) 28%) 60%,
      color-mix(in srgb, var(--accent) 80%, black 20%) 100%);
  }
  .rx__thrtbar-tgt {
    position: absolute;
    top: -5px;
    bottom: -1px;
    width: 0;
    z-index: 3;
    transform: translateX(-50%);
    transition: left 180ms linear, opacity 200ms ease;
  }
  .rx__thrtbar-tgt--dragging {
    transition: opacity 200ms ease;
  }
  .rx__thrtbar-tgt--dragging::before {
    border-top-color: var(--accent);
    filter: drop-shadow(0 0 4px var(--accent-glow));
  }
  .rx__thrtbar-tgt--dragging::after {
    background: var(--accent);
    opacity: 1;
  }
  .rx__thrtbar-tgt--hidden { opacity: 0; }
  .rx__thrtbar-tgt::before {
    content: '';
    position: absolute;
    left: -3.5px;
    top: 0;
    width: 0;
    height: 0;
    border-left: 3.5px solid transparent;
    border-right: 3.5px solid transparent;
    border-top: 4px solid var(--fg);
    filter: drop-shadow(0 0 2px rgba(226, 232, 242, 0.6));
  }
  .rx__thrtbar-tgt::after {
    content: '';
    position: absolute;
    left: -0.5px;
    top: 4px;
    bottom: 0;
    width: 1px;
    background: var(--fg);
    opacity: 0.7;
  }
  .rx__tiles {
    display: grid;
    grid-template-columns: repeat(3, 1fr);
    gap: 1px;
    background: var(--line);
    border: 1px solid var(--line);
    margin-top: 1px;
  }
  .rx__tile {
    display: flex;
    flex-direction: column;
    gap: 2px;
    padding: 6px 7px 7px;
    background: var(--bg);
    min-width: 0;
  }
  .rx__tile-label {
    color: var(--fg-mute);
    font-family: var(--font-display);
    font-size: 8px;
    letter-spacing: 0.20em;
    line-height: 1;
  }
  .rx__tile-value {
    display: flex;
    align-items: baseline;
    gap: 3px;
    margin-top: 2px;
    min-width: 0;
    white-space: nowrap;
  }
  .rx__tile-num {
    flex: 0 1 auto;
    font-family: var(--font-mono);
    font-size: 15px;
    font-weight: 500;
    color: var(--fg);
    font-variant-numeric: tabular-nums;
    line-height: 1;
    overflow: hidden;
    text-overflow: ellipsis;
  }
  .rx__tile-unit {
    flex: 0 0 auto;
    font-family: var(--font-mono);
    font-size: 10px;
    color: var(--fg-dim);
    line-height: 1;
  }
  .rx__tile--thermal .rx__tile-num { color: var(--info); }
  .rx__tile--flow .rx__tile-num    { color: var(--fg); }
  .rx__tile--thrust .rx__tile-num  {
    color: var(--warn);
    text-shadow: 0 0 4px var(--warn-glow);
  }
  .rx__tile--zero .rx__tile-num    {
    color: var(--fg-mute);
    text-shadow: none;
  }
  .rx__tile--zero .rx__tile-unit   { color: var(--fg-mute); }

  /* ── Ion drive card ──────────────────────────────────────────── */
  .ir {
    position: relative;
    display: flex;
    flex-direction: column;
    gap: 11px;
    padding: 10px 10px 12px 14px;
    border-top: 1px solid var(--line);
  }
  .ir:first-child { border-top: 0; }
  .ir { --ir-zone: var(--fg-dim); --ir-zone-glow: transparent; }
  .ir[data-zone="cool"]    { --ir-zone: var(--fg-dim); --ir-zone-glow: transparent; }
  .ir[data-zone="normal"]  { --ir-zone: var(--accent); --ir-zone-glow: var(--accent-glow); }
  .ir[data-zone="caution"] { --ir-zone: var(--warn);   --ir-zone-glow: var(--warn-glow); }
  .ir[data-zone="danger"]  { --ir-zone: var(--alert);  --ir-zone-glow: rgba(255, 82, 82, 0.55); }
  .ir--tripped { --ir-zone: var(--alert); --ir-zone-glow: rgba(255, 82, 82, 0.55); }
  .ir::before {
    content: '';
    position: absolute;
    left: 0;
    top: 10px;
    bottom: 12px;
    width: 2px;
    background: var(--ir-zone);
    box-shadow: 0 0 6px var(--ir-zone-glow), 0 0 1px var(--ir-zone-glow);
    opacity: 0.9;
    transition: background 280ms ease, box-shadow 280ms ease;
  }
  .ir--tripped::before {
    animation: ir-strip-pulse 1.8s ease-in-out infinite;
  }
  .ir__head {
    display: flex;
    align-items: center;
    gap: 6px;
    min-width: 0;
  }
  .ir__head-icon {
    flex: 0 0 auto;
    width: 12px;
    height: 12px;
    color: var(--ir-zone);
    filter: drop-shadow(0 0 4px var(--ir-zone-glow));
    transition: color 280ms ease, filter 280ms ease;
  }
  .ir__head-name {
    flex: 1 1 auto;
    min-width: 0;
    color: var(--fg);
    font-family: var(--font-display);
    font-size: 12px;
    letter-spacing: 0.10em;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }
  .ir__head-state {
    flex: 0 0 auto;
    padding: 1px 5px;
    border: 1px solid currentColor;
    color: var(--fg-mute);
    background: rgba(0, 0, 0, 0.28);
    font-family: var(--font-display);
    font-size: 9px;
    letter-spacing: 0.18em;
    line-height: 1.3;
    transition: color 220ms ease, border-color 220ms ease;
  }
  .ir__head-state--cool    { color: var(--fg-mute); }
  .ir__head-state--normal  { color: var(--accent); }
  .ir__head-state--caution { color: var(--warn); }
  .ir__head-state--danger  { color: var(--alert); }
  .ir__head-state--tripped {
    color: var(--alert);
    background: rgba(60, 12, 12, 0.42);
    animation: prp-pulse 1.4s ease-in-out infinite;
  }
  .ir__meter {
    display: flex;
    flex-direction: column;
    gap: 4px;
  }
  .ir__meter-row {
    display: flex;
    align-items: baseline;
    justify-content: space-between;
    gap: 8px;
    min-width: 0;
  }
  .ir__meter-label {
    flex: 0 0 auto;
    color: var(--fg-dim);
    font-family: var(--font-display);
    font-size: 9px;
    letter-spacing: 0.22em;
  }
  .ir__meter-value {
    flex: 0 1 auto;
    font-family: var(--font-mono);
    font-variant-numeric: tabular-nums;
    font-size: 12px;
    color: var(--fg);
    white-space: nowrap;
    transition: color 220ms ease, text-shadow 220ms ease;
  }
  .ir__meter-value em {
    font-style: normal;
    color: var(--fg-mute);
    margin-left: 2px;
    font-size: 0.82em;
  }
  .ir__meter-sep { color: var(--fg-mute); margin: 0 4px; }
  .ir__meter-cap { color: var(--fg-dim); }
  .ir__meter-value--cool    { color: var(--fg-dim); }
  .ir__meter-value--normal  { color: var(--accent); text-shadow: 0 0 4px var(--accent-glow); }
  .ir__meter-value--caution { color: var(--warn);   text-shadow: 0 0 4px var(--warn-glow); }
  .ir__meter-value--danger  { color: var(--alert); animation: prp-pulse 1.6s ease-in-out infinite; }
  .ir__tempbar {
    position: relative;
    height: 12px;
    background: rgba(0, 0, 0, 0.55);
    border: 1px solid var(--line);
    box-shadow:
      inset 0 1px 0 rgba(0, 0, 0, 0.6),
      inset 0 -1px 0 rgba(255, 255, 255, 0.025);
    overflow: hidden;
  }
  .ir__tempbar::before {
    content: '';
    position: absolute;
    inset: 0;
    background: repeating-linear-gradient(
      90deg,
      transparent 0,
      transparent 5.5px,
      rgba(255, 255, 255, 0.028) 5.5px,
      rgba(255, 255, 255, 0.028) 6px
    );
    pointer-events: none;
    z-index: 1;
  }
  .ir__tempbar-zone {
    position: absolute;
    top: 0;
    bottom: 0;
    z-index: 0;
    pointer-events: none;
  }
  .ir__tempbar-zone--normal  { background: var(--accent); opacity: 0.08; }
  .ir__tempbar-zone--caution { background: var(--warn);   opacity: 0.14; }
  .ir__tempbar-zone--danger  { background: var(--alert);  opacity: 0.20; }
  .ir__tempbar-fill {
    position: absolute;
    top: 0;
    bottom: 0;
    left: 0;
    z-index: 2;
    background: linear-gradient(180deg,
      color-mix(in srgb, var(--ir-zone) 80%, white 20%) 0%,
      var(--ir-zone) 55%,
      color-mix(in srgb, var(--ir-zone) 78%, black 22%) 100%);
    box-shadow:
      0 0 6px var(--ir-zone-glow),
      inset 0 1px 0 rgba(255, 255, 255, 0.18);
    transition: width 220ms cubic-bezier(0.4, 0, 0.2, 1),
                background 280ms ease,
                box-shadow 280ms ease;
  }
  .ir[data-zone="cool"] .ir__tempbar-fill {
    background: linear-gradient(180deg,
      color-mix(in srgb, var(--fg-dim) 65%, var(--fg) 35%) 0%,
      var(--fg-dim) 60%,
      color-mix(in srgb, var(--fg-dim) 70%, black 30%) 100%);
    box-shadow: none;
  }
  .ir__tempbar-trip {
    position: absolute;
    right: -1px;
    top: -2px;
    bottom: -2px;
    width: 0;
    z-index: 3;
    pointer-events: none;
  }
  .ir__tempbar-trip-rule {
    position: absolute;
    right: 0;
    top: 0;
    bottom: 0;
    width: 1.5px;
    background: var(--alert);
    box-shadow: 0 0 4px rgba(255, 82, 82, 0.65);
  }
  .ir__tempbar-foot {
    position: relative;
    height: 10px;
    margin-top: 2px;
  }
  .ir__tempbar-trip-label {
    position: absolute;
    right: 0;
    top: 0;
    transform: translateX(50%);
    font-family: var(--font-display);
    font-size: 8px;
    letter-spacing: 0.18em;
    color: var(--alert);
    line-height: 1;
  }
  .ir__couple {
    display: flex;
    flex-direction: column;
    gap: 4px;
  }
  .ir__couple-row {
    display: flex;
    align-items: center;
    gap: 6px;
    min-width: 0;
  }
  .ir__couple-tag {
    flex: 0 0 16px;
    text-align: right;
    font-family: var(--font-display);
    font-size: 9px;
    letter-spacing: 0.16em;
    line-height: 1;
  }
  .ir__couple-tag--ec { color: var(--info, #8ad4ff); }
  .ir__couple-tag--xe { color: var(--accent); }
  .ir__couple-tag--starved {
    color: var(--alert);
    animation: prp-pulse 1.4s ease-in-out infinite;
  }
  .ir__couple-bar {
    position: relative;
    flex: 1 1 auto;
    height: 8px;
    background: rgba(0, 0, 0, 0.55);
    border: 1px solid var(--line);
    overflow: hidden;
  }
  .ir__couple-fill {
    position: absolute;
    left: 0;
    top: 0;
    bottom: 0;
    transition: width 220ms cubic-bezier(0.4, 0, 0.2, 1);
  }
  .ir__couple-fill--ec {
    background: linear-gradient(180deg,
      color-mix(in srgb, var(--info, #8ad4ff) 70%, white 30%) 0%,
      var(--info, #8ad4ff) 60%,
      color-mix(in srgb, var(--info, #8ad4ff) 75%, black 25%) 100%);
    box-shadow: 0 0 4px rgba(138, 212, 255, 0.4);
  }
  .ir__couple-fill--xe {
    background: linear-gradient(180deg,
      color-mix(in srgb, var(--accent) 70%, white 30%) 0%,
      var(--accent) 60%,
      color-mix(in srgb, var(--accent) 75%, black 25%) 100%);
    box-shadow: 0 0 4px var(--accent-glow);
  }
  .ir__couple-fill--starved {
    background: rgba(60, 12, 12, 0.6);
    box-shadow: none;
  }
  .ir__couple--diverged .ir__couple-fill--xe {
    background: linear-gradient(180deg,
      color-mix(in srgb, var(--warn) 35%, var(--accent) 65%) 0%,
      color-mix(in srgb, var(--warn) 30%, var(--accent) 70%) 60%,
      color-mix(in srgb, var(--warn) 35%, black 25%) 100%);
    box-shadow: 0 0 5px var(--warn-glow);
  }
  .ir__couple-pct {
    flex: 0 0 38px;
    text-align: right;
    font-family: var(--font-mono);
    font-variant-numeric: tabular-nums;
    font-size: 10px;
    color: var(--fg-dim);
    line-height: 1;
  }
  .ir__couple-pct b {
    font-weight: 500;
    color: var(--fg);
  }
  .ir__couple-pct em {
    font-style: normal;
    color: var(--fg-mute);
    margin-left: 1px;
    font-size: 0.85em;
  }
  .ir__couple-pct--starved,
  .ir__couple-pct--starved b { color: var(--alert); }
  .ir__tiles {
    display: grid;
    grid-template-columns: 1fr 0.8fr 1.4fr;
    gap: 1px;
    background: var(--line);
    border: 1px solid var(--line);
    margin-top: 1px;
  }
  .ir__tile {
    display: flex;
    flex-direction: column;
    gap: 2px;
    padding: 6px 7px 7px;
    background: var(--bg);
    min-width: 0;
  }
  .ir__tile-label {
    color: var(--fg-mute);
    font-family: var(--font-display);
    font-size: 8px;
    letter-spacing: 0.20em;
    line-height: 1;
  }
  .ir__tile-value {
    display: flex;
    align-items: baseline;
    gap: 3px;
    margin-top: 2px;
    min-width: 0;
    white-space: nowrap;
  }
  .ir__tile-num {
    flex: 0 1 auto;
    font-family: var(--font-mono);
    font-size: 15px;
    font-weight: 500;
    color: var(--fg);
    font-variant-numeric: tabular-nums;
    line-height: 1;
    overflow: hidden;
    text-overflow: ellipsis;
  }
  .ir__tile-unit {
    flex: 0 0 auto;
    font-family: var(--font-mono);
    font-size: 10px;
    color: var(--fg-dim);
    line-height: 1;
  }
  .ir__tile-arrow {
    flex: 0 0 auto;
    font-size: 9px;
    color: var(--fg-mute);
    margin: 0 1px;
  }
  .ir__tile--power .ir__tile-num    {
    color: var(--info, #8ad4ff);
    text-shadow: 0 0 4px rgba(138, 212, 255, 0.4);
  }
  .ir__tile--throttle .ir__tile-num { color: var(--fg); }
  .ir__tile--thermal .ir__tile-num  {
    color: var(--warn);
    text-shadow: 0 0 4px var(--warn-glow);
    font-size: 13px;
  }
  .ir__tile-num--export {
    color: var(--accent);
    text-shadow: 0 0 4px var(--accent-glow);
  }
  .ir__tile-num--export-short {
    color: var(--alert);
    text-shadow: 0 0 4px rgba(255, 82, 82, 0.45);
    animation: prp-pulse 1.6s ease-in-out infinite;
  }
  .ir__tile--zero .ir__tile-num    {
    color: var(--fg-mute);
    text-shadow: none;
  }
  .ir__tile--zero .ir__tile-unit   { color: var(--fg-mute); }

  /* ── Shared animations ───────────────────────────────────────── */
  @keyframes prp-pulse {
    0%, 100% { opacity: 1; }
    50%      { opacity: 0.55; }
  }
  @keyframes ir-strip-pulse {
    0%, 100% {
      box-shadow:
        0 0 6px rgba(255, 82, 82, 0.55),
        0 0 1px rgba(255, 82, 82, 0.55);
    }
    50% {
      box-shadow:
        0 0 12px rgba(255, 82, 82, 0.85),
        0 0 3px rgba(255, 82, 82, 0.85);
    }
  }
</style>
