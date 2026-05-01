// Dev-only entry. Pulls in the Dragonglass runtime stylesheets the
// sidecar auto-links in production (tokens, flight.css) so the
// Vite dev server renders the same as inside KSP. Also installs the
// Nova-aware Ksp wrapper so the SCI / PWR / RES tabs see plausible
// fixture data — the production sidecar provides this from the live
// mod, but in plain-browser dev nothing emits it without help.
// Production lib build entry-points `hud.ts` directly and skips this
// branch entirely.

import '@dragonglass/instruments/theme/tokens.css';
import '@dragonglass/instruments/flight.css';
import { setKsp } from '@dragonglass/telemetry/svelte';
import { NovaSimulatedKsp } from './sim/nova-sim';

setKsp(new NovaSimulatedKsp());

import('./hud');
