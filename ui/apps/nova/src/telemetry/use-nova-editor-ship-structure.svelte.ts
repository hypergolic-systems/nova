// Subscribe to the editor's ship-structure topic. Mirrors
// `useNovaVesselStructure` but for the single-instance
// `NovaEditorShipStructure/editor` topic. No id parameter — the
// editor only ever holds one ShipConstruct at a time.

import { getKsp } from '@dragonglass/telemetry/svelte';
import {
  NovaEditorShipStructureTopic,
  decodeStructure,
  type NovaVesselStructure,
} from './nova-topics';

export function useNovaEditorShipStructure(): {
  readonly current: NovaVesselStructure | undefined;
} {
  const telemetry = getKsp();
  let current = $state<NovaVesselStructure | undefined>(undefined);

  $effect(() => {
    return telemetry.subscribe(NovaEditorShipStructureTopic, (frame) => {
      current = decodeStructure(frame);
    });
  });

  return {
    get current() {
      return current;
    },
  };
}
