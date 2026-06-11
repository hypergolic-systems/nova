<script lang="ts">
  // Editor-scope analogue of the flight VesselPanel. Same chassis — a
  // right-docked SideRack with a stack of multi-open accordion sections
  // beneath a permanent ship-identity header — but the section
  // inventory is decoupled from flight's. Flight sections describe a
  // vessel in operation (PWR/THM/PRP/SCI/…); editor sections describe a
  // vessel under construction.
  //
  // Wired sections: TANKS (per-tank loadout editing), POWER (the flight
  // PowerView in editor mode — the per-part NovaPart/<id> wire resolves
  // against the live ShipConstruct), and STAGING (the same StagingStack
  // instrument flight uses, folded into the rack instead of floating).
  //
  // Two layout deltas from the flight rack:
  //   • Top inset clears KSP's launch/save/load button row in the
  //     editor's top-right, where the flight rack only had to clear its
  //     own 48-px FlightTopBar.
  //   • A distinct localStorage prefix so the editor rack's width +
  //     collapsed disposition is independent of the flight rack's.

  import { onMount } from 'svelte';
  import { StagingStack } from '@dragonglass/instruments';
  import { useNovaEditorShipStructure } from '../../telemetry/use-nova-editor-ship-structure.svelte';
  import { useNovaEditorParts } from '../../telemetry/use-nova-parts.svelte';
  import SideRack from '../SideRack.svelte';
  import Accordion from '../common/Accordion.svelte';
  import AccordionSection from '../common/AccordionSection.svelte';
  import TanksView from './TanksView.svelte';
  import PowerView from '../power/PowerView.svelte';

  interface Props {
    /** Part id of the most recent right-click PAW pulse, used by
     *  TanksView to focus + auto-expand the matching row. */
    focusPartId: string | null;
  }
  const { focusPartId }: Props = $props();

  // Clears KSP's editor button row (New/Load/Save/Launch) anchored in
  // the top-right. Larger than flight's 48-px FlightTopBar inset; tune
  // against the live VAB if the rack tucks under or floats below the
  // buttons.
  const EDITOR_RACK_TOP = 56;

  type SectionId = 'tanks' | 'power' | 'staging';

  const STORAGE_KEY = 'nova.editor.rack.sections';

  // Default disposition: tanks + power open (the editor's two primary
  // readouts), staging collapsed — it's empty for single-stage craft
  // (StagingStack drops the active stage), so an open-by-default
  // staging section would show a blank body on simple builds.
  const DEFAULTS: Record<SectionId, boolean> = {
    tanks: true,
    power: true,
    staging: false,
  };

  let open = $state<Record<SectionId, boolean>>({ ...DEFAULTS });

  // Per-section content presence, mirrored from each view's $bindable
  // hasContent. Default true so sections render optimistically on the
  // first frame before the view's $effect runs. Staging has no
  // hasContent feed (StagingStack self-gates internally), so it's not
  // tracked here — its section is always present.
  let has = $state<Record<'tanks' | 'power', boolean>>({
    tanks: true,
    power: true,
  });

  onMount(() => {
    try {
      const raw = localStorage.getItem(STORAGE_KEY);
      if (!raw) return;
      const parsed = JSON.parse(raw) as Partial<Record<SectionId, boolean>>;
      for (const k of Object.keys(DEFAULTS) as SectionId[]) {
        if (typeof parsed[k] === 'boolean') open[k] = parsed[k] as boolean;
      }
    } catch {
      /* corrupt storage — keep defaults silently */
    }
  });

  $effect(() => {
    const snapshot: Record<SectionId, boolean> = {
      tanks: open.tanks,
      power: open.power,
      staging: open.staging,
    };
    try {
      localStorage.setItem(STORAGE_KEY, JSON.stringify(snapshot));
    } catch {
      /* quota / disabled — ignore */
    }
  });

  const structureRef = useNovaEditorShipStructure();
  const shipName = $derived(structureRef.current?.name ?? '');
</script>

<SideRack top={EDITOR_RACK_TOP} storageKey="nova.editor.rack">
  <!-- IDENTITY — always-visible anchor above the accordion stack.
       Brand differs from flight (`◇ VESSEL · EDIT`) so the rack
       announces its scene context; the ship name comes from the
       editor ship-structure topic, not per-vessel flight state. -->
  <header class="evh">
    <span class="evh__brand">◇ VESSEL · EDIT</span>
    <span class="evh__name" title={shipName || 'Unnamed craft'}>
      {shipName || '—'}
    </span>
  </header>

  <Accordion>
    <!-- TANKS — per-tank loadout editing. Auto-hides until the ship
         has at least one tank part. -->
    <AccordionSection
      id="tanks" title="Tanks"
      bind:open={open.tanks}
      vacant={!has.tanks}
    >
      <TanksView {focusPartId} bind:hasContent={has.tanks} />
    </AccordionSection>

    <!-- POWER — generation/consumption/storage tree for the design
         under construction. -->
    <AccordionSection
      id="power" title="Power"
      bind:open={open.power}
      vacant={!has.power}
    >
      <PowerView
        mode="editor"
        parts={() => useNovaEditorParts()}
        bind:hasContent={has.power}
      />
    </AccordionSection>

    <!-- STAGING — the flight StagingStack folded into the rack
         (KSP's stock editor stager is suppressed via the
         `editor/staging` capability in init.ts). Always present;
         StagingStack renders nothing for craft with no queued
         stages, so there's no separate vacant gate. -->
    <AccordionSection id="staging" title="Staging" bind:open={open.staging}>
      <div class="evh__staging">
        <StagingStack />
      </div>
    </AccordionSection>
  </Accordion>
</SideRack>

<style>
  /* Ship-identity header — mirrors the flight VesselHeader's etched
     chassis-bay treatment, with an added brand line announcing the
     editor scene. */
  .evh {
    display: flex;
    flex-direction: column;
    gap: 3px;
    padding: 11px 14px 10px 14px;
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

  .evh__brand {
    font-family: var(--font-display);
    font-size: 11px;
    letter-spacing: 0.18em;
    color: var(--accent);
    text-shadow: 0 0 6px var(--accent-glow);
  }

  .evh__name {
    font-family: var(--font-display);
    font-size: 16px;
    line-height: 1.05;
    letter-spacing: 0.12em;
    text-transform: uppercase;
    color: var(--fg-dim);
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  /* StagingStack authors at 120px @ zoom 0.67 (~80px effective) and
     left-aligns inside the rack body. The flex wrapper keeps it from
     stretching and gives the cards a little breathing room from the
     section's left padding. */
  .evh__staging {
    display: flex;
    justify-content: flex-start;
  }
</style>
