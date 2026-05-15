<script lang="ts">
  // Replaces KSP's stock `PartListTooltip`. Subscribes to the singleton
  // `NovaPartInfoTopic`; when the C# Harmony patch on
  // `PartListTooltipController.OnPointerEnter` fires, the topic emits a
  // populated frame and the popup opens at the supplied anchor.
  // Mouseout → empty frame → popup vanishes.
  //
  // Layout:
  //   Header — thumbnail slot (reserved for Dragonglass-rendered 3-D
  //            view, see follow-up) · title · manufacturer · mass · cost
  //   Body   — description paragraph
  //   Stack  — one group per Nova component kind. Group order is fixed
  //            so a part with multiple kinds always reads the same way
  //            (propulsion → power → control → structural → science).
  //
  // Smart placement: the C# patch sends raw browser-space cursor coords;
  // the UI measures the popup after mount and flips horizontally / clamps
  // vertically if the natural placement would clip. `pointer-events:
  // none` keeps the cursor "owned" by the underlying icon, so the popup
  // can't accidentally trap focus while the user is scanning the list.

  import { onDestroy } from 'svelte';
  import { getKsp } from '@dragonglass/telemetry/svelte';
  import {
    NovaPartInfoTopic,
    decodePartInfo,
    InsulationTier,
    type NovaPartInfo,
    type PropellantSpec,
  } from '../../telemetry/nova-topics';
  import { resourceMeta } from '../resource/resource-codes';
  import { fmtMag, fmtBytes, fmtDuration, siPrefix } from '../../util/units';

  const ksp = getKsp();
  let info = $state<NovaPartInfo | null>(null);

  const unsub = ksp.subscribe(NovaPartInfoTopic, (frame) => {
    info = decodePartInfo(frame);
  });
  onDestroy(unsub);

  // ----- Smart placement -----------------------------------------

  const ANCHOR_OFFSET_X = 22;   // gap between cursor and popup edge
  const ANCHOR_OFFSET_Y = -8;   // small upward bias so the popup head
                                // sits roughly even with the icon
  const VIEWPORT_MARGIN = 12;

  let popupEl: HTMLDivElement | null = $state(null);
  let placed = $state<{ x: number; y: number; flipped: boolean } | null>(null);

  // Re-place whenever the popup mounts or the anchor changes. The
  // measure → flip flow runs after the popup has rendered with its
  // natural intrinsic size; on the very first paint we hide the popup
  // with `placed === null` so it doesn't flash at the unmeasured
  // position.
  $effect(() => {
    if (!info || !popupEl) {
      placed = null;
      return;
    }
    const ax = info.anchorX;
    const ay = info.anchorY;
    // Force a layout read.
    const rect = popupEl.getBoundingClientRect();
    const vw = window.innerWidth;
    const vh = window.innerHeight;

    let x = ax + ANCHOR_OFFSET_X;
    let flipped = false;
    if (x + rect.width + VIEWPORT_MARGIN > vw) {
      x = ax - ANCHOR_OFFSET_X - rect.width;
      flipped = true;
    }
    x = Math.max(VIEWPORT_MARGIN, x);

    let y = ay + ANCHOR_OFFSET_Y;
    if (y + rect.height + VIEWPORT_MARGIN > vh) {
      y = vh - rect.height - VIEWPORT_MARGIN;
    }
    y = Math.max(VIEWPORT_MARGIN, y);

    placed = { x, y, flipped };
  });

  // ----- Formatting helpers --------------------------------------

  // Mass: tonnes for ≥1 kg ⋅ 1000 (i.e. ≥1 t), kg otherwise. Tonnes
  // get 2-decimal precision under 100 t, 1 decimal under 1000 t, none
  // beyond. Sub-kg edge case (massless decorative parts) collapses to
  // "—" so we don't render "0.000 kg" for a flag.
  function fmtMass(kg: number): string {
    if (!Number.isFinite(kg) || kg <= 0) return '—';
    if (kg >= 1000) {
      const t = kg / 1000;
      if (t >= 100) return `${t.toFixed(0)} t`;
      if (t >= 10)  return `${t.toFixed(1)} t`;
      return `${t.toFixed(2)} t`;
    }
    return `${Math.round(kg)} kg`;
  }

  // Funds: KSP-style integer with thousand separators. The funds glyph
  // (₣) is appended as a small unit, matching how other Nova readouts
  // separate magnitude from unit.
  function fmtFunds(funds: number): string {
    if (!Number.isFinite(funds) || funds < 0) return '—';
    return Math.round(funds).toLocaleString('en-US');
  }

  // Watts / Joules with SI prefix from `units.ts`. Picks the prefix off
  // the value's own magnitude rather than a reference pair — the popup
  // shows isolated specs, not paired stored/cap values.
  function fmtPower(w: number): string {
    if (!Number.isFinite(w) || w === 0) return '0 W';
    const p = siPrefix(w);
    return `${fmtMag(w / p.div)} ${p.letter}W`;
  }
  function fmtEnergy(j: number): string {
    if (!Number.isFinite(j) || j === 0) return '0 J';
    const p = siPrefix(j);
    return `${fmtMag(j / p.div)} ${p.letter}J`;
  }
  function fmtRate(bps: number): string {
    if (!Number.isFinite(bps) || bps <= 0) return '0 B/s';
    return `${fmtBytes(bps)}/s`;
  }
  // Litres: keep raw to 3 figs at the upper end so tank readouts ("14 400 L"
  // for a 14.4 m³ slice) read naturally. SI-prefix only past kL.
  function fmtVolume(litres: number): string {
    if (!Number.isFinite(litres) || litres <= 0) return '0 L';
    if (litres >= 1_000_000) return `${fmtMag(litres / 1_000_000)} ML`;
    if (litres >= 1000)      return `${litres.toFixed(0).replace(/\B(?=(\d{3})+(?!\d))/g, ' ')} L`;
    return `${litres.toFixed(0)} L`;
  }
  function fmtDistance(m: number): string {
    if (!Number.isFinite(m) || m <= 0) return '—';
    if (m >= 1_000_000_000) return `${fmtMag(m / 1e9)} Gm`;
    if (m >= 1_000_000)     return `${fmtMag(m / 1e6)} Mm`;
    if (m >= 1000)          return `${fmtMag(m / 1000)} km`;
    return `${Math.round(m)} m`;
  }

  const TIER_LABEL: Record<InsulationTier, string> = {
    [InsulationTier.MLI]:      'MLI',
    [InsulationTier.HeavyMLI]: 'MLI+',
    [InsulationTier.BAC]:      'BAC',
    [InsulationTier.ZBO]:      'ZBO',
  };

  // RTG decay decimation per Kerbin year: how many ten-day-step decays
  // (0.1% per step on a 32 032-day half-life baseline) fit in a year.
  // Closed-form (1 − stepDrop)^stepsPerYear shows the player how much
  // their EOL power slips per Kerbin year without forcing them to do
  // the half-life math.
  function rtgEolFraction(halfLifeDays: number): number {
    if (!Number.isFinite(halfLifeDays) || halfLifeDays <= 0) return 0;
    const stepDays = halfLifeDays * Math.log(1 - 0.001) / Math.log(0.5);
    const stepsPerKerbinYear = (426 * 6 * 3600) / 86400 / Math.abs(stepDays);
    return Math.pow(1 - 0.001, stepsPerKerbinYear);
  }
