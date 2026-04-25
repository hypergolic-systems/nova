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

  // Layout classes (.hud, .navslot, .staging-stack, .navball-cluster) and
  // the per-instrument SVG styling are defined in flight.css inside
  // @dragonglass/instruments and bundled into Dragonglass's runtime.css,
  // which the sidecar shell auto-links. No CSS import needed here.

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
