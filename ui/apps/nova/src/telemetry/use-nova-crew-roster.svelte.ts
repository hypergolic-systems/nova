// Subscribe to the per-vessel crew roster topic. Returns a reactive
// reference whose `current` updates when crew membership or assignment
// changes (EVA, transfer, dock/undock, vessel split). Accepts either a
// plain string id or a getter — the getter form binds to a reactive
// vesselId (e.g. from useFlightData).

import { getKsp } from '@dragonglass/telemetry/svelte';
import {
  NovaCrewRosterTopic,
  decodeCrewRoster,
  type NovaCrewRoster,
} from './nova-topics';

export function useNovaCrewRoster(
  vesselId: string | (() => string | undefined),
): { readonly current: NovaCrewRoster | undefined } {
  const telemetry = getKsp();
  let current = $state<NovaCrewRoster | undefined>(undefined);

  $effect(() => {
    const id = typeof vesselId === 'function' ? vesselId() : vesselId;
    if (!id) {
      current = undefined;
      return;
    }
    const t = NovaCrewRosterTopic(id);
    return telemetry.subscribe(t, (frame) => {
      current = decodeCrewRoster(frame);
    });
  });

  return {
    get current() {
      return current;
    },
  };
}
