// Singleton subscription to Nova's timewarp topic. Same lazy-store
// pattern as `useNovaScienceArchive`: a module-level reactive state
// shared across every consumer; the first call wires the subscription,
// subsequent calls return the same reference. Subscription is never
// torn down — the topic emits only on warp transitions, costs nothing
// at rest, and the HUD top bar always wants to read it.

import { getKsp } from '@dragonglass/telemetry/svelte';
import {
  NovaTimewarpTopic,
  decodeTimewarp,
  type NovaTimewarp,
} from './nova-topics';

const store = $state<{ current: NovaTimewarp | undefined }>({ current: undefined });
let subscribed = false;

export function useTimewarp(): { readonly current: NovaTimewarp | undefined } {
  if (!subscribed) {
    subscribed = true;
    const ksp = getKsp();
    ksp.subscribe(NovaTimewarpTopic, (frame) => {
      store.current = decodeTimewarp(frame);
    });
  }
  return store;
}
