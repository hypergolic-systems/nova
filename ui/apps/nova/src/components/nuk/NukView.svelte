<script lang="ts">
  // ─────────────────────────────────────────────────────────────────
  //  FISSION CONSOLE
  //
  //  Vintage Project Rover / NERVA-style reactor instrument cluster.
  //  Each reactor renders as a stacked card: header + temp meter +
  //  throttle meter + a three-tile readout strip. Temperature is a
  //  zoned fuel-rod-style bar with calibrated tick marks; throttle is
  //  a single fill bar with a downward-triangle target marker so the
  //  slew (actual chases target at SlewRatePerSec) reads at a glance.
  //
  //  Source of truth: NuclearReactorState (ui/.../nova-topics.ts) and
  //  Nova.Core.Components.Propulsion.NuclearEngine on the C# side.
  //  Display constants are calibrated against
  //  configs/overrides/propulsion/liquidEngineLV-N.cfg.
  // ─────────────────────────────────────────────────────────────────

  import { useNovaParts } from '../../telemetry/use-nova-parts.svelte';
  import type { NovaPartHandle } from '../../telemetry/use-nova-parts.svelte';
  import { NovaPartTopic, ReactorState } from '../../telemetry/nova-topics';
  import { getKsp, useStageOps } from '@dragonglass/telemetry/svelte';
  import { onDestroy } from 'svelte';
  import { siPrefix, fmtMag } from '../../util/units';

  // LV-N display-level thresholds. Match configs/overrides/propulsion/
  // liquidEngineLV-N.cfg conceptually, but these are UI gauge breakpoints,
  // not physics — the C# side is the source of truth for behaviour, and
  // sends `thermalPowerW` on the wire so the UI never has to know the
  // IdlePowerW / MaxPowerW calibration.
  const IDLE_TEMP_K        = 1500;      // warmup target / idle steady-state
  const OPERATING_TEMP_K   = 2700;      // rated exhaust temp at full Throttled
  const MELTDOWN_TEMP_K    = 3100;      // hard limit (not yet enforced in sim)
  const TEMP_SCALE_K       = 3200;      // bar extends slightly past meltdown

  // Zone breakpoints as fractions of the bar's full width.
  const IDLE_FRAC      = IDLE_TEMP_K      / TEMP_SCALE_K;
  const OPERATING_FRAC = OPERATING_TEMP_K / TEMP_SCALE_K;
  const MELTDOWN_FRAC  = MELTDOWN_TEMP_K  / TEMP_SCALE_K;
  // Spool zone on the throttle bar — 0..SPOOL_END_FRAC is the
  // "warming up to operating temperature" regime where flow demand
  // lags reactor power, T_target lerps 1500 K → 2700 K, and Isp is
  // derated. Matches SpoolEndThrottle in the cfg / NuclearEngine.
  const SPOOL_END_FRAC = 0.25;

  // LH₂ liquid density (kg/L). Same value as Nova.Core.Resources.Resource
  // .LiquidHydrogen.Density — kept here as a UI-side constant so we can
  // convert the wire's kg/s flow into the L/s value players expect for
  // a cryogenic propellant readout.
  const LH2_KG_PER_L = 0.07;

  interface Props { vesselId: string }
  const { vesselId }: Props = $props();

  const vesselParts = useNovaParts(() => vesselId);
  const ksp = getKsp();

  let expanded = $state(true);
  function toggle(): void { expanded = !expanded; }

  function isNuclearPart(p: NovaPartHandle): boolean {
    return !!p.state && p.state.nuclear.length > 0;
  }
  function reactorOf(p: NovaPartHandle) { return p.state?.nuclear[0]; }

  const reactors = $derived.by((): NovaPartHandle[] =>
    vesselParts.current.filter(isNuclearPart));

  // Throttle-band breakpoints for the inferred UI badge inside the
  // Throttled state. Mirrors the SpoolEndThrottle alias above so the
  // bar-hatched region, state badge, and any future indicator stay
  // in sync without a second numeric to maintain.
  const SPOOL_END_THROTTLE = SPOOL_END_FRAC;
  // Below this throttle, the UI reads as IDLE (gameplay label for
  // "engine engaged but not pushing"). Mirrors the C# thrust-gating
  // threshold so the label flips at the same point thrust becomes
  // non-zero.
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
      // Legacy Idle (never set post-migration) — show same as
      // Throttled-at-zero so any stale frames render sanely.
      case ReactorState.Idle:    return 'IDLE';
      default: return '';
    }
  }

  // Anything past Cold reads as "running" from the player's POV —
  // the on/off button toggles a single op that the C# side then
  // routes into the right state machine transition (start /
  // shutdown / shutdown-while-throttled latched into
  // ShutdownRequested).
  function isReactorRunning(state: ReactorState, shutdownRequested: boolean): boolean {
    if (shutdownRequested) return false;
    return state === ReactorState.Warming
        || state === ReactorState.Throttled
        || state === ReactorState.Idle;  // legacy
  }
  function setReactorActive(partId: string, active: boolean): void {
    ksp.send(NovaPartTopic(partId), 'setReactorActive', active);
  }

  function setReactorPlayerThrottle(partId: string, frac: number): void {
    ksp.send(NovaPartTopic(partId), 'setReactorPlayerThrottle',
        Math.max(0, Math.min(1, frac)));
  }

  // Throttle-bar drag. While the player has the pointer down on a
  // reactor's throttle bar, we override the displayed TGT marker with
  // the cursor-driven value (immediate feedback, no wire round-trip
  // lag) and dispatch a setReactorPlayerThrottle op on every move.
  // Only one reactor can be dragged at a time, so a single state
  // tuple suffices.
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
  // Small margin above the upper boundaries so a reactor sitting at
  // exactly OperatingTempK (the throttled-state equilibrium) doesn't
  // flicker between 'normal' and 'caution' as FP-precision noise
  // pushes the core temp microscopically across 2700 K. The IDLE
  // boundary stays sharp at 1500 K — warmup completion is supposed
  // to look like a clean transition into the normal band.
  const ZONE_UPPER_MARGIN = 10;
  function tempZone(k: number): TempZone {
    if (k > MELTDOWN_TEMP_K + ZONE_UPPER_MARGIN)  return 'danger';
    if (k > OPERATING_TEMP_K + ZONE_UPPER_MARGIN) return 'caution';
    if (k >= IDLE_TEMP_K)                         return 'normal';
    return 'cool';
  }

  // Formatters tuned for cramped width — keep digits compact so the
  // tile values don't ellipsize at 90px wide.
  function fmtPower(w: number): { mag: string; unit: string } {
    const p = siPrefix(w);
    return { mag: fmtMag(w / p.div), unit: p.letter + 'W' };
  }
  // Volumetric flow in L/s for the player-facing readout (cryogenic
  // propellants show in volume, not mass, in every other Nova view).
  // Sub-bin / large-bin rules mirror the kN / W formatters so the
  // tile rhythm matches across the three metric tiles.
  function fmtFlowLs(kgs: number): string {
    const ls = kgs / LH2_KG_PER_L;
    if (ls < 0.05) return '0.0';
    if (ls < 10)   return ls.toFixed(1);
    return ls.toFixed(0);
  }
  function fmtThrust(kn: number): string {
    if (kn < 0.05) return '0.0';
    if (kn < 10)   return kn.toFixed(1);
    return kn.toFixed(0);
  }
  function fmtTempK(v: number): string {
    return Math.abs(v) >= 100 ? v.toFixed(0) : v.toFixed(1);
  }
  function fmtPct01(v: number): string {
    return Math.round(Math.max(0, Math.min(1, v)) * 100).toString();
  }

  const stageOps = useStageOps();
  function highlightOn(ids: readonly string[]): void { stageOps.setHighlightParts(ids); }
  function highlightOff(): void { stageOps.setHighlightParts([]); }
  onDestroy(() => stageOps.setHighlightParts([]));
