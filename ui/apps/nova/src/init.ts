// Shared once-only init for every Nova scene entry. Module-cache
// semantics mean this file's top-level runs exactly once across all
// entries that import it — `flight.ts`, `editor.ts`, `rnd.ts` all
// pull it in, but the capabilities call lands once.
//
// Lives separately from any one entry so DG's per-scene dynamic-
// import can land any of them first — the entry that wins the race
// still triggers init via the import side-effect chain.

import { getKsp } from '@dragonglass/telemetry/svelte';
import {
  GameTopic,
  CAP_FLIGHT_UI,
  CAP_EDITOR_PAW,
  CAP_EDITOR_STAGING,
  CAP_EDITOR_TOOLBAR,
} from '@dragonglass/telemetry/core';

// Capabilities Nova owns:
//   flight/ui      — Nova draws navball, staging, tapes; DG suppresses
//                    stock chrome so they don't double-render.
//   editor/paw     — Nova replaces stock right-click PAW with a context
//                    menu. Stock PAW stays active in flight (no
//                    flight/paw declared).
//   editor/staging — Nova mounts StagingStack in the VAB; DG hides
//                    StageManager.mainListAnchor and short-circuits
//                    ShowHideStageStack so the stock stager doesn't
//                    paint on top of ours.
//   editor/toolbar — Nova draws its own Δv / mass / staging analysis;
//                    asks DG to hide ApplicationLauncher (Engineer's
//                    Report, Stock Δv, Alarm Clock, etc.) while in
//                    the editor so the affordances don't double up.
const ksp = getKsp();
ksp.connect().then(() => {
  ksp.send(GameTopic, 'setCapabilities', [
    CAP_FLIGHT_UI,
    CAP_EDITOR_PAW,
    CAP_EDITOR_STAGING,
    CAP_EDITOR_TOOLBAR,
  ]);
});
