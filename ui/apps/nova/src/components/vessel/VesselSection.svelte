<script lang="ts">
  // Vessel identity + state. Top accordion section of SideRack.
  //
  // Three strata in the open body:
  //   1. IDENTITY  — name (display caps) · status pip + situation · body
  //   2. TELEMETRY — mass · parts · crew, two-column mono grid
  //   3. (collapsed-summary slot, rendered ALWAYS in the section header)
  //
  // Reads NovaVesselState/<vesselId>. The structure topic is NOT
  // subscribed here — `partCount` comes off NovaVesselState so this
  // section is independent of the structure tree's load order.

  import AccordionSection from '../common/AccordionSection.svelte';
  import { useNovaVesselState } from '../../telemetry/use-nova-vessel-state.svelte';
  import {
    VesselSituation,
    type NovaVesselState,
  } from '../../telemetry/nova-topics';
  import { siPrefix, fmtMag } from '../../util/units';

  interface Props {
    vesselId: string | (() => string | undefined);
    open?: boolean;
  }

  let { vesselId, open = $bindable(true) }: Props = $props();

  // Wrap in a fresh closure so the hook re-evaluates on every change
  // to the destructured prop (Svelte's $props returns getters; reading
  // `vesselId` directly captures the initial value once).
  const stateRef = useNovaVesselState(() =>
    typeof vesselId === 'function' ? vesselId() : vesselId,
  );
  const state = $derived<NovaVesselState | undefined>(stateRef.current);

  // ---- Situation → label + status tier ----------------------------
  // Three tiers map to the pip colour. Stable states (orbit / land /
  // dock / prelaunch / splashed) are green; flight transitions are
  // amber; only Escaping is red (the only situation that actually
  // represents a flag-able trajectory state).
  type Tier = 'stable' | 'transitional' | 'critical';

  function situationLabel(s: VesselSituation | undefined): string {
    switch (s) {
      case VesselSituation.Landed:     return 'LANDED';
      case VesselSituation.Splashed:   return 'SPLASHED';
      case VesselSituation.Prelaunch:  return 'PRELAUNCH';
      case VesselSituation.Flying:     return 'FLYING';
      case VesselSituation.SubOrbital: return 'SUB-ORBITAL';
      case VesselSituation.Orbiting:   return 'ORBITING';
      case VesselSituation.Escaping:   return 'ESCAPING';
      case VesselSituation.Docked:     return 'DOCKED';
      default: return '—';
    }
  }

  function situationTier(s: VesselSituation | undefined): Tier {
    switch (s) {
      case VesselSituation.Flying:
      case VesselSituation.SubOrbital:
        return 'transitional';
      case VesselSituation.Escaping:
        return 'critical';
      default:
        return 'stable';
    }
  }

  // ---- Formatters --------------------------------------------------
  function fmtMass(massKg: number | undefined): { mag: string; unit: string } {
    if (massKg === undefined || !Number.isFinite(massKg)) return { mag: '—', unit: '' };
    // Convert SI base (kg) to tonnes, then SI-prefix on tonnes so
    // 1.5 Mt (i.e. 1.5e9 kg) reads as "1.50 Mt" if it ever happens.
    const t = massKg / 1000;
    const p = siPrefix(t);
    return { mag: fmtMag(t / p.div), unit: `${p.letter}t` };
  }

  const name = $derived(state?.name ?? '');
  const sitLabel = $derived(situationLabel(state?.situation));
  const sitTier = $derived(situationTier(state?.situation));
  const body = $derived(state?.bodyName ?? '');
  const mass = $derived(fmtMass(state?.massKg));
  const partCount = $derived(state?.partCount ?? 0);
  const crewCount = $derived(state?.crewCount ?? 0);
  const crewCapacity = $derived(state?.crewCapacity ?? 0);
</script>

