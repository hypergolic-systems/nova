// Subscribe to every part on a vessel (or in the editor's ShipConstruct)
// and return a reactive list of {struct, state} pairs. Each view
// switches on the components present in each part's frame to decide
// what to render — there's no view-side filter; the per-view `switch`
// statement is the filter.
//
// Subscriptions are diff-reconciled across structure publishes via
// `useKeyedSubscriptions` — surviving parts keep their wire
// subscription and last-decoded state; departing parts unsubscribe;
// new parts subscribe.
//
// Three specialised flavors live here (one per per-part topic kind):
//   useNovaParts        → NovaPart    (resources + components)
//   useNovaScienceParts → NovaScience (instrument + experiments)
//   useNovaStorageParts → NovaStorage (DataStorage + files)
// Each emits an empty frame for parts where the topic has nothing
// to say (e.g. NovaScience for a non-instrument part), so iterating
// the full vessel is cheap — the view skips empty frames at render.

import type { Topic } from '@dragonglass/telemetry/core';
import { useNovaVesselStructure } from './use-nova-vessel-structure.svelte';
import { useNovaEditorShipStructure } from './use-nova-editor-ship-structure.svelte';
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
  type NovaVesselStructure,
} from './nova-topics';

export interface NovaPartEntry<T> {
  struct: NovaPartStruct;
  state: T | undefined;
}

interface StructureRef {
  readonly current: NovaVesselStructure | undefined;
}

// Inner core — given any structure-ref (flight or editor), fan out
// to per-part keyed subscriptions for every part in the structure.
function useEveryPartWithStructure<F, T>(
  structureRef: StructureRef,
  topicFor: (partId: string) => Topic<F, unknown>,
  decode: (frame: F) => T,
): { readonly current: NovaPartEntry<T>[] } {
  const allParts = $derived.by<NovaPartStruct[]>(() => {
    const s = structureRef.current;
    return s ? s.parts : [];
  });

  const subs = useKeyedSubscriptions(
    () => allParts.map((p) => p.id),
    topicFor,
    decode,
  );

  const entries = $derived.by<NovaPartEntry<T>[]>(() =>
    allParts.map((p) => ({ struct: p, state: subs.get(p.id) })),
  );

  return {
    get current() {
      return entries;
    },
  };
}

// Back-compat alias — most existing call sites read `state: NovaPart`.
export type NovaPartHandle = NovaPartEntry<NovaPart>;

export function useNovaParts(
  vesselId: string | (() => string | undefined),
): { readonly current: NovaPartEntry<NovaPart>[] } {
  return useEveryPartWithStructure(
    useNovaVesselStructure(vesselId),
    (id) => NovaPartTopic(id),
    decodePart,
  );
}

export function useNovaScienceParts(
  vesselId: string | (() => string | undefined),
): { readonly current: NovaPartEntry<NovaScience>[] } {
  return useEveryPartWithStructure(
    useNovaVesselStructure(vesselId),
    (id) => NovaScienceTopic(id),
    decodeScience,
  );
}

export function useNovaStorageParts(
  vesselId: string | (() => string | undefined),
): { readonly current: NovaPartEntry<NovaStorage>[] } {
  return useEveryPartWithStructure(
    useNovaVesselStructure(vesselId),
    (id) => NovaStorageTopic(id),
    decodeStorage,
  );
}

// Editor-scene parallel of useNovaParts. The editor topic is single-
// instance (no vesselId routing). Per-part NovaPart/<partId>
// subscriptions still resolve in the editor — NovaSubscriptionManager
// falls back to the live ShipConstruct, and NovaPartTopic
// reads from NovaPartModule.Components when there's no VirtualVessel.
export function useNovaEditorParts(): {
  readonly current: NovaPartEntry<NovaPart>[];
} {
  return useEveryPartWithStructure(
    useNovaEditorShipStructure(),
    (id) => NovaPartTopic(id),
    decodePart,
  );
}
