import { mount } from 'svelte';
import { getKsp } from '@dragonglass/telemetry/svelte';
import { GameTopic, CAP_FLIGHT_UI, CAP_EDITOR_PAW } from '@dragonglass/telemetry/core';
import Hud from './Hud.svelte';

mount(Hud, { target: document.getElementById('app') ?? document.body });

// Capabilities Nova owns:
//   flight/ui   — Nova draws navball, staging, tapes; DG suppresses
//                 stock chrome so they don't double-render.
//   editor/paw  — Nova replaces stock right-click PAW with a context
//                 menu (currently: Set Tank Config). Stock PAW stays
//                 active in flight (no flight/paw declared).
const ksp = getKsp();
ksp.connect().then(() => {
  ksp.send(GameTopic, 'setCapabilities', [CAP_FLIGHT_UI, CAP_EDITOR_PAW]);
});
