// Subscribe to every part on the vessel and return a reactive list
// of `{struct, state}` pairs. Subscriptions are diff-reconciled
// across structure publishes — surviving parts keep their wire
// subscription and last-decoded state; departing parts unsubscribe;
// new parts subscribe. Reconciliation hygiene lives in
// `useKeyedSubscriptions`; this hook just joins struct + state.
//
// Views that want a subset (e.g. PowerView wants parts with a
// `B`/`C`/`F`/`S` component) filter the returned list on
// `struct.componentKinds`. Tags are not a wire-level concept —
// each view encodes its own "which kinds matter to me" rule.
//
// `state` is `undefined` until the first per-part frame arrives.
// The view renders a loading slot or hides the row.

import { useNovaVesselStructure } from './use-nova-vessel-structure.svelte';
import { useKeyedSubscriptions } from './use-keyed-subscriptions.svelte';
import {
  NovaPartTopic,
  decodePart,
  type NovaPart,
  type NovaPartStruct,
  type NovaScience,
  type NovaStorage,
} from './nova-topics';

export interface NovaPartEntry {
  struct: NovaPartStruct;
  state: NovaPart | undefined;
}

export function useNovaParts(
  vesselId: string | (() => string | undefined),
): { readonly current: NovaPartEntry[] } {
  const structureRef = useNovaVesselStructure(vesselId);

  const subs = useKeyedSubscriptions(
    () => structureRef.current?.parts.map((p) => p.id) ?? [],
    (id) => NovaPartTopic(id),
    decodePart,
  );

  const entries = $derived.by<NovaPartEntry[]>(() => {
    const parts = structureRef.current?.parts ?? [];
    return parts.map((p) => ({ struct: p, state: subs.get(p.id) }));
  });

  return {
    get current() {
      return entries;
    },
  };
}

// ── Legacy science / storage stubs ─────────────────────────────────
//
// `nova/science/*` and `nova/storage/*` aren't ported to nova-telemetry
// yet; until those land their hooks return empty lists so ScienceView
// typechecks and renders nothing rather than blocking the build.

export interface NovaTagged<T> {
  struct: NovaPartStruct;
  state: T | undefined;
}

const EMPTY_SCIENCE = { get current() { return [] as NovaTagged<NovaScience>[]; } };
const EMPTY_STORAGE = { get current() { return [] as NovaTagged<NovaStorage>[]; } };

export function useNovaScienceByTag(
  _vesselId: string | (() => string | undefined),
  _tag: string,
): { readonly current: NovaTagged<NovaScience>[] } {
  return EMPTY_SCIENCE;
}

export function useNovaStorageByTag(
  _vesselId: string | (() => string | undefined),
  _tag: string,
): { readonly current: NovaTagged<NovaStorage>[] } {
  return EMPTY_STORAGE;
}
