// Subscribe to a per-vessel NovaOrbit topic. Mirrors `useNovaPart`'s
// shape — accepts either a static vesselId or a getter so callers can
// pass a reactive `useFlightData().vesselId` derivation directly.

import { getKsp } from '@dragonglass/telemetry/svelte';
import {
  NovaOrbitTopic,
  decodeOrbit,
  type NovaOrbit,
} from './nova-topics';

export function useOrbit(
  vesselId: string | (() => string | undefined),
): { readonly current: NovaOrbit | undefined } {
  const telemetry = getKsp();
  let current = $state<NovaOrbit | undefined>(undefined);

  $effect(() => {
    const id = typeof vesselId === 'function' ? vesselId() : vesselId;
    if (!id) {
      current = undefined;
      return;
    }
    const t = NovaOrbitTopic(id);
    return telemetry.subscribe(t, (frame) => {
      current = decodeOrbit(frame);
    });
  });

  return {
    get current() {
      return current;
    },
  };
}
