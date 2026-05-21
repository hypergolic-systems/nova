<script lang="ts">
  import { useFlightData, useFlightOps } from '@dragonglass/telemetry/svelte';
  import {
    Navball,
    CurvedTape,
    NavballIndicator,
    Propulsion,
    StagingStack,
    formatSurfaceSpeed,
    formatAltitude,
    SPEED_SCALE,
    ALTITUDE_SCALE,
  } from '@dragonglass/instruments';
  import VesselPanel from './components/VesselPanel.svelte';
  import FlightTopBar from './components/FlightTopBar.svelte';

  // Layout classes (.hud, .navslot, .staging-stack, .navball-cluster)
  // and per-instrument SVG styling come from @dragonglass/instruments/
  // flight.css. Dragonglass's runtime.css no longer ships them (those
  // styles moved out with the stock-UI removal in dragonglass@f1339cc1),
  // so Nova adopts the sheet itself in `flight.ts` via the CSS Module
  // import pattern from docs/mod-ui.md. No CSS import needed here.

  const s = useFlightData();
  const ops = useFlightOps();

  const speedVector = $derived(
    s.speedDisplayMode === 'orbit' ? s.orbitalVelocity
    : s.speedDisplayMode === 'target' ? s.targetVelocity
    : s.surfaceVelocity,
  );
  const speed = $derived(speedVector.length());
  const speedLabel = $derived(
    s.speedDisplayMode === 'orbit' ? 'ORBIT'
    : s.speedDisplayMode === 'target' ? 'TARGET'
    : 'SURFACE',
  );
</script>

<FlightTopBar />

<div class="hud hud--navball-only">
  <div class="navslot navslot--bottom-left">
    <div class="staging-stack">
      <Propulsion />
      <StagingStack />
    </div>
    <div class="navball-cluster">
      <Navball />
      <CurvedTape
        side="left"
        value={speed}
        modeLabel={speedLabel}
        scale={SPEED_SCALE}
        formatReadout={formatSurfaceSpeed}
      />
      <CurvedTape
        side="right"
        value={s.altitudeAsl}
        modeLabel="ALT"
        scale={ALTITUDE_SCALE}
        formatReadout={formatAltitude}
      />
      <NavballIndicator
        kind="rcs"
        active={s.rcs}
        onclick={() => ops.setRcs(!s.rcs)}
      />
      <NavballIndicator
        kind="sas"
        active={s.sas}
        onclick={() => ops.setSas(!s.sas)}
      />
    </div>
  </div>
</div>

<VesselPanel />
