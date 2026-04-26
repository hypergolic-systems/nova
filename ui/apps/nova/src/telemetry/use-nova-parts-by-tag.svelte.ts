// Auto-subscribes to every part on the vessel whose tag list
// contains `tag`, and returns a reactive list of {struct, state}
// pairs. Re-subscribes whenever the structure topic emits — adds
// for newly-tagged parts, drops for parts that no longer match.
//
// `state` starts undefined and fills on the first per-part frame
// (which the broadcaster snapshots immediately on subscribe). The
// view is responsible for rendering the loading slot.

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

  // Plain $state array; entry mutations go through the proxy and
  // fire fine-grained reactivity. Each effect run rebuilds the
  // array from the current structure snapshot — structure topic
  // is low-frequency (1 Hz on changes), so the cost is negligible
  // compared to the simplification of avoiding per-part diffing.
  let entries = $state<NovaTaggedPart[]>([]);

  $effect(() => {
    const structure = structureRef.current;
    if (!structure) {
      entries = [];
      return;
    }

    const matching = structure.parts.filter((p) => p.tags.includes(tag));
    entries = matching.map((struct) => ({ struct, state: undefined }));

    // Snapshot the local indices into stable closures — the
    // subscribe callback fires across many ticks and must always
    // mutate the slot it was created for.
    const subs = matching.map((p, i) =>
      telemetry.subscribe(NovaPartTopic(p.id), (frame) => {
        const slot = entries[i];
        if (slot) slot.state = decodePart(frame);
      }),
    );

    return () => {
      for (const u of subs) u();
    };
  });

  return {
    get current() {
      return entries;
    },
  };
}
