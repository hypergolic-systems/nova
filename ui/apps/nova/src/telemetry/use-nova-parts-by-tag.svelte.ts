// Auto-subscribes to every part on the vessel whose tag list
// contains `tag`, and returns a reactive list of {struct, state}
// pairs. Reconciles incrementally on structure changes — adds
// subscriptions for newly-tagged parts, drops them for parts that
// no longer match, and leaves surviving parts' subscriptions and
// slots untouched.
//
// `state` starts undefined and fills on the first per-part frame
// (which the broadcaster snapshots immediately on subscribe). The
// view is responsible for rendering the loading slot.

import { onDestroy, untrack } from 'svelte';
import { getKsp } from '@dragonglass/telemetry/svelte';
import { useNovaVesselStructure } from './use-nova-vessel-structure.svelte';
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
  const telemetry = getKsp();
  const structureRef = useNovaVesselStructure(vesselId);

  // Stable per-part slots and unsub handles, keyed by partId. Survives
  // across structure publishes so the wire subscription, the slot
  // identity, and the last-decoded `state` all carry through unchanged
  // for parts that didn't leave the matching set.
  const slots = new Map<string, NovaTaggedPart>();
  const subs = new Map<string, () => void>();

  let entries = $state<NovaTaggedPart[]>([]);

  $effect(() => {
    const structure = structureRef.current;
    const matching = structure
      ? structure.parts.filter((p) => p.tags.includes(tag))
      : [];
    const wanted = new Set(matching.map((p) => p.id));

    // Drop parts that left the matching set.
    for (const [id, unsub] of subs) {
      if (!wanted.has(id)) {
        unsub();
        subs.delete(id);
        slots.delete(id);
      }
    }

    // Add parts that entered, refresh struct on parts that stayed.
    for (const p of matching) {
      const existing = slots.get(p.id);
      if (existing) {
        // Title/tags can shift without the partId changing — keep the
        // displayed struct in sync with the latest structure frame.
        existing.struct = p;
        continue;
      }
      const slot: NovaTaggedPart = { struct: p, state: undefined };
      slots.set(p.id, slot);
      // `untrack` keeps the synchronous cached-frame fire that
      // `subscribe` performs from binding any reactive reads inside
      // the callback to this effect — without it, a `$state` read in
      // the callback would self-trigger the effect to depth-exceed.
      const unsub = untrack(() =>
        telemetry.subscribe(NovaPartTopic(p.id), (frame) => {
          slot.state = decodePart(frame);
        }),
      );
      subs.set(p.id, unsub);
    }

    entries = matching.map((p) => slots.get(p.id)!);
  });

  onDestroy(() => {
    for (const u of subs.values()) u();
    subs.clear();
    slots.clear();
  });

  return {
    get current() {
      return entries;
    },
  };
}