</script>

<section class="nuk">
  <div class="nuk__node">
    <button type="button" class="nuk__node-head"
            aria-expanded={expanded}
            onclick={toggle}>
      <svg class="nuk__chev" class:nuk__chev--open={expanded}
           viewBox="0 0 8 8" aria-hidden="true">
        <path d="M2.2 1.4 L5.8 4 L2.2 6.6" fill="none" stroke="currentColor"
              stroke-width="1.25" stroke-linecap="round" stroke-linejoin="round" />
      </svg>
      <span class="nuk__node-title">REACTORS</span>
      {#if reactors.length > 0}
        <span class="nuk__node-count">{reactors.length}</span>
      {/if}
    </button>

    {#if expanded}
      {#if reactors.length === 0}
        <p class="nuk__empty">
          <span class="nuk__empty-rule"></span>
          <span class="nuk__empty-text">NO NUCLEAR HARDWARE</span>
          <span class="nuk__empty-rule"></span>
        </p>
      {:else}
        <ul class="nuk__rows">
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

                <!-- HEADER ─ name + state badge + power button ───── -->
                <header class="rx__head">
                  <span class="rx__head-mark" aria-hidden="true">⬢</span>
                  <span class="rx__head-name">{p.struct.title}</span>
                  <span class="rx__head-state rx__head-state--{zone}"
                        class:rx__head-state--shutdown={r.shutdownRequested}
                        title="Reactor state machine phase">
                    {stateLabel}{#if r.shutdownRequested}·SHDN{/if}
                  </span>
                  <button type="button"
                          class="rx__btn"
                          class:rx__btn--off={!running}
                          class:rx__btn--on={running}
                          aria-label={running ? 'Shut down reactor' : 'Start reactor'}
                          title={running
                            ? 'SHUTDOWN — auto-sequences from BURN through IDLE'
                            : 'STARTUP — Cold → Warming → Idle'}
                          onclick={() => setReactorActive(p.struct.id, !running)}>
                    <span class="rx__btn-glyph" aria-hidden="true">
                      {#if running}■{:else}▸{/if}
                    </span>
                    <span class="rx__btn-label">{running ? 'STOP' : 'START'}</span>
                  </button>
                </header>

                <!-- CORE TEMPERATURE ─ zoned fuel-rod bar with ticks ── -->
                <div class="rx__meter">
                  <div class="rx__meter-row">
                    <span class="rx__meter-label">CORE TEMP</span>
                    <span class="rx__meter-value rx__meter-value--{zone}">
                      {fmtTempK(r.coreTempK)}<em>K</em>
                    </span>
                  </div>

                  <div class="rx__tempbar">
                    <!-- Background zone rules — tinted regions so the
                         player sees IDLE/OPER/DANGER bands at rest. -->
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
                    <!-- Active fill (extends to current temp, colored
                         by the zone the temp lands in). -->
                    <div class="rx__tempbar-fill"
                         style:width="{tempPct}%"></div>
                    <!-- Needle: 1.5px vertical mark + small triangle. -->
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

                <!-- THROTTLE ─ actual fill + target marker ─────────── -->
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
                    <!-- Spool zone (0..SPOOL_END_FRAC) — hatched to mark
                         "warming up to operating temp" regime. Reactor
                         power and propellant flow scale through this
                         band while T target lerps 1500 K → 2700 K, so
                         the engine is on but Isp is derated. -->
                    <div class="rx__thrtbar-min"
                         style:width="{SPOOL_END_FRAC * 100}%"
                         aria-hidden="true"></div>
                    <!-- Actual fill. Tint shifts subtly warm when
                         spooling up, cool when spooling down. -->
                    <div class="rx__thrtbar-fill"
                         class:rx__thrtbar-fill--lagging={slewLagging}
                         class:rx__thrtbar-fill--leading={slewLeading}
                         style:width="{actFrac * 100}%"></div>
                    <!-- Target marker — downward triangle + tickline.
                         Hidden when target = 0 (idle / shutdown).
                         While the player drags the bar, the marker
                         follows the cursor immediately rather than
                         waiting for the wire round-trip. -->
                    <div class="rx__thrtbar-tgt"
                         class:rx__thrtbar-tgt--hidden={tgtDisplayFrac < 0.005}
                         class:rx__thrtbar-tgt--dragging={dragFrac !== null}
                         style:left="{tgtDisplayFrac * 100}%"
                         aria-hidden="true"></div>
                  </div>
                </div>

                <!-- READOUT TILES ─ THERMAL / FLOW / THRUST ────────── -->
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
      {/if}
    {/if}
  </div>
</section>

<style>
  /* ═══════════════════════════════════════════════════════════════
     FISSION CONSOLE — vintage NERVA-era reactor instrument cluster.

     Color choreography (zone-tracked):
       cool    → fg-mute / fg-dim    (below idle, no fission interest)
       normal  → accent (mint)        (idle through operating)
       caution → warn   (amber)       (operating → meltdown)
       danger  → alert  (red)         (past meltdown)

     The card-level `--rx-zone-*` custom properties are set from
     data-zone so every zone-driven element (left strip, needle,
     state badge, temp value) re-tints with one cascade hit.
     ═══════════════════════════════════════════════════════════ */

  /* ── Collapsible section wrapper (matches ThermalView). ── */
  .nuk {
    display: flex;
    flex-direction: column;
    gap: 6px;
  }
  .nuk__node {
    border: 1px solid var(--line);
    background: var(--bg-elev);
  }
  .nuk__node-head {
    display: flex;
    align-items: center;
    gap: 6px;
    width: 100%;
    padding: 4px 8px;
    background: transparent;
    border: 0;
    border-bottom: 1px solid var(--line);
    color: var(--fg-dim);
    font-family: var(--font-display);
    font-size: 11px;
    letter-spacing: 0.14em;
    cursor: pointer;
  }
  .nuk__node-title { flex: 1 1 auto; text-align: left; }
  .nuk__node-count {
    flex: 0 0 auto;
    padding: 0 5px;
    color: var(--fg-mute);
    font-family: var(--font-mono);
    font-size: 10px;
    border: 1px solid var(--line);
    line-height: 1.4;
  }
  .nuk__chev {
    width: 8px;
    height: 8px;
    flex: 0 0 auto;
    color: var(--fg-mute);
    transition: transform 160ms ease;
  }
  .nuk__chev--open { transform: rotate(90deg); }

  .nuk__rows { list-style: none; margin: 0; padding: 0; }

  .nuk__empty {
    display: flex;
    align-items: center;
    gap: 8px;
    margin: 0;
    padding: 10px;
    color: var(--fg-mute);
    font-family: var(--font-display);
    font-size: 10px;
    letter-spacing: 0.14em;
  }
  .nuk__empty-rule {
    flex: 1 1 auto;
    height: 1px;
    background: var(--line);
  }

  /* ── Reactor card ─────────────────────────────────────────── */
  .rx {
    position: relative;
    display: flex;
    flex-direction: column;
    gap: 11px;
    padding: 10px 10px 12px 14px;
    border-top: 1px solid var(--line);
  }
  .rx:first-child { border-top: 0; }

  /* Zone propagation — the card sets `--rx-zone` and `--rx-zone-glow`,
     children that need the active hue (left strip, head mark, state
     badge, temp value, needle) read off it. Default = mint dim
     accent so cards in a 'normal' zone read warm-mint. */
  .rx { --rx-zone: var(--accent); --rx-zone-glow: var(--accent-glow); }
  .rx[data-zone="cool"]    { --rx-zone: var(--fg-dim); --rx-zone-glow: transparent; }
  .rx[data-zone="normal"]  { --rx-zone: var(--accent); --rx-zone-glow: var(--accent-glow); }
  .rx[data-zone="caution"] { --rx-zone: var(--warn);   --rx-zone-glow: var(--warn-glow); }
  .rx[data-zone="danger"]  { --rx-zone: var(--alert);  --rx-zone-glow: rgba(255, 82, 82, 0.55); }

  /* Left-edge accent strip — phosphor-tinted by zone so a glance
     down the panel tells you which reactor is hot. */
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

  /* ── Header ───────────────────────────────────────────────── */
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
  .rx__head-state--danger  { color: var(--alert); animation: rx-pulse 1.6s ease-in-out infinite; }
  .rx__head-state--shutdown {
    color: var(--warn);
    border-style: dashed;
  }

  /* Power button — vintage rocker switch feel. Mint when off (ready
     to start), amber when on (ready to stop). Black face contrasts
     against the panel's elev background so the switch reads as a
     physical control sitting proud of the surface. */
  .rx__btn {
    flex: 0 0 auto;
    display: flex;
    align-items: center;
    gap: 4px;
    padding: 2px 6px 2px 5px;
    background: var(--bg);
    border: 1px solid var(--line);
    color: var(--fg-dim);
    font-family: var(--font-display);
    font-size: 9px;
    letter-spacing: 0.16em;
    cursor: pointer;
    transition:
      color 160ms ease,
      border-color 160ms ease,
      background 160ms ease,
      box-shadow 160ms ease;
  }
  .rx__btn-glyph {
    font-family: var(--font-mono);
    font-size: 10px;
    line-height: 1;
  }
  .rx__btn-label { line-height: 1; }
  .rx__btn--off {
    color: var(--accent);
    border-color: var(--accent-dim);
  }
  .rx__btn--off:hover {
    color: var(--accent-soft);
    border-color: var(--accent);
    background: rgba(126, 245, 184, 0.08);
    box-shadow: 0 0 6px var(--accent-glow);
  }
  .rx__btn--on {
    color: var(--warn);
    border-color: rgba(240, 180, 41, 0.45);
  }
  .rx__btn--on:hover {
    color: #ffd070;
    border-color: var(--warn);
    background: rgba(240, 180, 41, 0.10);
    box-shadow: 0 0 6px var(--warn-glow);
  }
  .rx__btn:active { transform: translateY(1px); }

  /* ── Meter blocks (shared shell for temp + throttle) ────── */
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
  .rx__meter-value--danger  { color: var(--alert); animation: rx-pulse 1.6s ease-in-out infinite; }

  .rx__meter-row-pair {
    flex: 0 0 auto;
    display: inline-flex;
    align-items: baseline;
    font-family: var(--font-mono);
    font-variant-numeric: tabular-nums;
    font-size: 11px;
    white-space: nowrap;
  }
  .rx__meter-row-pair b {
    font-weight: 500;
  }
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

  /* ── Temperature bar (fuel rod channel) ──────────────────── */
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
  /* Striations — vertical hairlines every 5px suggest fuel-rod
     cladding. Sit under the zone tints + fill so they read as a
     subtle texture, not a foreground pattern. */
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
  /* Cool zone gets a much dimmer fill — no glow, just a desaturated
     reference so the player knows the reading is real but cold. */
  .rx[data-zone="cool"] .rx__tempbar-fill {
    background: linear-gradient(180deg,
      color-mix(in srgb, var(--fg-dim) 65%, var(--fg) 35%) 0%,
      var(--fg-dim) 60%,
      color-mix(in srgb, var(--fg-dim) 70%, black 30%) 100%);
    box-shadow: none;
  }

  /* Needle marker — vertical line sitting on top of the fill,
     aligned to its right edge. The line glows the zone color so the
     "current position" reads even when the fill width is 0 (e.g.
     coreTempK == 0 in cold). */
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

  /* Tick row sits below the bar. Each tick is absolutely positioned
     and translateX(-50%)'d so its rule + numeric stack centers on
     the target percentage. The 0K tick has no text label — it's the
     origin and reads as such. */
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

  /* ── Throttle bar ─────────────────────────────────────────── */
  .rx__thrtbar {
    position: relative;
    height: 12px;
    background: rgba(0, 0, 0, 0.55);
    border: 1px solid var(--line);
    box-shadow: inset 0 1px 0 rgba(0, 0, 0, 0.6);
    overflow: visible;
    cursor: ew-resize;
    touch-action: none;  /* prevent scroll gestures from eating drag */
  }
  .rx__thrtbar:focus-visible {
    outline: 1px solid var(--accent);
    outline-offset: 2px;
  }
  /* Spool-zone hatched band — visible cue that throttling into this
     range engages the reactor at derated Isp. */
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
  /* Slew direction tinting. Lagging = spooling up (target > actual),
     leading = spooling down (target < actual). Subtle warm/cool
     wash on the fill so the player can FEEL the slew at a glance. */
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
  /* Target marker — downward triangle above + 1px tickline through
     the bar. The container itself has zero width and is just a
     positioning anchor for the two pseudos. */
  .rx__thrtbar-tgt {
    position: absolute;
    top: -5px;
    bottom: -1px;
    width: 0;
    z-index: 3;
    transform: translateX(-50%);
    transition: left 180ms linear, opacity 200ms ease;
  }
  /* While the player is actively dragging, the marker should snap
     to the cursor (no easing) so the on-screen position visibly
     tracks the gesture without smear. */
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

  /* ── Readout tiles ─────────────────────────────────────────── */
  /* Three-column instrument cluster. 1px gaps generate hairline
     dividers via the parent's background showing through. Each tile
     has a small uppercase label above and a big mono numeric below
     with a dim unit suffix. */
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
  /* Thermal in info-blue (it's energy, distinct from thrust's amber);
     flow neutral; thrust amber-when-active (it's the engine doing
     the engine thing — that should pop). */
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

  /* ── Shared animation ─────────────────────────────────────── */
  @keyframes rx-pulse {
    0%, 100% { opacity: 1; }
    50%      { opacity: 0.55; }
  }
</style>
