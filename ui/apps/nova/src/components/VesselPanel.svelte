<script lang="ts">
  // Vessel rack — docked to the right edge of the viewport, full
  // height, with a stack of multi-open accordion sections. Replaces
  // the previous FloatingWindow + tabbed layout.
  //
  // Per-section open/closed state hydrates from localStorage so the
  // player's rack disposition (e.g. POWER open, others collapsed)
  // persists across scene reloads. The default disposition is
  // Vessel + Power open; everything else collapsed.
  //
  // Each section's content is gated on `flight.vesselId` so the rack
  // shell stays mounted even between vessels (during scene
  // transitions or save loads), while view bodies wait for an
  // active vessel.

  import { onMount } from 'svelte';
  import { useFlightData } from '@dragonglass/telemetry/svelte';
  import { useNovaParts } from '../telemetry/use-nova-parts.svelte';
  import SideRack from './SideRack.svelte';
  import Accordion from './common/Accordion.svelte';
  import AccordionSection from './common/AccordionSection.svelte';
  import VesselSection from './vessel/VesselSection.svelte';
  import SystemView from './system/SystemView.svelte';
  import PowerView from './power/PowerView.svelte';
  import ThermalView from './thermal/ThermalView.svelte';
  import PrpView from './prp/PrpView.svelte';
  import TanksView from './tanks/TanksView.svelte';
  import ScienceView from './science/ScienceView.svelte';

  type SectionId =
    | 'vessel'
    | 'system'
    | 'power'
    | 'thermal'
    | 'prp'
    | 'tank'
    | 'science';

  const STORAGE_KEY = 'nova.rack.sections';

  // Default rack disposition: identity + power open on first load.
  // The rest stay collapsed so the rack reads as a compact spine
  // until the player explicitly opens a subsystem.
  const DEFAULTS: Record<SectionId, boolean> = {
    vessel: true,
    system: false,
    power: true,
    thermal: false,
    prp: false,
    tank: false,
    science: false,
  };

  let open = $state<Record<SectionId, boolean>>({ ...DEFAULTS });

  onMount(() => {
    try {
      const raw = localStorage.getItem(STORAGE_KEY);
      if (!raw) return;
      const parsed = JSON.parse(raw) as Partial<Record<SectionId, boolean>>;
      // Merge over defaults so a freshly-added section gets its default
      // expansion (not `false` from an absent key).
      for (const k of Object.keys(DEFAULTS) as SectionId[]) {
        if (typeof parsed[k] === 'boolean') open[k] = parsed[k] as boolean;
      }
    } catch {
      // Corrupt storage — keep defaults silently.
    }
  });

  // Persist whenever any section toggles. $effect tracks the whole
  // record by deep-reading every key. Cheap (7 booleans, one write).
  $effect(() => {
    const snapshot: Record<SectionId, boolean> = {
      vessel: open.vessel,
      system: open.system,
      power: open.power,
      thermal: open.thermal,
      prp: open.prp,
      tank: open.tank,
      science: open.science,
    };
    try {
      localStorage.setItem(STORAGE_KEY, JSON.stringify(snapshot));
    } catch {
      /* quota / disabled — ignore */
    }
  });

  const flight = useFlightData();
</script>

<SideRack>
  <Accordion>
    <!-- VESSEL — identity + live state. Always-on (renders an empty
         dash row when no vessel is loaded). -->
    <VesselSection vesselId={() => flight.vesselId} bind:open={open.vessel} />

    <!-- SYSTEM — vessel-wide power/comms/staging overview. -->
    <AccordionSection id="system" title="System" bind:open={open.system}>
      {#if flight.vesselId}
        <SystemView vesselId={flight.vesselId} />
      {/if}
    </AccordionSection>

    <!-- POWER — solar, batteries, fuel cells, RTGs, draw/supply. -->
    <AccordionSection id="power" title="Power" bind:open={open.power}>
      {#if flight.vesselId}
        <PowerView parts={() => useNovaParts(() => flight.vesselId)} />
      {/if}
    </AccordionSection>

    <!-- THERMAL — radiators, coolers, heat budget. -->
    <AccordionSection id="thermal" title="Thermal" bind:open={open.thermal}>
      {#if flight.vesselId}
        <ThermalView vesselId={flight.vesselId} />
      {/if}
    </AccordionSection>

    <!-- PROPULSION — engines, gimbals, throttles. -->
    <AccordionSection id="prp" title="Propulsion" bind:open={open.prp}>
      {#if flight.vesselId}
        <PrpView vesselId={flight.vesselId} />
      {/if}
    </AccordionSection>

    <!-- TANKS — by-resource flow rates + per-tank breakdown. -->
    <AccordionSection id="tank" title="Tanks" bind:open={open.tank}>
      {#if flight.vesselId}
        <TanksView vesselId={flight.vesselId} />
      {/if}
    </AccordionSection>

    <!-- SCIENCE — experiments, storage, transmission. -->
    <AccordionSection id="science" title="Science" bind:open={open.science}>
      {#if flight.vesselId}
        <ScienceView vesselId={flight.vesselId} />
      {/if}
    </AccordionSection>
  </Accordion>
</SideRack>
