// Subscribe to the per-vessel dynamic-state topic. Returns a reactive
// reference whose `current` updates when the vessel's name, situation,
// body, mass, part count, or crew change. Accepts either a plain
// string id or a getter — the getter form binds to a reactive vesselId
// (e.g. from useFlightData).

import { getKsp } from '@dragonglass/telemetry/svelte';
import {
  NovaVesselStateTopic,
  decodeVesselState,
  type NovaVesselState,
} from './nova-topics';

export function useNovaVesselState(
  vesselId: string | (() => string | undefined),
): { readonly current: NovaVesselState | undefined } {
  const telemetry = getKsp();
  let current = $state<NovaVesselState | undefined>(undefined);

  $effect(() => {
    const id = typeof vesselId === 'function' ? vesselId() : vesselId;
    if (!id) {
      current = undefined;
      return;
    }
    const t = NovaVesselStateTopic(id);
    return telemetry.subscribe(t, (frame) => {
      current = decodeVesselState(frame);
    });
  });

  return {
    get current() {
      return current;
    },
  };
}
