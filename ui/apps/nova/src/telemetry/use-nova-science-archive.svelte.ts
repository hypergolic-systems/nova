// Singleton subscription to Nova's KSC-side science archive. Mirrors
// the lazy-store pattern of `useFlightData` — a module-level `$state`
// shared across every consumer; the first call wires up the topic
// subscription, every subsequent call returns the same reactive
// reference. Subscription is never torn down: the transport is
// durable for the app lifetime and the archive surfaces in only one
// place (the R&D Science tab), so refcount juggling buys nothing.

import { getKsp } from '@dragonglass/telemetry/svelte';
import {
  NovaScienceArchiveTopic,
  decodeScienceArchive,
  type NovaScienceArchive,
} from './nova-topics';

const EMPTY: NovaScienceArchive = {
  bodies:   [],
  subjects: new Map(),
};

const store = $state<{ current: NovaScienceArchive }>({ current: EMPTY });
let subscribed = false;

export function useNovaScienceArchive(): { readonly current: NovaScienceArchive } {
  if (!subscribed) {
    subscribed = true;
    const ksp = getKsp();
    ksp.subscribe(NovaScienceArchiveTopic, (frame) => {
      store.current = decodeScienceArchive(frame);
    });
  }
  return store;
}
