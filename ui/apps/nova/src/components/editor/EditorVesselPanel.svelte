<script lang="ts">
  // Editor-scope analogue of VesselPanel. Same chrome — FloatingWindow,
  // brand header, chip-style tab nav — but the tab inventory and views
  // are deliberately decoupled from the in-flight panel. Flight tabs
  // (PWR/THM/RES/PRP/RCS/ATT/SCI) describe a vessel in operation;
  // editor tabs describe a vessel under construction (TANKS first;
  // MASS/ΔV/STAGING/CREW slated). Sharing chrome keeps the visual
  // rhythm; not sharing views keeps each scene's surface honest.
  //
  // The shell is built now with TANKS as the only enabled tab and the
  // rest as disabled placeholders so the future surface is locked in
  // without scaffolding empty views.

  import { FloatingWindow } from '@dragonglass/windows';
  import { useNovaEditorShipStructure } from '../../telemetry/use-nova-editor-ship-structure.svelte';
  import TanksView from './TanksView.svelte';

  interface Props {
    /** Part id of the most recent right-click PAW pulse, used by
     *  TanksView to focus + auto-expand the matching row. */
    focusPartId: string | null;
  }
  const { focusPartId }: Props = $props();

  const MIN_W = 340;
  const MIN_H = 320;
  const EDGE_MARGIN = 16;

  // Default position — left edge in the editor (StagingStack owns the
  // right side, mirroring KSP's stock VAB convention). Differs from
  // the flight VesselPanel's right-edge dock; the editor scene's
  // visual rhythm is "stages on the right, vessel readout on the
  // left", so muscle memory follows the scene's geometry, not the
  // flight panel.
  const initialW = MIN_W;
  const initialH = Math.max(MIN_H, Math.round(window.innerHeight * 0.75));
  const initialX = EDGE_MARGIN;
  const initialY = Math.max(EDGE_MARGIN, Math.round((window.innerHeight - initialH) / 2));

  // STAGING is intentionally not a tab here — it lives outside the
  // panel as a free-floating instrument (mirroring flight, where
  // StagingStack is its own surface separate from VesselPanel). See
  // EditorHud.svelte's bottom-left mount.
  type TabId = 'tanks' | 'mass' | 'dv' | 'crew';

  interface Tab {
    id: TabId;
    short: string;
    label: string;
    enabled: boolean;
  }

  const tabs: Tab[] = [
    { id: 'tanks',   short: 'TANKS',   label: 'Tank Volumes',  enabled: true  },
    { id: 'mass',    short: 'MASS',    label: 'Mass Breakdown', enabled: false },
    { id: 'dv',      short: 'ΔV',      label: 'Delta-V',        enabled: false },
    { id: 'crew',    short: 'CREW',    label: 'Crew',           enabled: false },
  ];

  const structureRef = useNovaEditorShipStructure();
  const shipName = $derived(structureRef.current?.name ?? '');

  let activeTab = $state<TabId>('tanks');
  let z = $state(100);

  function raise(): void { z++; }
</script>

<FloatingWindow
  defaultPos={{ x: initialX, y: initialY }}
  defaultSize={{ w: initialW, h: initialH }}
  minSize={{ w: MIN_W, h: MIN_H }}
  {z}
  onRaise={raise}
>
  {#snippet header()}
    <div class="evp__head">
      <span class="evp__brand">◇ VESSEL · EDIT</span>
      <span class="evp__head-spacer"></span>
      <span class="evp__name">{shipName}</span>
    </div>
  {/snippet}

  <nav class="evp__tabs" aria-label="Editor subsystems">
    {#each tabs as t (t.id)}
      <button
        type="button"
        class="evp__chip"
        class:evp__chip--active={activeTab === t.id}
        class:evp__chip--disabled={!t.enabled}
        disabled={!t.enabled}
        title={t.label}
        onclick={() => t.enabled && (activeTab = t.id)}
      >
        <span class="evp__chip-short">{t.short}</span>
      </button>
    {/each}
  </nav>

  <div class="evp__scroll">
    {#if activeTab === 'tanks'}
      <TanksView {focusPartId} />
    {/if}
  </div>
</FloatingWindow>

<style>
  /* Reuse the same window chrome the flight VesselPanel established —
     the FloatingWindow primitive scopes its own styles, so the :global
     selectors reach in for the bg / border / header treatment. The
     flight panel's own styles double-apply harmlessly when both panels
     mount in the same scene; only one of FlightHud / EditorHud is
     active at any time, so in practice this stays single-source. */

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
    padding: 0;
    overflow: hidden;
    display: flex;
    flex-direction: column;
    min-height: 0;
  }

  .evp__head {
    flex: 1 1 auto;
    display: flex;
    align-items: center;
    gap: 10px;
    min-width: 0;
  }
  /* Brand differs from flight (`◇ VESSEL · EDIT` vs `◇ VESSEL`) so the
     panel announces its scene context directly in the header. */
  .evp__brand {
    font-family: var(--font-display);
    font-size: 13px;
    letter-spacing: 0.18em;
    color: var(--accent);
    text-shadow: 0 0 6px var(--accent-glow);
    flex: 0 0 auto;
  }
  .evp__head-spacer { flex: 1 1 auto; }
  .evp__name {
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

  .evp__tabs {
    flex: 0 0 auto;
    display: flex;
    gap: 4px;
    padding: 6px 10px;
    border-bottom: 1px solid var(--line);
    background: var(--bg-elev);
  }
  .evp__chip {
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
  .evp__chip:hover:not(.evp__chip--disabled) {
    color: var(--accent);
    border-color: var(--accent-dim);
  }
  .evp__chip--active {
    color: var(--accent);
    border-color: var(--accent);
    background: rgba(126, 245, 184, 0.08);
    text-shadow: 0 0 6px var(--accent-glow);
  }
  .evp__chip--disabled {
    color: var(--fg-mute);
    cursor: not-allowed;
    opacity: 0.5;
  }
  .evp__chip-short { font-variant-numeric: tabular-nums; }

  .evp__scroll {
    flex: 1 1 0;
    min-height: 0;
    padding: 10px;
    overflow-y: scroll;
    color: var(--fg);
    font-family: var(--font-mono);
    font-size: 11px;
  }
  .evp__scroll::-webkit-scrollbar {
    width: 8px;
    height: 8px;
  }
  .evp__scroll::-webkit-scrollbar-track {
    background: rgba(0, 0, 0, 0.25);
    border-left: 1px solid var(--line);
  }
  .evp__scroll::-webkit-scrollbar-thumb {
    background: rgba(126, 245, 184, 0.18);
    border: 1px solid transparent;
    background-clip: padding-box;
    transition: background 200ms ease;
  }
  .evp__scroll::-webkit-scrollbar-thumb:hover {
    background: rgba(126, 245, 184, 0.42);
    background-clip: padding-box;
  }
  .evp__scroll::-webkit-scrollbar-corner {
    background: rgba(0, 0, 0, 0.25);
  }
</style>
