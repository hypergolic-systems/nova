<script lang="ts">
  // Vessel-overview floating panel. Tabbed body — only Power is wired
  // up for the first cut; the other chips render disabled so the
  // visual rhythm is right when more views land.
  //
  // Mounted always-on while in Flight (open/close UX is deferred).
  // Default position pins to the upper-left so it doesn't compete
  // with the navball cluster in the lower-left.

  import { useFlightData } from '@dragonglass/telemetry/svelte';
  import { FloatingWindow } from '@dragonglass/windows';
  import { useNovaVesselStructure } from '../telemetry/use-nova-vessel-structure.svelte';
  import PowerView from './power/PowerView.svelte';

  type TabId = 'power' | 'propulsion' | 'rcs' | 'attitude';

  interface Tab {
    id: TabId;
    short: string;
    label: string;
    enabled: boolean;
  }

  const tabs: Tab[] = [
    { id: 'power',      short: 'PWR', label: 'Power',      enabled: true  },
    { id: 'propulsion', short: 'PRP', label: 'Propulsion', enabled: false },
    { id: 'rcs',        short: 'RCS', label: 'RCS',        enabled: false },
    { id: 'attitude',   short: 'ATT', label: 'Attitude',   enabled: false },
  ];

  const flight = useFlightData();
  const structureRef = useNovaVesselStructure(() => flight.vesselId);
  const vesselName = $derived(structureRef.current?.name ?? '');

  let activeTab = $state<TabId>('power');
  let z = $state(100);

  function raise(): void {
    z++;
  }
</script>

<FloatingWindow
  defaultPos={{ x: 24, y: 80 }}
  defaultSize={{ w: 380, h: 460 }}
  minSize={{ w: 300, h: 260 }}
  {z}
  onRaise={raise}
>
  {#snippet header()}
    <div class="vp__head">
      <span class="vp__brand">◇ VESSEL</span>
      <span class="vp__head-spacer"></span>
      <span class="vp__name">{vesselName}</span>
    </div>
  {/snippet}

  <nav class="vp__tabs" aria-label="Subsystems">
    {#each tabs as t (t.id)}
      <button
        type="button"
        class="vp__chip"
        class:vp__chip--active={activeTab === t.id}
        class:vp__chip--disabled={!t.enabled}
        disabled={!t.enabled}
        title={t.label}
        onclick={() => t.enabled && (activeTab = t.id)}
      >
        <span class="vp__chip-short">{t.short}</span>
      </button>
    {/each}
  </nav>

  <div class="vp__body">
    {#if activeTab === 'power' && flight.vesselId}
      <PowerView vesselId={flight.vesselId} />
    {/if}
  </div>
</FloatingWindow>

<style>
  /* Window chrome — let the FloatingWindow primitive own drag /
     resize, dress it here with Nova's visual language. The :global
     selector reaches into the package's class names because the
     FloatingWindow component scopes its own styles. */

  :global(.fw) {
    background: var(--bg-panel-strong);
    border: 1px solid var(--line-accent);
    color: var(--fg);
    font-family: var(--font-mono);
    font-size: 11px;
    letter-spacing: 0.02em;
    box-shadow: inset 0 0 0 1px rgba(126, 245, 184, 0.05);
    backdrop-filter: blur(14px) saturate(125%);
    -webkit-backdrop-filter: blur(14px) saturate(125%);
  }

  :global(.fw__header) {
    min-height: 26px;
    padding: 4px 10px;
    border-bottom: 1px solid var(--line);
  }

  :global(.fw__body) {
    padding: 10px;
  }

  /* Header content. */
  .vp__head {
    flex: 1 1 auto;
    display: flex;
    align-items: center;
    gap: 10px;
    min-width: 0;
  }
  .vp__brand {
    font-family: var(--font-display);
    font-size: 13px;
    letter-spacing: 0.18em;
    color: var(--accent);
    text-shadow: 0 0 6px var(--accent-glow);
    flex: 0 0 auto;
  }
  .vp__head-spacer {
    flex: 1 1 auto;
  }
  .vp__name {
    flex: 0 1 auto;
    min-width: 0;
    font-family: var(--font-mono);
    font-size: 11px;
    color: var(--fg-dim);
    text-transform: uppercase;
    letter-spacing: 0.16em;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  /* Subsystem chips — sub-tab navigation. Borrowed visually from the
     workbench EngineeringPanel prototype: 1 px-bordered cells with a
     bright accent on the active chip. Disabled chips render at half
     opacity to advertise "more views coming" without inviting clicks. */
  .vp__tabs {
    display: flex;
    gap: 4px;
    margin: -10px -10px 8px;
    padding: 6px 10px;
    border-bottom: 1px solid var(--line);
    background: var(--bg-elev);
  }
  .vp__chip {
    flex: 0 0 auto;
    padding: 3px 8px;
    background: transparent;
    border: 1px solid var(--line);
    color: var(--fg-dim);
    font-family: var(--font-display);
    font-size: 11px;
    letter-spacing: 0.14em;
    cursor: pointer;
    transition: color 160ms ease, border-color 160ms ease, background 160ms ease;
  }
  .vp__chip:hover:not(.vp__chip--disabled) {
    color: var(--accent);
    border-color: var(--accent-dim);
  }
  .vp__chip--active {
    color: var(--accent);
    border-color: var(--accent);
    background: rgba(126, 245, 184, 0.08);
    text-shadow: 0 0 6px var(--accent-glow);
  }
  .vp__chip--disabled {
    color: var(--fg-mute);
    cursor: not-allowed;
    opacity: 0.5;
  }
  .vp__chip-short {
    font-variant-numeric: tabular-nums;
  }

  .vp__body {
    color: var(--fg);
    font-family: var(--font-mono);
    font-size: 11px;
  }
</style>