<AccordionSection id="vessel" title="Vessel" bind:open>
  {#snippet summary({ open: o })}
    {#if !o}
      <!-- Collapsed: terse identity + numerics so the rack stays
           glanceable. Mass-only is too sparse; name-only loses the
           live-state signal. Mass + crew is the right compression. -->
      <span class="vs__sum">
        <span class="vs__sum-mass">{mass.mag}<em>{mass.unit}</em></span>
        <span class="vs__sum-sep">·</span>
        <span class="vs__sum-crew">{crewCount}/{crewCapacity}</span>
      </span>
    {/if}
  {/snippet}

  <div class="vs">
    <!-- Stratum 1: IDENTITY -->
    <div class="vs__identity">
      <div class="vs__name" title={name}>{name || '—'}</div>
      <div class="vs__sit-row">
        <span class="vs__pip" class:vs__pip--transitional={sitTier === 'transitional'}
              class:vs__pip--critical={sitTier === 'critical'}></span>
        <span class="vs__sit">{sitLabel}</span>
        {#if body}
          <span class="vs__dot">·</span>
          <span class="vs__body">{body}</span>
        {/if}
      </div>
    </div>

    <!-- Etched separator between strata. The rule's accent edge
         mirrors the active-section accent on the rack so the strata
         feel like instrument-panel sub-bays, not just stacked text. -->
    <div class="vs__rule" aria-hidden="true"></div>

    <!-- Stratum 2: TELEMETRY -->
    <div class="vs__telemetry">
      <div class="vs__cell">
        <div class="vs__cell-label">MASS</div>
        <div class="vs__cell-val">
          {mass.mag}<em class="vs__unit">{mass.unit}</em>
        </div>
      </div>
      <div class="vs__cell">
        <div class="vs__cell-label">PARTS</div>
        <div class="vs__cell-val">{partCount}</div>
      </div>
      <div class="vs__cell vs__cell--right">
        <div class="vs__cell-label">CREW</div>
        <div class="vs__cell-val">
          <span class="vs__crew-cur">{crewCount}</span>
          <span class="vs__crew-sep">/</span>
          <span class="vs__crew-cap">{crewCapacity}</span>
        </div>
      </div>
    </div>
  </div>
</AccordionSection>

<style>
  .vs {
    display: flex;
    flex-direction: column;
    gap: 10px;
  }

  /* ----- Identity stratum ----- */
  .vs__identity {
    display: flex;
    flex-direction: column;
    gap: 4px;
  }
  .vs__name {
    font-family: var(--font-display);
    font-size: 16px;
    letter-spacing: 0.14em;
    text-transform: uppercase;
    color: var(--accent);
    text-shadow: 0 0 8px var(--accent-glow);
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }
  .vs__sit-row {
    display: flex;
    align-items: center;
    gap: 6px;
    font-family: var(--font-mono);
    font-size: 10px;
    letter-spacing: 0.16em;
    text-transform: uppercase;
    color: var(--fg-dim);
  }
  .vs__pip {
    flex: 0 0 6px;
    width: 6px;
    height: 6px;
    border-radius: 50%;
    background: var(--accent);
    box-shadow: 0 0 6px var(--accent-glow);
  }
  .vs__pip--transitional {
    background: var(--warn);
    box-shadow: 0 0 6px var(--warn-glow);
  }
  .vs__pip--critical {
    background: var(--alert);
    box-shadow: 0 0 6px rgba(255, 82, 82, 0.5);
  }
  .vs__sit {
    color: var(--fg);
  }
  .vs__dot {
    color: var(--fg-mute);
  }
  .vs__body {
    color: var(--fg-dim);
  }

  /* Etched rule — gradient that fades in/out so the line reads as a
     subtle inscribed seam, not a hard border. */
  .vs__rule {
    height: 1px;
    background: linear-gradient(
      to right,
      transparent 0,
      var(--line-accent) 20%,
      var(--line-accent) 80%,
      transparent 100%
    );
    opacity: 0.7;
  }

  /* ----- Telemetry stratum ----- */
  .vs__telemetry {
    display: grid;
    grid-template-columns: 1fr 1fr auto;
    gap: 12px;
    align-items: end;
  }
  .vs__cell {
    display: flex;
    flex-direction: column;
    gap: 2px;
    min-width: 0;
  }
  .vs__cell--right {
    text-align: right;
  }
  .vs__cell-label {
    font-family: var(--font-display);
    font-size: 9px;
    letter-spacing: 0.24em;
    text-transform: uppercase;
    color: var(--fg-mute);
  }
  .vs__cell-val {
    font-family: var(--font-mono);
    font-size: 15px;
    font-variant-numeric: tabular-nums;
    color: var(--fg);
  }
  .vs__unit {
    margin-left: 3px;
    font-size: 10px;
    font-style: normal;
    color: var(--fg-dim);
  }
  .vs__crew-cur {
    color: var(--fg);
  }
  .vs__crew-sep {
    color: var(--fg-mute);
    margin: 0 1px;
  }
  .vs__crew-cap {
    color: var(--fg-dim);
  }

  /* ----- Collapsed-state summary (always-rendered slot) ----- */
  .vs__sum {
    display: inline-flex;
    align-items: baseline;
    gap: 4px;
    font-variant-numeric: tabular-nums;
  }
  .vs__sum-mass em {
    margin-left: 2px;
    font-style: normal;
    color: var(--fg-mute);
  }
  .vs__sum-sep {
    color: var(--fg-mute);
  }
  .vs__sum-crew {
    color: var(--fg-dim);
  }
</style>
