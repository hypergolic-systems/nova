// Dev-only entry for `vite dev`. Mirrors what Dragonglass's hud shell
// does in production:
//
//   1. Pull in the runtime stylesheets the sidecar auto-links into
//      its synthesized shell (tokens.css + flight.css). flight.css
//      isn't in DG's runtime.css anymore (left when stock-UI was
//      removed in dragonglass@f1339cc1), but the dev server still
//      needs it for layout. Production lands it via flight.ts's
//      `adoptedStyleSheets` import.
//   2. Install a `NovaSimulatedKsp` fixture so the offline panels
//      have plausible data — UNLESS `?ws=ws://host:port` is in the
//      URL, in which case `getKsp` auto-bootstraps a real
//      `DragonglassTelemetry` against that endpoint (typically the
//      headless `Nova.Sim` binary in mod/Nova.Sim/).
//   3. Dynamic-import the per-scene entry that matches `?scene=...`
//      and mount its default export. Defaults to `flight`.

import '@dragonglass/instruments/theme/tokens.css';
import '@dragonglass/instruments/flight.css';
import { mount } from 'svelte';
import { setKsp } from '@dragonglass/telemetry/svelte';
import { NovaSimulatedKsp } from './sim/nova-sim';

const params = new URLSearchParams(location.search);
if (!params.has('ws')) {
  setKsp(new NovaSimulatedKsp());
}

const scene = params.get('scene') ?? 'flight';
const loader: Record<string, () => Promise<{ default: any }>> = {
  flight: () => import('./flight'),
  editor: () => import('./editor'),
  rnd: () => import('./rnd'),
};
(loader[scene] ?? loader.flight)().then(({ default: Component }) => {
  mount(Component, { target: document.getElementById('app') ?? document.body });
});
