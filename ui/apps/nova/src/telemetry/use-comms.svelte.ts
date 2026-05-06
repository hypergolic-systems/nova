// Subscribe to a per-vessel NovaComms topic. Same shape as
// `useOrbit`; the HUD top bar pairs the two on the active vessel.

import { getKsp } from '@dragonglass/telemetry/svelte';
import {
  NovaCommsTopic,
  decodeComms,
  type NovaComms,
} from './nova-topics';

export function useComms(
  vesselId: string | (() => string | undefined),
): { readonly current: NovaComms | undefined } {
  const telemetry = getKsp();
  let current = $state<NovaComms | undefined>(undefined);

  $effect(() => {
    const id = typeof vesselId === 'function' ? vesselId() : vesselId;
    if (!id) {
      current = undefined;
      return;
    }
    const t = NovaCommsTopic(id);
    return telemetry.subscribe(t, (frame) => {
      current = decodeComms(frame);
    });
  });

  return {
    get current() {
      return current;
    },
  };
}
