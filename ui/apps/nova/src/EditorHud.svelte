<script lang="ts">
  // Editor-scene HUD root. Mounts the persistent EditorVesselPanel and
  // forwards right-click PAW pulses through to it as `focusPartId` so
  // the panel can scroll the matching tank-row into view and force it
  // expanded. The panel itself is always-on while the editor scene is
  // loaded — there's no toggle yet (parity with the flight VesselPanel,
  // which is also always-on).
  //
  // Right-click on a non-tank part is currently a no-op. The previous
  // tank-preset context menu is gone — preset selection lives inside
  // the panel's TankRowEditor as one-click chips.

  import { onDestroy } from 'svelte';
  import { getKsp } from '@dragonglass/telemetry/svelte';
  import { PawTopic } from '@dragonglass/telemetry/core';
  import { StagingStack } from '@dragonglass/instruments';
  import EditorVesselPanel from './components/editor/EditorVesselPanel.svelte';

  const ksp = getKsp();

  // Most recent right-clicked part id. Reset is automatic: re-clicking
  // the same part publishes the same id, which the panel sees as a new
  // pulse via the $effect dependency on `focusPartId`. Cleared back to
  // null after the panel has had a tick to react — avoids the row
  // staying anchored after the user has moved on.
  let focusPartId = $state<string | null>(null);

  const unsubPaw = ksp.subscribe(PawTopic, (raw) => {
    const partId = Array.isArray(raw) && typeof raw[0] === 'string' ? raw[0] : null;
    if (!partId) return;
    // Pulse semantics: set, then clear next tick. The $effect in the
    // panel keys off the value changing, so set→same-value would not
    // re-fire. Toggle through null first to guarantee a change signal
    // even when the user re-right-clicks the same part.
    focusPartId = null;
    queueMicrotask(() => { focusPartId = partId; });
  });
  onDestroy(unsubPaw);
</script>

<EditorVesselPanel {focusPartId} />

<!-- Editor staging mirrors flight's StagingStack but anchored bottom-
     right (KSP's editor convention has the stage list on the right
     side of the screen — flipped from the bottom-left flight layout).
     Nova declares the `editor/staging` capability in hud.ts to
     suppress the stock stager so it doesn't paint on top. -->
<div class="nova-editor-staging">
  <StagingStack />
</div>

<style>
  .nova-editor-staging {
    position: fixed;
    bottom: 24px;
    right: 24px;
    z-index: 50;
    pointer-events: auto;
  }
</style>
