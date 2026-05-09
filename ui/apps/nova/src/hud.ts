import { mount } from 'svelte';
import { getKsp } from '@dragonglass/telemetry/svelte';
import {
  GameTopic,
  CAP_FLIGHT_UI,
  CAP_EDITOR_PAW,
  CAP_EDITOR_STAGING,
  CAP_EDITOR_TOOLBAR,
} from '@dragonglass/telemetry/core';
import Hud from './Hud.svelte';

mount(Hud, { target: document.getElementById('app') ?? document.body });

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
