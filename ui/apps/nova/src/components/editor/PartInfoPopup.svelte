<script lang="ts">
  // Replaces KSP's stock `PartListTooltip`. Subscribes to the singleton
  // `NovaPartInfoTopic`; when the C# Harmony patch on
  // `PartListTooltipController.OnPointerEnter` fires, the topic emits a
  // populated frame and the popup opens anchored to the parts catalog's
  // right edge (not the icon's edge — neighbouring icons stay visible).
  // Right-click on the icon toggles pin (stock-style sticky); the C# side
  // owns the pin flag and we mirror it as a glyph in the title bar.
  //
  // Layout (top-to-bottom):
  //   1. Title bar      — diamond glyph · title · pin glyph
  //   2. Marquee block  — 3-D preview · class line + 2-3 marquee stats
  //   3. Meta strip     — manufacturer · dry mass · cost
  //   4. Description    — flavour text, no clamp
  //   5. Detail groups  — one section per Nova component kind, the
  //                       18-px tile rendering the same SVG icon that
  //                       appears in the flight vessel panel.
  //
  // The popup renders opaque so Dragonglass's raycast filter sees it
  // as "DG UI" — that's how `NovaPartInfoCloser` C#-side decides to
  // keep the popup open when the cursor crosses from icon onto popup.
  // No hover bookkeeping on this side.

  import { onDestroy } from 'svelte';
  import { getKsp } from '@dragonglass/telemetry/svelte';
  import { PunchThrough } from '@dragonglass/instruments';
  import {
    NovaPartInfoTopic,
    decodePartInfo,
    InsulationTier,
    type NovaPartInfo,
    type PropellantSpec,
  } from '../../telemetry/nova-topics';
  import { resourceMeta } from '../resource/resource-codes';
  import { fmtMag, fmtBytes, fmtDuration, siPrefix } from '../../util/units';
  import ComponentIcon, { type ComponentKind } from '../ComponentIcon.svelte';

  const ksp = getKsp();
  let info = $state<NovaPartInfo | null>(null);
  const unsub = ksp.subscribe(NovaPartInfoTopic, (frame) => {
    info = decodePartInfo(frame);
  });
  onDestroy(unsub);

  // ----- Placement ------------------------------------------------

  const VIEWPORT_MARGIN = 12;
  // Gap between the catalog's right edge and the popup's left edge.
  // Small enough that the cursor's transit from icon → popup is short,
  // large enough that the catalog's outer chrome doesn't visually merge
  // with the popup's accent stripe.
  const CATALOG_GAP = 10;

  let popupEl: HTMLDivElement | null = $state(null);
  let placed = $state<{ x: number; y: number; flipped: boolean } | null>(null);

  // Anchor flush against the catalog's right edge by default; flip to the
  // catalog's left side if the right side would clip the viewport. Vertical
  // anchor is still the icon's top so the popup tracks the hovered icon
  // when the player scans the catalog with the popup pinned.
  //
  // The wire delivers all anchors in Unity screen pixels; CSS works in
  // logical pixels. Divide by DPR once at the boundary so every
  // comparison below stays in CSS-pixel space.
  $effect(() => {
    if (!info || !popupEl) {
      placed = null;
      return;
    }
    const dpr = window.devicePixelRatio || 1;
    const iconY = info.iconY / dpr;
    const catRight = info.catalogRightX / dpr;
    const catLeft  = info.catalogLeftX  / dpr;
    const rect = popupEl.getBoundingClientRect();
    const vw = window.innerWidth;
    const vh = window.innerHeight;

    let x = catRight + CATALOG_GAP;
    let flipped = false;
    if (x + rect.width + VIEWPORT_MARGIN > vw) {
      x = catLeft - rect.width - CATALOG_GAP;
      flipped = true;
    }
    x = Math.max(VIEWPORT_MARGIN, x);

    let y = iconY;
    if (y + rect.height + VIEWPORT_MARGIN > vh) {
      y = vh - rect.height - VIEWPORT_MARGIN;
    }
    y = Math.max(VIEWPORT_MARGIN, y);

    placed = { x, y, flipped };
  });

  // ----- Formatting helpers --------------------------------------

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

  function fmtFunds(funds: number): string {
    if (!Number.isFinite(funds) || funds < 0) return '—';
    return Math.round(funds).toLocaleString('en-US');
  }

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

  function rtgEolFraction(halfLifeDays: number): number {
    if (!Number.isFinite(halfLifeDays) || halfLifeDays <= 0) return 0;
    const stepDays = halfLifeDays * Math.log(1 - 0.001) / Math.log(0.5);
    const stepsPerKerbinYear = (426 * 6 * 3600) / 86400 / Math.abs(stepDays);
    return Math.pow(1 - 0.001, stepsPerKerbinYear);
  }

  // Propellant family glyph for the marquee: single propellant returns
  // its short code; two propellants join with " + " so the marquee line
  // stays one row. The detail section below still shows the full chip
  // strip with ratios. Resource codes come from the same `resourceMeta`
  // table the chips use, so the marquee glyph is colour-anchored.
  function propellantFamily(p: PropellantSpec[]): string {
    if (!p || p.length === 0) return '—';
    return p.map(e => resourceMeta(e.resource).code).join(' + ');
  }

  // ----- Component kind → SVG icon mapping ------------------------
  //
  // The wire kind char is the canonical key (see PartInfoFormatter on
  // the C# side); the SVG icon shapes live in ComponentIcon.svelte and
  // are shared with the flight vessel panel. Same shape = same component
  // in both editor and flight, which is the whole point of replacing the
  // single-char monogram tiles with these icons.

  const KIND_ICON: Record<string, ComponentKind> = {
    E: 'engine',
    N: 'nuclear',
    M: 'rcs',
    T: 'tank',
    B: 'battery',
    F: 'fuelCell',
    S: 'solar',
    R: 'rtg',
    W: 'wheel',
    X: 'radiator',
    L: 'light',
    C: 'command',
    P: 'probe',
    A: 'antenna',
    D: 'decoupler',
    K: 'docking',
    Y: 'crew',
    Z: 'dataStorage',
    H: 'thermometer',
  };

  // ----- Marquee picker ------------------------------------------
  //
  // Pick the part's primary kind by precedence and build the marquee
  // block's class line + up to three big-typeset stats. Compound parts
  // (e.g. command pod with battery + wheel) still get a single marquee
  // anchored on the headline kind — the detail sections below cover
  // every component. Structural parts fall through to a STRUCTURAL
  // marquee so the slot doesn't read as broken.

  type MarqueeStat = { label: string; value: string };
  type Marquee = { kind: ComponentKind | null; classLine: string; stats: MarqueeStat[] };

  function summarize(i: NovaPartInfo): Marquee {
    if (i.nuclear.length > 0) {
      const n = i.nuclear[0];
      return {
        kind: 'nuclear',
        classLine: 'NUCLEAR ENGINE',
        stats: [
          { label: 'THRUST', value: `${fmtMag(n.thrustKn)} kN` },
          { label: 'ISP',    value: `${fmtMag(n.ispS)} s` },
          { label: 'PWR',    value: `${fmtPower(n.idlePowerW)} → ${fmtPower(n.maxPowerW)}` },
        ],
      };
    }
    if (i.engine.length > 0) {
      const e = i.engine[0];
      const cls = (e.class || '').toUpperCase();
      return {
        kind: 'engine',
        classLine: cls ? `${cls} ENGINE` : 'ENGINE',
        stats: [
          { label: 'THRUST', value: `${fmtMag(e.thrustKn)} kN` },
          { label: 'ISP',    value: `${fmtMag(e.ispS)} s` },
          { label: 'PROP',   value: propellantFamily(e.propellants) },
        ],
      };
    }
    if (i.rcs.length > 0) {
      const r = i.rcs[0];
      return {
        kind: 'rcs',
        classLine: 'RCS THRUSTERS',
        stats: [
          { label: 'TOTAL', value: `${fmtMag(r.thrusterPowerKn * r.thrusterCount)} kN (${r.thrusterCount}×)` },
          { label: 'ISP',   value: `${fmtMag(r.ispS)} s` },
          { label: 'PROP',  value: propellantFamily(r.propellants) },
        ],
      };
    }
    if (i.tank.length > 0) {
      const t = i.tank[0];
      const sliceSummary = t.slices.length === 1
        ? resourceMeta(t.slices[0].resource).code
        : `${t.slices.length} slices`;
      return {
        kind: 'tank',
        classLine: 'FUEL TANK',
        stats: [
          { label: 'VOLUME', value: fmtVolume(t.volumeL) },
          { label: 'RATE',   value: `${fmtMag(t.maxRateLps)} L/s` },
          { label: 'STORE',  value: sliceSummary },
        ],
      };
    }
    if (i.solar.length > 0) {
      const s = i.solar[0];
      return {
        kind: 'solar',
        classLine: 'SOLAR ARRAY',
        stats: [
          { label: 'OUTPUT', value: `${fmtPower(s.chargeRateW)} @ 1AU` },
          { label: 'TRACK',  value: s.isTracking ? 'yes' : (s.isDeployable ? 'deploy' : 'fixed') },
        ],
      };
    }
    if (i.rtg.length > 0) {
      const r = i.rtg[0];
      const eolY = rtgEolFraction(r.halfLifeDays);
      return {
        kind: 'rtg',
        classLine: 'RTG',
        stats: [
          { label: 'BOL',       value: fmtPower(r.referencePowerW) },
          { label: 'HALF-LIFE', value: `${fmtMag(r.halfLifeDays / 365.25)} yr` },
          { label: 'EOL Y+1',   value: `${(eolY * 100).toFixed(1)}%` },
        ],
      };
    }
    if (i.fuelCell.length > 0) {
      const f = i.fuelCell[0];
      return {
        kind: 'fuelCell',
        classLine: 'FUEL CELL',
        stats: [
          { label: 'OUTPUT', value: fmtPower(f.maxOutputW) },
          { label: 'LH₂',    value: `${fmtMag(f.lh2RateKgs * 1000)} g/s` },
          { label: 'LOX',    value: `${fmtMag(f.loxRateKgs * 1000)} g/s` },
        ],
      };
    }
    if (i.battery.length > 0) {
      const b = i.battery[0];
      return {
        kind: 'battery',
        classLine: 'BATTERY',
        stats: [
          { label: 'CAPACITY', value: fmtEnergy(b.capacityJ) },
          { label: 'RATE',     value: `${fmtPower(b.maxRateW)} ⇋` },
        ],
      };
    }
    if (i.wheel.length > 0) {
      const w = i.wheel[0];
      const maxAxis = Math.max(w.pitchTorqueKnm, w.yawTorqueKnm, w.rollTorqueKnm);
      return {
        kind: 'wheel',
        classLine: 'REACTION WHEEL',
        stats: [
          { label: 'TORQUE', value: `${fmtMag(maxAxis)} kN·m` },
          { label: 'EC',     value: fmtPower(w.electricRateW) },
        ],
      };
    }
    if (i.radiator.length > 0) {
      const x = i.radiator[0];
      return {
        kind: 'radiator',
        classLine: 'RADIATOR',
        stats: [
          { label: 'COOLING', value: fmtPower(x.vacuumCoolingW) },
          { label: 'PUMP',    value: x.ecPerWattCooling > 0 ? 'active' : 'passive' },
        ],
      };
    }
    if (i.antenna.length > 0) {
      const a = i.antenna[0];
      return {
        kind: 'antenna',
        classLine: 'ANTENNA',
        stats: [
          { label: 'TX',  value: fmtPower(a.txPowerW) },
          { label: 'MAX', value: fmtRate(a.maxRateBps) },
          { label: 'REF', value: fmtDistance(a.refDistanceM) },
        ],
      };
    }
    if (i.command.length > 0) {
      const c = i.command[0];
      const crew = i.crew[0]?.crewCapacity ?? 0;
      const stats: MarqueeStat[] = [];
      if (crew > 0) stats.push({ label: 'CREW', value: `${crew}` });
      stats.push({ label: 'IDLE', value: fmtPower(c.idleDrawW) });
      return { kind: 'command', classLine: 'COMMAND POD', stats };
    }
    if (i.probe.length > 0) {
      const p = i.probe[0];
      return {
        kind: 'probe',
        classLine: 'PROBE CORE',
        stats: [
          { label: 'SAS',  value: `lv ${p.sasLevel}` },
          { label: 'IDLE', value: fmtPower(p.idleDrawW) },
        ],
      };
    }
    if (i.decoupler.length > 0) {
      const d = i.decoupler[0];
      return {
        kind: 'decoupler',
        classLine: 'DECOUPLER',
        stats: [
          { label: 'FORCE', value: `${fmtMag(d.ejectionForceKn)} kN` },
          { label: 'TYPE',  value: d.canFullSeparate ? 'separator' : 'radial' },
        ],
      };
    }
    if (i.docking.length > 0) {
      const k = i.docking[0];
      return {
        kind: 'docking',
        classLine: 'DOCKING PORT',
        stats: [{ label: 'SIZE', value: k.nodeType || '—' }],
      };
    }
    if (i.crew.length > 0) {
      const c = i.crew[0];
      return {
        kind: 'crew',
        classLine: 'CREW CABIN',
        stats: [{ label: 'CAPACITY', value: `${c.crewCapacity}` }],
      };
    }
    if (i.light.length > 0) {
      const l = i.light[0];
      return {
        kind: 'light',
        classLine: 'LIGHT',
        stats: [{ label: 'DRAW', value: fmtPower(l.drawW) }],
      };
    }
    if (i.storage.length > 0) {
      const z = i.storage[0];
      return {
        kind: 'dataStorage',
        classLine: 'DATA STORAGE',
        stats: [{ label: 'CAPACITY', value: fmtBytes(z.capacityBytes) }],
      };
    }
    if (i.thermometer.length > 0) {
      const h = i.thermometer[0];
      return {
        kind: 'thermometer',
        classLine: 'INSTRUMENT',
        stats: [{ label: 'EC', value: fmtPower(h.ecRateW) }],
      };
    }
    return { kind: null, classLine: 'STRUCTURAL', stats: [] };
  }

  const marquee = $derived(info ? summarize(info) : null);
