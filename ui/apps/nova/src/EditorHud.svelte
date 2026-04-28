<script lang="ts">
  // Editor-scene HUD root. Owns the right-click context menu that
  // replaces stock KSP's PAW (Dragonglass's UIPartActionController
  // patch already vetoes stock when `editor/paw` is declared, fanning
  // the right-click pulse out via PawTopic).
  //
  // Flow:
  //   1. Track cursor via a global mousedown listener — each PawTopic
  //      pulse arrives shortly after the click that produced it, so
  //      the most-recent mousedown coords are the correct anchor.
  //   2. On pulse, subscribe to NovaPartTopic just long enough to read
  //      the first frame (tells us what components the part has).
  //      Unsubscribe immediately — DG's OpDispatcher re-attaches the
  //      Topic on demand when `setTankConfig` lands later.
  //   3. If `tank.length > 0`, render a ContextMenu with the
  //      TANK_PRESETS entries; on click, fire setTankConfig and
  //      dismiss. Otherwise no menu (per current scope).

  import { onDestroy } from 'svelte';
  import { getKsp } from '@dragonglass/telemetry/svelte';
  import { PawTopic } from '@dragonglass/telemetry/core';
  import { ContextMenu, type MenuItem } from '@dragonglass/instruments';
  import { NovaPartTopic, decodePart } from './telemetry/nova-topics';
  import { TANK_PRESETS } from './editor/tank-presets';

  const ksp = getKsp();

  let menu = $state<{ items: MenuItem[]; x: number; y: number } | null>(null);

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

  const unsubPaw = ksp.subscribe(PawTopic, (raw) => {
    const partId = Array.isArray(raw) && typeof raw[0] === 'string' ? raw[0] : null;
    if (!partId) return;

    const partTopic = NovaPartTopic(partId);
    const x = cursorX;
    const y = cursorY;
    let consumed = false;
    let unsub: (() => void) | null = null;

    const onFrame = (frame: unknown) => {
      if (consumed) return;
      consumed = true;
      // Defer the unsub until after the synchronous cached-frame
      // dispatch returns; calling unsub here when subscribe hasn't
      // yet returned its handle would leak the listener.
      queueMicrotask(() => unsub?.());

      const part = decodePart(frame as Parameters<typeof decodePart>[0]);
      if (part.tank.length === 0) {
        menu = null;
        return;
      }
      menu = {
        x,
        y,
        items: TANK_PRESETS.map((p) => ({
          label: p.label,
          onSelect: () => ksp.send(partTopic, 'setTankConfig', p.id),
        })),
      };
    };

    unsub = ksp.subscribe(partTopic, onFrame);
    if (consumed) unsub();
  });

  onDestroy(unsubPaw);
</script>

{#if menu}
  <ContextMenu items={menu.items} x={menu.x} y={menu.y} onDismiss={() => (menu = null)} />
{/if}
