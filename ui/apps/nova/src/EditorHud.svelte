<script lang="ts">
  // Editor-scene HUD root. Two things share the right-click PAW pulse:
  //
  //   1. EditorVesselPanel uses it as a `focusPartId` signal — the
  //      panel's TANKS view scrolls + auto-expands the matching tank
  //      row. Tank-loadout editing lives there as one-click chips, so
  //      the menu doesn't duplicate that surface.
  //
  //   2. The right-click ContextMenu (this file) handles per-part
  //      one-shot actions that don't merit a panel row: the decoupler
  //      "Full Separation" toggle today, and whatever else gets layered
  //      on later (engine-plate variant, fairing base mode, …).
  //
  // Dragonglass's UIPartActionController patch vetoes the stock PAW
  // when Nova declares `editor/paw`, so right-clicks fan out via
  // PawTopic without a stock window opening underneath.
  //
  // Flow for the menu:
  //   1. Track cursor via a capture-phase mousedown listener — each
  //      PawTopic pulse arrives shortly after the click that produced
  //      it, so the most-recent mousedown coords anchor the menu at
  //      the click.
  //   2. On pulse, subscribe one-shot to NovaPartTopic. The first
  //      frame's component list decides what items appear.
  //   3. Items are derived by `buildItems(part)` — decoder kinds map
  //      to MenuItem entries. Empty list → no menu opens.
  //   4. Unsubscribe after the first frame: we don't keep the per-part
  //      feed alive once we've snapshot it for menu-build time.

  import { onDestroy } from 'svelte';
  import { getKsp } from '@dragonglass/telemetry/svelte';
  import { PawTopic } from '@dragonglass/telemetry/core';
  import { StagingStack } from '@dragonglass/instruments';
  import { ContextMenu, type MenuItem } from '@dragonglass/instruments';
  import EditorVesselPanel from './components/editor/EditorVesselPanel.svelte';
  import { NovaPartTopic, decodePart, type NovaPart } from './telemetry/nova-topics';

  const ksp = getKsp();

  // ----- focusPartId (TANKS row focus) ---------------------------

  // Most recent right-clicked part id. Reset is automatic: re-clicking
  // the same part publishes the same id, which the panel sees as a new
  // pulse via the $effect dependency on `focusPartId`. Cleared back to
  // null after the panel has had a tick to react — avoids the row
  // staying anchored after the user has moved on.
  let focusPartId = $state<string | null>(null);

  // ----- ContextMenu state ---------------------------------------

  let menu = $state<{ items: MenuItem[]; x: number; y: number } | null>(null);
  let pendingPartSub: (() => void) | null = null;

  let cursorX = 0;
  let cursorY = 0;
  $effect(() => {
    const onDown = (e: MouseEvent) => {
      cursorX = e.clientX;
      cursorY = e.clientY;
    };
    document.addEventListener('mousedown', onDown, true);
    return () => document.removeEventListener('mousedown', onDown, true);
  });

  // ----- Item factory --------------------------------------------

  // Builds the per-component menu rows for a part. Add a block per
  // virtual-component kind that exposes a player toggle here; each
  // block is independent so a part with multiple kinds gets a
  // combined menu in section order.
  function buildItems(part: NovaPart, partId: string): MenuItem[] {
    const items: MenuItem[] = [];

    for (const decoupler of part.decoupler) {
      const on = decoupler.fullSeparation;
      const usable = decoupler.canFullSeparate;
      // Checkbox-style item. MenuItem has no native checked state, so
      // encode it in the label with a leading marker. `[x]` / `[ ]`
      // reads cleanly in the monospace menu font.
      const marker = !usable ? '[—]' : on ? '[x]' : '[ ]';
      const suffix = !usable ? '  (radial — N/A)' : '';
      items.push({
        label: `${marker} Full Separation${suffix}`,
        disabled: !usable,
        onSelect: () => {
          ksp.send(NovaPartTopic(partId), 'setFullSeparation', !on);
        },
      });
    }

    return items;
  }

  // ----- PawTopic subscription -----------------------------------

  const unsubPaw = ksp.subscribe(PawTopic, (raw) => {
    const partId = Array.isArray(raw) && typeof raw[0] === 'string' ? raw[0] : null;
    if (!partId) return;

    // Focus pulse for the vessel panel — same toggle-through-null
    // trick so re-clicking the same part fires the $effect again.
    focusPartId = null;
    queueMicrotask(() => { focusPartId = partId; });

    // Cancel any in-flight one-shot from a previous click.
    pendingPartSub?.();
    pendingPartSub = null;

    const partTopic = NovaPartTopic(partId);
    const x = cursorX;
    const y = cursorY;

    pendingPartSub = ksp.subscribe(partTopic, (frame) => {
      // Tear down before mutating menu — `pendingPartSub` becomes
      // stale once we've consumed the first frame.
      pendingPartSub?.();
      pendingPartSub = null;

      const part = decodePart(frame);
      const items = buildItems(part, partId);
      menu = items.length > 0 ? { x, y, items } : null;
    });
  });

  onDestroy(() => {
    unsubPaw();
    pendingPartSub?.();
  });
</script>

<EditorVesselPanel {focusPartId} />

{#if menu}
  <ContextMenu items={menu.items} x={menu.x} y={menu.y} onDismiss={() => (menu = null)} />
{/if}

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
