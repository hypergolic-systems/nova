<script lang="ts">
  // Always-visible header at the top of the rack. Anchors the panel
  // with the vessel's identity — name (display caps, accent), then
  // a situation row (status pip + label + body), then a compact
  // single-line vital-stats strip (mass · parts · crew).
  //
  // Sits outside the Accordion so it never folds away. When no
  // vessel is loaded the header renders a placeholder shell so the
  // panel doesn't visually jump on scene transitions.
  //
  // Replaces the previous VesselSection accordion fold. The
  // mass/parts/crew telemetry that lived inside that fold now sits
  // inline beneath the name, because the four numbers are small
  // enough to anchor permanently and folding them away saved less
  // vertical space than a section head consumed.

  import { useNovaVesselState } from '../../telemetry/use-nova-vessel-state.svelte';
  import {
    VesselSituation,
    type NovaVesselState,
  } from '../../telemetry/nova-topics';
  import { siPrefix, fmtMag } from '../../util/units';

  interface Props {
    vesselId: string | (() => string | undefined);
  }

  let { vesselId }: Props = $props();

  const stateRef = useNovaVesselState(() =>
    typeof vesselId === 'function' ? vesselId() : vesselId,
  );
  const state = $derived<NovaVesselState | undefined>(stateRef.current);

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

  function fmtMass(massKg: number | undefined): { mag: string; unit: string } {
    if (massKg === undefined || !Number.isFinite(massKg)) return { mag: '—', unit: '' };
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

<header class="vh">
  <div class="vh__name" title={name || 'No vessel'}>{name || '—'}</div>

  <div class="vh__sit-row">
    <span
      class="vh__pip"
      class:vh__pip--transitional={sitTier === 'transitional'}
      class:vh__pip--critical={sitTier === 'critical'}
    ></span>
    <span class="vh__sit">{sitLabel}</span>
    {#if body}
      <span class="vh__dot">·</span>
      <span class="vh__body">{body}</span>
    {/if}
  </div>

  <div class="vh__stats" role="group" aria-label="Vessel vital statistics">
    <span class="vh__stat">
      <span class="vh__stat-val">{mass.mag}</span><em
        class="vh__stat-unit">{mass.unit}</em>
    </span>
    <span class="vh__stat-sep">·</span>
    <span class="vh__stat">
      <span class="vh__stat-val">{partCount}</span><em
        class="vh__stat-unit">PARTS</em>
    </span>
    <span class="vh__stat-sep">·</span>
    <span class="vh__stat">
      <span class="vh__stat-val">{crewCount}<span class="vh__stat-slash">/</span>{crewCapacity}</span><em
        class="vh__stat-unit">CREW</em>
    </span>
  </div>
</header>

<style>
  .vh {
    display: flex;
    flex-direction: column;
    gap: 6px;
    padding: 14px 14px 12px 14px;
    /* Inscribed bottom seam so the header reads as a distinct
       chassis bay above the accordion. Gradient fades the etched
       line at the edges so it doesn't read as a hard rule. */
    border-bottom: 1px solid transparent;
    background:
      linear-gradient(
        to bottom,
        rgba(126, 245, 184, 0.05) 0%,
        rgba(126, 245, 184, 0.00) 100%
      ),
      linear-gradient(
        to right,
        transparent 0%,
        var(--line-accent) 18%,
        var(--line-accent) 82%,
        transparent 100%
      );
    background-position: top left, bottom left;
    background-size: 100% 100%, 100% 1px;
    background-repeat: no-repeat;
  }

  /* ----- Name — the headline ----- */
  .vh__name {
    font-family: var(--font-display);
    font-size: 17px;
    line-height: 1.05;
    letter-spacing: 0.12em;
    text-transform: uppercase;
    color: var(--accent);
    text-shadow: 0 0 10px var(--accent-glow);
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
    /* Tiny upward optical adjustment — the all-caps display face
       sits slightly heavy without it. */
    margin-top: -1px;
  }

  /* ----- Situation row ----- */
  .vh__sit-row {
    display: flex;
    align-items: center;
    gap: 6px;
    font-family: var(--font-mono);
    font-size: 10px;
    letter-spacing: 0.18em;
    text-transform: uppercase;
    color: var(--fg-dim);
  }
  .vh__pip {
    flex: 0 0 6px;
    width: 6px;
    height: 6px;
    border-radius: 50%;
    background: var(--accent);
    box-shadow: 0 0 6px var(--accent-glow);
  }
  .vh__pip--transitional {
    background: var(--warn);
    box-shadow: 0 0 6px var(--warn-glow);
  }
  .vh__pip--critical {
    background: var(--alert);
    box-shadow: 0 0 6px rgba(255, 82, 82, 0.5);
  }
  .vh__sit {
    color: var(--fg);
  }
  .vh__dot {
    color: var(--fg-mute);
  }
  .vh__body {
    color: var(--fg-dim);
  }

  /* ----- Vital stats — single inline strip ----- */
  .vh__stats {
    display: flex;
    align-items: baseline;
    gap: 8px;
    font-family: var(--font-mono);
    font-variant-numeric: tabular-nums;
    font-size: 11px;
    color: var(--fg);
    /* Pull the stats line down ever so slightly so the three-line
       header has a clear rhythm (name → situation → stats), not a
       cramped block. */
    margin-top: 2px;
  }
  .vh__stat {
    display: inline-flex;
    align-items: baseline;
    gap: 3px;
  }
  .vh__stat-val {
    color: var(--fg);
  }
  .vh__stat-unit {
    font-style: normal;
    font-size: 9px;
    letter-spacing: 0.16em;
    color: var(--fg-mute);
  }
  .vh__stat-slash {
    color: var(--fg-mute);
    margin: 0 1px;
  }
  .vh__stat-sep {
    color: var(--fg-mute);
    opacity: 0.6;
  }
</style>
