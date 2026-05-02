// Auto-subscribes to every part on the vessel whose tag list
// contains `tag`, and returns a reactive list of {struct, state}
// pairs. Subscriptions are diff-reconciled across structure
// publishes — surviving parts keep their wire subscription and
// last-decoded state; departing parts unsubscribe; new parts
// subscribe. The reconciliation hygiene lives in
// `useKeyedSubscriptions`; this hook just joins struct + state.
//
// `state` is `undefined` until the first per-part frame arrives.
// The view is responsible for rendering the loading slot.
//
// Three specialised flavors live here (one per per-part topic kind):
//   useNovaPartsByTag    → NovaPart   (resources + components)
//   useNovaScienceByTag  → NovaScience (instrument + experiments)
//   useNovaStorageByTag  → NovaStorage (DataStorage + files)

import type { Topic } from '@dragonglass/telemetry/core';
import { useNovaVesselStructure } from './use-nova-vessel-structure.svelte';
import { useKeyedSubscriptions } from './use-keyed-subscriptions.svelte';
import {
  NovaPartTopic,
  NovaScienceTopic,
  NovaStorageTopic,
  decodePart,
  decodeScience,
  decodeStorage,
  type NovaPart,
  type NovaScience,
  type NovaStorage,
  type NovaPartStruct,
  type SystemTag,
} from './nova-topics';

export interface NovaTagged<T> {
  struct: NovaPartStruct;
  state: T | undefined;
}

// Generic — factor the structure-fetch + tag-filter + keyed-sub
// plumbing once; the three flavours below differ only in the topic
// factory + decoder they hand to the keyed-subscription core.
function useByTag<F, T>(
  vesselId: string | (() => string | undefined),
  tag: SystemTag,
  topicFor: (partId: string) => Topic<F, unknown>,
  decode: (frame: F) => T,
): { readonly current: NovaTagged<T>[] } {
  const structureRef = useNovaVesselStructure(vesselId);

  const matching = $derived.by<NovaPartStruct[]>(() => {
    const s = structureRef.current;
    return s ? s.parts.filter((p) => p.tags.includes(tag)) : [];
  });

  const subs = useKeyedSubscriptions(
    () => matching.map((p) => p.id),
    topicFor,
    decode,
  );

  const entries = $derived.by<NovaTagged<T>[]>(() =>
    matching.map((p) => ({ struct: p, state: subs.get(p.id) })),
  );

  return {
    get current() {
      return entries;
    },
  };
}

// Back-compat alias — most existing call sites read `state: NovaPart`.
export type NovaTaggedPart = NovaTagged<NovaPart>;

export function useNovaPartsByTag(
  vesselId: string | (() => string | undefined),
  tag: SystemTag,
): { readonly current: NovaTagged<NovaPart>[] } {
  return useByTag(vesselId, tag, (id) => NovaPartTopic(id), decodePart);
}

export function useNovaScienceByTag(
  vesselId: string | (() => string | undefined),
  tag: SystemTag,
): { readonly current: NovaTagged<NovaScience>[] } {
  return useByTag(vesselId, tag, (id) => NovaScienceTopic(id), decodeScience);
}

export function useNovaStorageByTag(
  vesselId: string | (() => string | undefined),
  tag: SystemTag,
): { readonly current: NovaTagged<NovaStorage>[] } {
  return useByTag(vesselId, tag, (id) => NovaStorageTopic(id), decodeStorage);
}
