import { mount } from 'svelte';
import { getKsp } from '@dragonglass/telemetry/svelte';
import { GameTopic, CAP_FLIGHT_UI } from '@dragonglass/telemetry/core';
import Hud from './Hud.svelte';

mount(Hud, { target: document.getElementById('app') ?? document.body });

// Tell the plugin that Nova owns flight-scene UI so Dragonglass's
// StockUiHider suppresses KSP's built-in navball, staging, etc.
// Without this handshake stock KSP's chrome stays drawn alongside
// Nova's HUD. PAW + editor caps are deliberately omitted — Nova
// doesn't render those yet, so leaving them out keeps stock KSP's
// right-click menus and editor screens functional.
const ksp = getKsp();
ksp.connect().then(() => {
  ksp.send(GameTopic, 'setCapabilities', [CAP_FLIGHT_UI]);
});