</script>

{#if info}
  <div
    bind:this={popupEl}
    class="pip"
    class:pip--flipped={placed?.flipped}
    class:pip--placed={placed !== null}
    class:pip--pinned={info.isPinned}
    style:left="{(placed?.x ?? -9999)}px"
    style:top="{(placed?.y ?? -9999)}px"
    role="tooltip"
    aria-live="polite"
  >
    <!-- TITLE BAR ------------------------------------------------ -->
    <header class="pip__titlebar">
      <span class="pip__diamond" aria-hidden="true">◇</span>
      <h2 class="pip__title">{info.title}</h2>
      <span class="pip__pin" aria-hidden="true" title={info.isPinned ? 'Pinned (right-click icon to unpin)' : 'Right-click icon to pin'}>
        {#if info.isPinned}
          <!-- Filled push-pin glyph: head + stem -->
          <svg viewBox="0 0 12 12" class="pip__pin-svg">
            <path d="M3.4 1.5 L8.6 1.5 L7.6 4.8 L9.1 7.0 L2.9 7.0 L4.4 4.8 Z" fill="currentColor"/>
            <line x1="6" y1="7" x2="6" y2="10.6" stroke="currentColor" stroke-width="1.1" stroke-linecap="round"/>
          </svg>
        {:else}
          <svg viewBox="0 0 12 12" class="pip__pin-svg">
            <path d="M3.4 1.5 L8.6 1.5 L7.6 4.8 L9.1 7.0 L2.9 7.0 L4.4 4.8 Z" stroke="currentColor" fill="none" stroke-width="0.9" stroke-linejoin="round"/>
            <line x1="6" y1="7" x2="6" y2="10.6" stroke="currentColor" stroke-width="0.9" stroke-linecap="round"/>
          </svg>
        {/if}
      </span>
    </header>

    <!-- MARQUEE : 3-D preview · class line + headline stats -------- -->
    <div class="pip__marquee">
      <div class="pip__thumb" aria-hidden="true">
        <PunchThrough id="novaPartPreview" />
      </div>

      <div class="pip__marquee-stats">
        <div class="pip__class">
          {#if marquee?.kind}
            <span class="pip__class-icon"><ComponentIcon kind={marquee.kind} /></span>
          {/if}
          <span class="pip__class-text">{marquee?.classLine ?? ''}</span>
        </div>
        <div class="pip__class-rule" aria-hidden="true"></div>
        {#if marquee && marquee.stats.length > 0}
          <dl class="pip__marquee-grid">
            {#each marquee.stats as s (s.label)}
              <dt>{s.label}</dt>
              <dd>{s.value}</dd>
            {/each}
          </dl>
        {/if}
      </div>
    </div>

    <!-- META STRIP : manufacturer · dry · cost --------------------- -->
    <div class="pip__meta">
      <span class="pip__meta-slot">{info.manufacturer || '—'}</span>
      <span class="pip__meta-sep" aria-hidden="true">·</span>
      <span class="pip__meta-slot pip__meta-slot--num">{fmtMass(info.dryMassKg)}</span>
      <span class="pip__meta-sep" aria-hidden="true">·</span>
      <span class="pip__meta-slot pip__meta-slot--num">{fmtFunds(info.costFunds)}<em>₣</em></span>
    </div>

    {#if info.description}
      <p class="pip__desc">{info.description}</p>
    {/if}

    <!-- COMPONENT GROUPS ------------------------------------------ -->
    <div class="pip__groups">
      {#each info.engine as e, i (i)}
        {@render group('E', e.class ? `${e.class.toUpperCase()} ENGINE` : 'ENGINE', null)}
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
          {@render kv('LH₂',    `${fmtMag(f.lh2RateKgs * 1000)} g/s`)}
          {@render kv('LOX',    `${fmtMag(f.loxRateKgs * 1000)} g/s`)}
        </div>
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
        {@render group('C', 'COMMAND POD', null)}
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
        {@render group('K', 'DOCKING PORT', k.nodeType || null)}
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
        <div class="pip__grid">
          {@render kv('EC', fmtPower(h.ecRateW))}
        </div>
      {/each}

      {#each info.light as l, i (i)}
        {@render group('L', 'LIGHT', null)}
        <div class="pip__grid">
          {@render kv('DRAW', fmtPower(l.drawW))}
        </div>
      {/each}
    </div>

    {#snippet group(kindChar: string, label: string, hint: string | null)}
      {@const iconKind = KIND_ICON[kindChar]}
      <div class="pip__group-head">
        <span class="pip__icon-tile">
          {#if iconKind}<ComponentIcon kind={iconKind} />{/if}
        </span>
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
  /* Outer shell. Width-locked at 340px so the marquee + detail
     sections share consistent column widths. */
  .pip {
    position: fixed;
    width: 340px;
    z-index: 1200;
    pointer-events: auto;
    opacity: 0;
    transform: translateY(-4px);
    background: var(--bg-panel-strong);
    border: 1px solid var(--line-accent);
    /* No outward box-shadows — they'd render alpha>0 pixels in CEF
       outside the popup's box and steal raycasts away from icons under
       the halo. Inset 1px stays inside the box and is safe. */
    box-shadow: inset 0 0 0 1px rgba(126, 245, 184, 0.045);
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
  /* Reading-edge stripe — left edge by default, right when flipped. */
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

  /* TITLE BAR ----------------------------------------------------- */
  .pip__titlebar {
    display: grid;
    grid-template-columns: 14px 1fr 18px;
    align-items: center;
    gap: 8px;
    height: 26px;
    padding: 0 12px;
    background:
      linear-gradient(90deg,
        rgba(126, 245, 184, 0.07) 0,
        rgba(126, 245, 184, 0.03) 60%,
        transparent 100%);
    border-bottom: 1px solid var(--line);
  }
  .pip__diamond {
    color: var(--accent);
    font-family: var(--font-display);
    font-size: 10px;
    text-shadow: 0 0 4px var(--accent-glow);
    letter-spacing: 0.2em;
  }
  .pip__title {
    margin: 0;
    font-family: var(--font-display);
    font-size: 14px;
    line-height: 1.05;
    letter-spacing: 0.04em;
    color: var(--fg);
    text-shadow: 0 0 6px rgba(126, 245, 184, 0.08);
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }
  .pip__pin {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    width: 18px;
    height: 18px;
    color: var(--fg-mute);
    opacity: 0.55;
    transition: color 120ms, opacity 120ms;
  }
  .pip--pinned .pip__pin {
    color: var(--accent);
    opacity: 1;
    text-shadow: 0 0 4px var(--accent-glow);
  }
  .pip__pin-svg {
    width: 12px;
    height: 12px;
    overflow: visible;
    vector-effect: non-scaling-stroke;
  }

  /* MARQUEE ------------------------------------------------------- */
  .pip__marquee {
    display: grid;
    grid-template-columns: 96px 1fr;
    gap: 12px;
    padding: 12px 14px 10px;
  }
  .pip__thumb {
    width: 96px;
    height: 96px;
    background: rgba(4, 7, 16, 0.6);
    border: 1px solid var(--line);
    box-shadow: inset 0 0 12px rgba(0, 0, 0, 0.6);
  }
  .pip__marquee-stats {
    min-width: 0;
    display: flex;
    flex-direction: column;
    gap: 4px;
  }
  .pip__class {
    display: flex;
    align-items: center;
    gap: 6px;
    color: var(--accent);
  }
  .pip__class-icon {
    display: inline-flex;
    width: 14px;
    height: 14px;
    color: var(--accent);
    text-shadow: 0 0 4px var(--accent-glow);
  }
  .pip__class-text {
    font-family: var(--font-display);
    font-size: 13px;
    letter-spacing: 0.18em;
    text-transform: uppercase;
    color: var(--accent);
    text-shadow: 0 0 6px var(--accent-glow);
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }
  .pip__class-rule {
    height: 1px;
    background: linear-gradient(90deg, var(--line-accent), transparent);
    margin: 2px 0 4px;
  }
  .pip__marquee-grid {
    margin: 0;
    display: grid;
    grid-template-columns: auto 1fr;
    column-gap: 8px;
    row-gap: 3px;
  }
  .pip__marquee-grid dt {
    font-family: var(--font-mono);
    font-size: 7px;
    letter-spacing: 0.22em;
    text-transform: uppercase;
    color: var(--fg-dim);
    align-self: baseline;
    padding-top: 2px;
  }
  .pip__marquee-grid dd {
    margin: 0;
    font-family: var(--font-display);
    font-size: 12px;
    line-height: 1.1;
    color: var(--fg);
    font-variant-numeric: tabular-nums;
    letter-spacing: 0.02em;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  /* META STRIP --------------------------------------------------- */
  .pip__meta {
    display: flex;
    align-items: baseline;
    gap: 6px;
    padding: 4px 14px 8px;
    border-bottom: 1px solid var(--line);
    color: var(--fg-dim);
    font-family: var(--font-mono);
    font-size: 9px;
    letter-spacing: 0.16em;
    text-transform: uppercase;
  }
  .pip__meta-slot {
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
    flex: 0 1 auto;
  }
  .pip__meta-slot--num {
    color: var(--fg);
    font-variant-numeric: tabular-nums;
    letter-spacing: 0.04em;
  }
  .pip__meta-slot em {
    font-style: normal;
    font-size: 7.5px;
    margin-left: 2px;
    color: var(--fg-dim);
    letter-spacing: 0.1em;
  }
  .pip__meta-sep {
    color: var(--fg-mute);
    flex: 0 0 auto;
  }

  /* DESCRIPTION -------------------------------------------------- */
  .pip__desc {
    margin: 0;
    padding: 8px 14px 12px;
    color: var(--fg);
    font-family: var(--font-mono);
    font-size: 10.5px;
    line-height: 1.42;
    border-bottom: 1px solid var(--line);
  }

  /* GROUPS ------------------------------------------------------- */
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
  /* Section icon tile — same chrome as the previous monogram tile
     (border, glow, accent background) so the visual hierarchy stays
     consistent; the centre now hosts a 12-px SVG from ComponentIcon
     instead of a single display-font letter. The 12-px child sitting
     in an 18-px frame gives a small breathing margin on each side. */
  .pip__icon-tile {
    flex: 0 0 18px;
    width: 18px;
    height: 18px;
    display: inline-flex;
    align-items: center;
    justify-content: center;
    color: var(--accent);
    background: rgba(126, 245, 184, 0.08);
    border: 1px solid var(--line-accent);
    text-shadow: 0 0 6px var(--accent-glow);
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

  /* KEY-VALUE GRID --------------------------------------------- */
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

  /* TANK SLICES ------------------------------------------------- */
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

  /* PROPELLANT CHIPS ------------------------------------------- */
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

  /* DECOUPLER CROSSFEED ---------------------------------------- */
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