</script>

{#if info}
  <div
    bind:this={popupEl}
    class="pip"
    class:pip--flipped={placed?.flipped}
    class:pip--placed={placed !== null}
    style:left="{(placed?.x ?? -9999)}px"
    style:top="{(placed?.y ?? -9999)}px"
    role="tooltip"
    aria-live="polite"
  >
    <div class="pip__rule pip__rule--head" aria-hidden="true"></div>

    <!-- HEADER : thumbnail slot · title · manufacturer · mass · cost -->
    <header class="pip__head">
      <div class="pip__thumb" aria-hidden="true">
        <svg viewBox="0 0 64 64" preserveAspectRatio="xMidYMid meet">
          <!-- Crosshair grid: a marker for the reserved 3-D thumbnail
               slot. Reads as "instrument viewport pending art" rather
               than a generic empty box. -->
          <rect x="0.5" y="0.5" width="63" height="63"
                fill="none" stroke="var(--line-bright)" stroke-width="1" />
          <line x1="0" y1="32" x2="64" y2="32"
                stroke="var(--line)" stroke-width="0.5" />
          <line x1="32" y1="0" x2="32" y2="64"
                stroke="var(--line)" stroke-width="0.5" />
          <circle cx="32" cy="32" r="8"
                  fill="none" stroke="var(--line-accent)" stroke-width="0.75"
                  stroke-dasharray="2 1.5" opacity="0.55" />
          <text x="32" y="35" text-anchor="middle"
                font-family="var(--font-display)" font-size="9"
                letter-spacing="0.15em" fill="var(--fg-mute)">3D</text>
        </svg>
      </div>

      <div class="pip__head-text">
        <h2 class="pip__title">{info.title}</h2>
        <div class="pip__manuf">{info.manufacturer || '—'}</div>
        <div class="pip__head-stats">
          <span class="pip__stat">
            <span class="pip__stat-label">DRY</span>
            <span class="pip__stat-value">{fmtMass(info.dryMassKg)}</span>
          </span>
          <span class="pip__stat-sep" aria-hidden="true"></span>
          <span class="pip__stat">
            <span class="pip__stat-label">COST</span>
            <span class="pip__stat-value pip__stat-value--funds">
              {fmtFunds(info.costFunds)}<em>₣</em>
            </span>
          </span>
        </div>
      </div>
    </header>

    {#if info.description}
      <p class="pip__desc">{info.description}</p>
    {/if}

    <!-- COMPONENT GROUPS -------------------------------------------- -->
    <div class="pip__groups">
      {#each info.engine as e, i (i)}
        {@render group('E', 'ENGINE', null)}
        <div class="pip__grid">
          {@render kv('THRUST', `${fmtMag(e.thrustKn)} kN`)}
          {@render kv('ISP',    `${fmtMag(e.ispS)} s`)}
          {#if e.gimbalDeg > 0}
            {@render kv('GIMBAL', `±${fmtMag(e.gimbalDeg)}°`)}
          {/if}
          {@render kv('FLOW',
            `${fmtMag(e.thrustKn * 1000 / (e.ispS * 9.80665))} kg/s`)}
        </div>
        {@render propellants(e.propellants)}
      {/each}

      {#each info.nuclear as n, i (i)}
        {@render group('N', 'NUCLEAR', null)}
        <div class="pip__grid">
          {@render kv('THRUST', `${fmtMag(n.thrustKn)} kN`)}
          {@render kv('ISP',    `${fmtMag(n.ispS)} s`)}
          {@render kv('IDLE T', `${fmtMag(n.idleTempK)} K`)}
          {@render kv('OP T',   `${fmtMag(n.opTempK)} K`)}
          {@render kv('PWR',    `${fmtPower(n.idlePowerW)} → ${fmtPower(n.maxPowerW)}`)}
          {@render kv('WARMUP', fmtDuration(n.warmupSec))}
        </div>
        {@render propellants(n.propellants)}
      {/each}

      {#each info.rcs as r, i (i)}
        {@render group('M', 'RCS', `${r.thrusterCount}× nozzle`)}
        <div class="pip__grid">
          {@render kv('THRUST', `${fmtMag(r.thrusterPowerKn)} kN ea`)}
          {@render kv('TOTAL',  `${fmtMag(r.thrusterPowerKn * r.thrusterCount)} kN`)}
          {@render kv('ISP',    `${fmtMag(r.ispS)} s`)}
        </div>
        {@render propellants(r.propellants)}
      {/each}

      {#each info.tank as t, i (i)}
        {@render group('T', 'TANK', `${fmtVolume(t.volumeL)} · ${fmtMag(t.maxRateLps)} L/s`)}
        <ul class="pip__slices">
          {#each t.slices as s (`${s.resource}-${i}`)}
            {@const meta = resourceMeta(s.resource)}
            <li class="pip__slice"
                style:--slice-color={meta.color}
                style:--slice-tint={meta.tint}>
              <span class="pip__slice-code">{meta.code}</span>
              <span class="pip__slice-cap">{fmtVolume(s.capacityL)}</span>
              <span class="pip__slice-tier">{TIER_LABEL[s.tier]}</span>
            </li>
          {/each}
        </ul>
      {/each}

      {#each info.battery as b, i (i)}
        {@render group('B', 'BATTERY', null)}
        <div class="pip__grid">
          {@render kv('CAPACITY', fmtEnergy(b.capacityJ))}
          {@render kv('RATE',     `${fmtPower(b.maxRateW)} ⇋`)}
        </div>
      {/each}

      {#each info.fuelCell as f, i (i)}
        {@render group('F', 'FUEL CELL', null)}
        <div class="pip__grid">
          {@render kv('OUTPUT', fmtPower(f.maxOutputW))}
        </div>
        {@render propellants(f.propellants)}
      {/each}

      {#each info.solar as s, i (i)}
        {@render group('S', 'SOLAR',
          s.isTracking ? 'tracking' : (s.isDeployable ? 'deployable' : 'fixed'))}
        <div class="pip__grid">
          {@render kv('OPTIMAL', `${fmtPower(s.chargeRateW)} @ 1AU`)}
        </div>
      {/each}

      {#each info.rtg as r, i (i)}
        {@const eolYr = rtgEolFraction(r.halfLifeDays)}
        {@render group('R', 'RTG', null)}
        <div class="pip__grid">
          {@render kv('BOL',      fmtPower(r.referencePowerW))}
          {@render kv('EOL Y+1',  `${(eolYr * 100).toFixed(1)}%`)}
          {@render kv('HALF-LIFE', `${fmtMag(r.halfLifeDays / 365.25)} yr`)}
          {@render kv('WASTE',    fmtPower(r.thermalOutputW))}
          {@render kv('MAX T',    `${r.maxOpTempC}°C`)}
          {@render kv('REJECT',   `${fmtPower(r.vacuumRejectionW)} ‧ ${fmtPower(r.atmRejectionW)}`)}
        </div>
      {/each}

      {#each info.wheel as w, i (i)}
        {@render group('W', 'REACTION WHEEL', null)}
        <div class="pip__grid">
          {@render kv('PITCH',  `${fmtMag(w.pitchTorqueKnm)} kN·m`)}
          {@render kv('YAW',    `${fmtMag(w.yawTorqueKnm)} kN·m`)}
          {@render kv('ROLL',   `${fmtMag(w.rollTorqueKnm)} kN·m`)}
          {@render kv('EC',     `${fmtPower(w.electricRateW)} /int`)}
        </div>
      {/each}

      {#each info.radiator as x, i (i)}
        {@render group('X', 'RADIATOR', x.isDeployable ? 'deployable' : 'fixed')}
        <div class="pip__grid">
          {@render kv('VAC',  fmtPower(x.vacuumCoolingW))}
          {@render kv('ATM',  fmtPower(x.atmCoolingW))}
          {#if x.ecPerWattCooling > 0}
            {@render kv('PUMP', `${fmtPower(x.ecPerWattCooling * x.vacuumCoolingW)}`)}
          {:else}
            {@render kv('PUMP', 'passive')}
          {/if}
        </div>
      {/each}

      {#each info.antenna as a, i (i)}
        {@render group('A', 'ANTENNA', null)}
        <div class="pip__grid">
          {@render kv('TX',      fmtPower(a.txPowerW))}
          {@render kv('GAIN',    `${fmtMag(a.gain)}×`)}
          {@render kv('MAX',     fmtRate(a.maxRateBps))}
          {@render kv('REF',     fmtDistance(a.refDistanceM))}
        </div>
      {/each}

      {#each info.command as c, i (i)}
        {@render group('C', 'COMMAND POD', c.crewCapacity > 0 ? `${c.crewCapacity} crew` : null)}
        <div class="pip__grid">
          {@render kv('IDLE',  fmtPower(c.idleDrawW))}
          {#if c.testLoadRateW > 0}
            {@render kv('TEST', `${fmtPower(c.testLoadRateW)} ⌃`)}
          {/if}
        </div>
      {/each}

      {#each info.probe as p, i (i)}
        {@render group('P', 'PROBE CORE', `SAS lv ${p.sasLevel}`)}
        <div class="pip__grid">
          {@render kv('IDLE',     fmtPower(p.idleDrawW))}
          {@render kv('CMD CAP',  fmtBytes(p.commandCapBytes))}
          {@render kv('DECAY',    fmtRate(p.commandDecayBps))}
          {@render kv('RECEIVE',  fmtRate(p.commandReceiveBps))}
          {@render kv('INPUT',    `${fmtRate(p.inputCostBps)} /unit`)}
          {#if p.testLoadRateW > 0}
            {@render kv('TEST',   `${fmtPower(p.testLoadRateW)} ⌃`)}
          {/if}
        </div>
      {/each}

      {#each info.decoupler as d, i (i)}
        {@render group('D', 'DECOUPLER', d.canFullSeparate ? null : 'radial')}
        <div class="pip__grid">
          {@render kv('FORCE', `${fmtMag(d.ejectionForceKn)} kN`)}
        </div>
        {#if d.allowedResources.length > 0}
          <div class="pip__crossfeed">
            <span class="pip__crossfeed-label">CROSSFEED</span>
            <span class="pip__crossfeed-list">
              {#each d.allowedResources as r, j (j)}
                {@const meta = resourceMeta(r)}
                <span class="pip__crossfeed-chip"
                      style:--slice-color={meta.color}
                      style:--slice-tint={meta.tint}>{meta.code}</span>
              {/each}
            </span>
          </div>
        {/if}
      {/each}

      {#each info.docking as k, i (i)}
        {@render group('K', 'DOCKING PORT', `size ${k.sizeIndex}`)}
      {/each}

      {#each info.crew as c, i (i)}
        {@render group('Y', 'CABIN', `${c.crewCapacity} crew`)}
      {/each}

      {#each info.storage as z, i (i)}
        {@render group('Z', 'DATA STORAGE', null)}
        <div class="pip__grid">
          {@render kv('CAPACITY', fmtBytes(z.capacityBytes))}
        </div>
      {/each}

      {#each info.thermometer as h, i (i)}
        {@render group('H', 'INSTRUMENT', h.instrumentName)}
      {/each}

      {#each info.light as l, i (i)}
        {@render group('L', 'LIGHT', null)}
        <div class="pip__grid">
          {@render kv('DRAW', fmtPower(l.drawW))}
        </div>
      {/each}
    </div>

    {#snippet group(kind: string, label: string, hint: string | null)}
      <div class="pip__group-head">
        <span class="pip__mono">{kind}</span>
        <span class="pip__group-label">{label}</span>
        {#if hint}
          <span class="pip__group-hint">{hint}</span>
        {/if}
      </div>
    {/snippet}

    {#snippet kv(label: string, value: string)}
      <div class="pip__cell">
        <span class="pip__cell-label">{label}</span>
        <span class="pip__cell-value">{value}</span>
      </div>
    {/snippet}

    {#snippet propellants(p: PropellantSpec[])}
      {#if p.length > 0}
        <div class="pip__prop">
          <span class="pip__prop-label">PROPELLANT</span>
          <span class="pip__prop-list">
            {#each p as e, i (i)}
              {@const meta = resourceMeta(e.resource)}
              <span class="pip__prop-chip"
                    style:--slice-color={meta.color}
                    style:--slice-tint={meta.tint}>
                <em>{e.ratio}×</em>{meta.code}
              </span>
              {#if i < p.length - 1}
                <span class="pip__prop-plus" aria-hidden="true">+</span>
              {/if}
            {/each}
          </span>
        </div>
      {/if}
    {/snippet}
  </div>
{/if}

<style>
  /* Outer shell. Width-locked at 340px so component groups read with
     consistent column widths — the popup is a spec-sheet, not a
     fluid window. */
  .pip {
    position: fixed;
    width: 340px;
    z-index: 1200;
    pointer-events: none;
    opacity: 0;
    transform: translateY(-4px);
    background: var(--bg-panel-strong);
    border: 1px solid var(--line-accent);
    box-shadow:
      0 0 22px rgba(126, 245, 184, 0.06),
      inset 0 0 0 1px rgba(126, 245, 184, 0.045),
      0 14px 32px rgba(0, 0, 0, 0.55);
    color: var(--fg);
    font-family: var(--font-mono);
    font-size: 11px;
    letter-spacing: 0.02em;
    backdrop-filter: blur(14px) saturate(125%);
    -webkit-backdrop-filter: blur(14px) saturate(125%);
    transition: opacity 160ms cubic-bezier(0.2, 0.8, 0.25, 1),
                transform 200ms cubic-bezier(0.16, 1, 0.3, 1);
  }
  .pip--placed {
    opacity: 1;
    transform: translateY(0);
  }
  /* Reading-edge stripe — left edge by default, right when the popup
     has flipped to the cursor's left side. Sits on the side closest to
     the part icon so the eye is led from icon → readout. */
  .pip::before {
    content: '';
    position: absolute;
    top: 0;
    bottom: 0;
    left: 0;
    width: 2px;
    background: var(--accent);
    box-shadow: 0 0 8px var(--accent-glow);
    opacity: 0.65;
  }
  .pip--flipped::before {
    left: auto;
    right: 0;
  }
  /* Decorative head rule with a notch — small "this is a readout, not a
     window" marker. */
  .pip__rule--head {
    height: 18px;
    border-bottom: 1px solid var(--line);
    background:
      linear-gradient(90deg,
        transparent 0,
        rgba(126, 245, 184, 0.05) 50%,
        transparent 100%);
    position: relative;
  }
  .pip__rule--head::after {
    content: '◇';
    position: absolute;
    top: 50%;
    right: 8px;
    transform: translateY(-50%);
    color: var(--accent);
    font-family: var(--font-display);
    font-size: 9px;
    text-shadow: 0 0 4px var(--accent-glow);
    letter-spacing: 0.2em;
  }

  /* HEADER ----------------------------------------------------------- */
  .pip__head {
    display: grid;
    grid-template-columns: 64px 1fr;
    gap: 12px;
    padding: 12px 14px 10px;
  }
  .pip__thumb {
    width: 64px;
    height: 64px;
    background: rgba(4, 7, 16, 0.6);
    border: 1px solid var(--line);
    box-shadow: inset 0 0 12px rgba(0, 0, 0, 0.6);
    display: flex;
    align-items: center;
    justify-content: center;
  }
  .pip__thumb > svg {
    width: 100%;
    height: 100%;
  }
  .pip__head-text {
    min-width: 0;
    display: flex;
    flex-direction: column;
    gap: 3px;
  }
  .pip__title {
    margin: 0;
    font-family: var(--font-display);
    font-size: 17px;
    line-height: 1.05;
    letter-spacing: 0.04em;
    color: var(--fg);
    text-shadow: 0 0 6px rgba(126, 245, 184, 0.08);
    /* Two-line clamp keeps long part titles from blowing out the head
       (some stock parts run 40+ chars). */
    display: -webkit-box;
    -webkit-line-clamp: 2;
    line-clamp: 2;
    -webkit-box-orient: vertical;
    overflow: hidden;
  }
  .pip__manuf {
    font-family: var(--font-mono);
    font-size: 9px;
    color: var(--fg-dim);
    text-transform: uppercase;
    letter-spacing: 0.18em;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }
  .pip__head-stats {
    margin-top: 4px;
    display: flex;
    align-items: baseline;
    gap: 10px;
  }
  .pip__stat {
    display: inline-flex;
    align-items: baseline;
    gap: 5px;
  }
  .pip__stat-label {
    font-family: var(--font-mono);
    font-size: 7px;
    letter-spacing: 0.22em;
    text-transform: uppercase;
    color: var(--fg-dim);
  }
  .pip__stat-value {
    font-family: var(--font-display);
    font-size: 13px;
    color: var(--accent);
    text-shadow: 0 0 6px var(--accent-glow);
    font-variant-numeric: tabular-nums;
    letter-spacing: 0.02em;
  }
  .pip__stat-value--funds em {
    font-style: normal;
    font-size: 8px;
    margin-left: 2px;
    color: var(--fg-dim);
    letter-spacing: 0.1em;
  }
  .pip__stat-sep {
    width: 1px;
    align-self: stretch;
    background: var(--line);
    margin: 1px 0;
  }

  /* DESCRIPTION ----------------------------------------------------- */
  .pip__desc {
    margin: 0;
    padding: 8px 14px 12px;
    color: var(--fg);
    font-family: var(--font-mono);
    font-size: 10.5px;
    line-height: 1.42;
    border-bottom: 1px solid var(--line);
    /* Stock descriptions are author-written prose; keep them legible
       without inflating the popup. Clamp at 4 lines, fade the tail so
       the truncation reads as intentional. */
    display: -webkit-box;
    -webkit-line-clamp: 4;
    line-clamp: 4;
    -webkit-box-orient: vertical;
    overflow: hidden;
    position: relative;
  }
  .pip__desc::after {
    /* Bottom fade — only visible if the description was clamped, since
       a 1-line description renders no overflow under the gradient
       endpoint. The gradient lives in the last line's space, so short
       text just hides under transparent stops. */
    content: '';
    position: absolute;
    inset: auto 14px 12px 14px;
    height: 1.42em;
    background: linear-gradient(to bottom,
      transparent 0,
      rgba(4, 7, 16, 0.94) 100%);
    pointer-events: none;
  }

  /* GROUPS ---------------------------------------------------------- */
  .pip__groups {
    display: flex;
    flex-direction: column;
  }
  .pip__group-head {
    display: flex;
    align-items: center;
    gap: 8px;
    padding: 8px 14px 4px;
    border-top: 1px solid rgba(26, 35, 53, 0.6);
  }
  .pip__group-head:first-child {
    border-top: none;
  }
  /* Single-char monogram tile per component kind. Reads as a typographic
     anchor for the group; the kind letter is the same character we use
     on the wire so the tile doubles as a wire-format key. */
  .pip__mono {
    flex: 0 0 18px;
    width: 18px;
    height: 18px;
    display: inline-flex;
    align-items: center;
    justify-content: center;
    font-family: var(--font-display);
    font-size: 11px;
    color: var(--accent);
    text-shadow: 0 0 6px var(--accent-glow);
    background: rgba(126, 245, 184, 0.08);
    border: 1px solid var(--line-accent);
    letter-spacing: 0;
  }
  .pip__group-label {
    flex: 0 1 auto;
    font-family: var(--font-display);
    font-size: 12px;
    letter-spacing: 0.2em;
    color: var(--fg);
    text-transform: uppercase;
  }
  .pip__group-hint {
    flex: 1 1 auto;
    text-align: right;
    font-family: var(--font-mono);
    font-size: 8.5px;
    letter-spacing: 0.16em;
    color: var(--fg-dim);
    text-transform: uppercase;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  /* KEY-VALUE GRID ------------------------------------------------- */
  .pip__grid {
    display: grid;
    grid-template-columns: 1fr 1fr;
    gap: 1px 14px;
    padding: 4px 14px 8px;
  }
  .pip__cell {
    display: flex;
    align-items: baseline;
    justify-content: space-between;
    gap: 6px;
    min-height: 13px;
    padding: 1px 0;
  }
  .pip__cell-label {
    font-family: var(--font-mono);
    font-size: 7px;
    letter-spacing: 0.22em;
    text-transform: uppercase;
    color: var(--fg-dim);
    flex: 0 0 auto;
  }
  .pip__cell-value {
    font-family: var(--font-display);
    font-size: 12px;
    line-height: 1;
    color: var(--fg);
    font-variant-numeric: tabular-nums;
    letter-spacing: 0.02em;
    text-align: right;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  /* TANK SLICES ---------------------------------------------------- */
  .pip__slices {
    list-style: none;
    margin: 0;
    padding: 0 14px 8px;
    display: flex;
    flex-direction: column;
    gap: 2px;
  }
  .pip__slice {
    display: grid;
    grid-template-columns: 42px 1fr auto;
    align-items: center;
    gap: 8px;
    padding: 3px 6px;
    background: var(--slice-tint, rgba(126, 245, 184, 0.04));
    border-left: 2px solid var(--slice-color, var(--accent));
  }
  .pip__slice-code {
    font-family: var(--font-display);
    font-size: 11px;
    color: var(--slice-color, var(--accent));
    letter-spacing: 0.06em;
    text-shadow: 0 0 4px rgba(0, 0, 0, 0.6);
  }
  .pip__slice-cap {
    font-family: var(--font-display);
    font-size: 12px;
    color: var(--fg);
    font-variant-numeric: tabular-nums;
    letter-spacing: 0.02em;
  }
  .pip__slice-tier {
    font-family: var(--font-mono);
    font-size: 8px;
    letter-spacing: 0.16em;
    color: var(--fg-dim);
    text-transform: uppercase;
  }

  /* PROPELLANT CHIPS ----------------------------------------------- */
  .pip__prop {
    display: flex;
    align-items: center;
    gap: 8px;
    padding: 2px 14px 8px;
    flex-wrap: wrap;
  }
  .pip__prop-label {
    flex: 0 0 auto;
    font-family: var(--font-mono);
    font-size: 7px;
    letter-spacing: 0.22em;
    color: var(--fg-dim);
    text-transform: uppercase;
  }
  .pip__prop-list {
    flex: 1 1 auto;
    display: flex;
    align-items: center;
    gap: 4px;
    flex-wrap: wrap;
    font-variant-numeric: tabular-nums;
  }
  .pip__prop-chip {
    display: inline-flex;
    align-items: baseline;
    gap: 3px;
    padding: 2px 6px;
    background: var(--slice-tint);
    border-left: 1px solid var(--slice-color);
    font-family: var(--font-display);
    font-size: 11px;
    color: var(--slice-color);
    letter-spacing: 0.06em;
  }
  .pip__prop-chip em {
    font-style: normal;
    font-family: var(--font-mono);
    font-size: 9px;
    color: var(--fg-dim);
  }
  .pip__prop-plus {
    color: var(--fg-mute);
    font-family: var(--font-mono);
    font-size: 10px;
    user-select: none;
  }

  /* DECOUPLER CROSSFEED -------------------------------------------- */
  .pip__crossfeed {
    display: flex;
    align-items: center;
    gap: 8px;
    padding: 0 14px 8px;
    flex-wrap: wrap;
  }
  .pip__crossfeed-label {
    flex: 0 0 auto;
    font-family: var(--font-mono);
    font-size: 7px;
    letter-spacing: 0.22em;
    color: var(--fg-dim);
    text-transform: uppercase;
  }
  .pip__crossfeed-list {
    flex: 1 1 auto;
    display: flex;
    gap: 4px;
    flex-wrap: wrap;
  }
  .pip__crossfeed-chip {
    padding: 1px 6px;
    background: var(--slice-tint);
    border: 1px solid var(--slice-color);
    border-radius: 0;
    font-family: var(--font-display);
    font-size: 10px;
    color: var(--slice-color);
    letter-spacing: 0.08em;
  }
</style>
