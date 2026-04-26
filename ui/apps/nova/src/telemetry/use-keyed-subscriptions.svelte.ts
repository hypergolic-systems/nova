// Maintain per-key topic subscriptions over a reactive set of keys.
// When the key set changes, surviving keys keep their subscription
// and last-decoded state; departing keys unsubscribe; new keys
// subscribe. Consumers read each key's latest state via `get(key)`,
// which is reactive — re-derives fire on add, remove, and state
// updates.
//
// This wraps three Svelte 5 / Dragonglass-transport gotchas the
// callers shouldn't have to think about:
//
//   1. The `subscribe()` call is wrapped in `untrack` so the
//      synchronous cached-frame fire (which calls the cb inline)
//      doesn't bind reactive reads inside the cb to the surrounding
//      effect, which would self-trigger a depth-exceed loop.
//   2. Per-key state lives in a `SvelteMap` — the Svelte-blessed
//      reactive collection for dynamic key sets. Writes via
//      `set` / `delete` fire per-key signals; consumers reading
//      `get(key)` re-derive on add / change / remove.
//   3. Diff-reconciliation of subscriptions across key set changes
//      keeps the wire subscription stable for parts that didn't
//      come or go — both for transport efficiency and so the last-
//      decoded state carries through (no flicker through undefined).
//
// Single-key consumers — `useNovaPart`, `useNovaVesselStructure` —
// don't need this helper; they're already trivial single subs that
// don't run into the multi-key reconciliation hazards.

import { onDestroy, untrack } from 'svelte';
import { SvelteMap } from 'svelte/reactivity';
import { getKsp } from '@dragonglass/telemetry/svelte';
import type { Topic } from '@dragonglass/telemetry/core';

export interface KeyedSubscriptions<K, T> {
  /** Latest decoded state for `key`, or `undefined` until the
   *  first wire frame for that key arrives. Reactive. */
  get(key: K): T | undefined;
}

export function useKeyedSubscriptions<K, F, T>(
  keys: () => readonly K[],
  // `Ops` is `unknown` (not the default `never`) so callers can hand
  // us topics whose ops interface is non-empty — we don't `send`,
  // so the ops parameter is irrelevant here.
  topicFor: (key: K) => Topic<F, unknown>,
  decode: (frame: F) => T,
): KeyedSubscriptions<K, T> {
  const telemetry = getKsp();
  const subs = new Map<K, () => void>();
  const states = new SvelteMap<K, T>();

  $effect(() => {
    const wanted = new Set(keys());

    // Drop departed keys.
    for (const [k, unsub] of subs) {
      if (!wanted.has(k)) {
        unsub();
        subs.delete(k);
        states.delete(k);
      }
    }

    // Subscribe to new keys. `untrack` shields this effect from
    // the synchronous cached-frame fire that `subscribe` performs
    // inline — without it, a reactive read inside the cb would
    // bind that signal as a dep of this effect, and a write to
    // the same signal in the cb would self-trigger a loop. Async
    // wire frames run later from the websocket message handler,
    // outside any tracking context, and just write through.
    untrack(() => {
      for (const k of wanted) {
        if (subs.has(k)) continue;
        const unsub = telemetry.subscribe(topicFor(k), (frame) => {
          states.set(k, decode(frame));
        });
        subs.set(k, unsub);
      }
    });
  });

  onDestroy(() => {
    for (const u of subs.values()) u();
    subs.clear();
  });

  return {
    get(k: K) {
      return states.get(k);
    },
  };
}
