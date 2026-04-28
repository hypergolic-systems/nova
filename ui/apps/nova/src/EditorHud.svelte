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
  //   2. On pulse, subscribe to the per-part NovaPartTopic.
  //      NovaSubscriptionManager attaches the topic to the part
  //      GameObject; the broadcaster emits within ~100 ms.
  //   3. First frame: if `tank.length > 0`, render a ContextMenu with
  //      the six TANK_PRESETS entries, dispatching `setTankConfig` on
  //      select. Otherwise the menu stays closed (per current scope:
  //      no menu for parts Nova has no opinions about).
  //   4. The per-part subscription is one-shot — unsubscribe after the
  //      first frame so we don't keep the topic alive once the menu
  //      is built.

  import { onDestroy } from 'svelte';
  import { getKsp } from '@dragonglass/telemetry/svelte';
  import { PawTopic } from '@dragonglass/telemetry/core';
  import { ContextMenu, type MenuItem } from '@dragonglass/instruments';
  import { NovaPartTopic, decodePart } from './telemetry/nova-topics';
  import { TANK_PRESETS } from './editor/tank-presets';

  const ksp = getKsp();

  let menu = $state<{ items: MenuItem[]; x: number; y: number } | null>(null);
  let pendingSub: (() => void) | null = null;

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

    pendingSub?.();
    pendingSub = null;

    const partTopic = NovaPartTopic(partId);
    const x = cursorX;
    const y = cursorY;

    pendingSub = ksp.subscribe(partTopic, (frame) => {
      const part = decodePart(frame);
      if (part.tank.length === 0) {
        menu = null;
      } else {
        menu = {
          x,
          y,
          items: TANK_PRESETS.map((p) => ({
            label: p.label,
            onSelect: () => ksp.send(partTopic, 'setTankConfig', p.id),
          })),
        };
      }
      pendingSub?.();
      pendingSub = null;
    });
  });

  onDestroy(() => {
    unsubPaw();
    pendingSub?.();
  });
</script>

{#if menu}
  <ContextMenu items={menu.items} x={menu.x} y={menu.y} onDismiss={() => (menu = null)} />
{/if}
