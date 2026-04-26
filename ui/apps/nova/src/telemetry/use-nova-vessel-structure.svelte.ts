// Subscribe to the per-vessel structure topic. Returns a reactive
// reference whose `current` updates when the vessel's part list,
// hierarchy, or tags change. Accepts either a plain string id or
// a getter function — the getter form lets the caller bind to a
// reactive vesselId (e.g. from useFlightData).

import { getKsp } from '@dragonglass/telemetry/svelte';
import {
  NovaVesselStructureTopic,
  decodeStructure,
  type NovaVesselStructure,
} from './nova-topics';

export function useNovaVesselStructure(
  vesselId: string | (() => string | undefined),
): { readonly current: NovaVesselStructure | undefined } {
  const telemetry = getKsp();
  let current = $state<NovaVesselStructure | undefined>(undefined);

  $effect(() => {
    const id = typeof vesselId === 'function' ? vesselId() : vesselId;
    if (!id) {
      current = undefined;
      return;
    }
    const t = NovaVesselStructureTopic(id);
    return telemetry.subscribe(t, (frame) => {
      current = decodeStructure(frame);
    });
  });

  return {
    get current() {
      return current;
    },
  };
}
