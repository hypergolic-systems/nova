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

import { useNovaVesselStructure } from './use-nova-vessel-structure.svelte';
import { useKeyedSubscriptions } from './use-keyed-subscriptions.svelte';
import {
  NovaPartTopic,
  decodePart,
  type NovaPart,
  type NovaPartStruct,
  type SystemTag,
} from './nova-topics';

export interface NovaTaggedPart {
  struct: NovaPartStruct;
  state: NovaPart | undefined;
}

export function useNovaPartsByTag(
  vesselId: string | (() => string | undefined),
  tag: SystemTag,
): { readonly current: NovaTaggedPart[] } {
  const structureRef = useNovaVesselStructure(vesselId);

  const matching = $derived.by<NovaPartStruct[]>(() => {
    const s = structureRef.current;
    return s ? s.parts.filter((p) => p.tags.includes(tag)) : [];
  });

  const subs = useKeyedSubscriptions(
    () => matching.map((p) => p.id),
    (partId) => NovaPartTopic(partId),
    decodePart,
  );

  const entries = $derived.by<NovaTaggedPart[]>(() =>
    matching.map((p) => ({ struct: p, state: subs.get(p.id) })),
  );

  return {
    get current() {
      return entries;
    },
  };
}
