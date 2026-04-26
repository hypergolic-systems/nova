// Subscribe to a single NovaPart topic. Cheaper variant of
// useNovaPartsByTag for callers that already know the partId they
// want (e.g. an inspector pinned to a specific part).

import { getKsp } from '@dragonglass/telemetry/svelte';
import {
  NovaPartTopic,
  decodePart,
  type NovaPart,
} from './nova-topics';

export function useNovaPart(
  partId: string | (() => string | undefined),
): { readonly current: NovaPart | undefined } {
  const telemetry = getKsp();
  let current = $state<NovaPart | undefined>(undefined);

  $effect(() => {
    const id = typeof partId === 'function' ? partId() : partId;
    if (!id) {
      current = undefined;
      return;
    }
    const t = NovaPartTopic(id);
    return telemetry.subscribe(t, (frame) => {
      current = decodePart(frame);
    });
  });

  return {
    get current() {
      return current;
    },
  };
}
