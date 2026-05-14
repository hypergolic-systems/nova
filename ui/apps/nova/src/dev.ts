// Dev-only entry. Pulls in the Dragonglass runtime stylesheets the
// sidecar auto-links in production (tokens, flight.css) so the
// Vite dev server renders the same as inside KSP. Then installs a
// fixture Ksp so the SCI / PWR / RES tabs see plausible offline data
// — UNLESS `?ws=ws://host:port` is in the URL, in which case
// Dragonglass's own `getKsp` auto-bootstraps a real `DragonglassTelemetry`
// against that endpoint (typically the headless `Nova.Sim` binary in
// mod/Nova.Sim/). The production sidecar likewise sets `?ws=` on its
// boot URL, so this branching mirrors how stock CEF picks its transport.
// Production lib build entry-points `hud.ts` directly and skips this
// branch entirely.

import '@dragonglass/instruments/theme/tokens.css';
import '@dragonglass/instruments/flight.css';
import { setKsp } from '@dragonglass/telemetry/svelte';
import { NovaSimulatedKsp } from './sim/nova-sim';

if (!new URLSearchParams(location.search).has('ws')) {
  setKsp(new NovaSimulatedKsp());
}

import('./hud');
