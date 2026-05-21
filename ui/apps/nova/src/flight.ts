// Nova FLIGHT scene entry. Dynamic-imported by Dragonglass's hud
// shell when SceneMapTopic has `"FLIGHT": "@nova/flight"` and the
// active scene matches — see NovaUiOverrideAddon.cs for the C# side
// of the registration. The default export must be a Svelte component;
// DG's shell mounts and unmounts it on scene transitions, so this
// module must NOT call `mount()` itself (that mounted FlightHud twice
// and broke every keydown handler in the bargain — same trap Kad
// chased through three rounds).
//
// CSS: the flight-HUD layout containers (.hud, .navslot, .staging-
// stack, .navball-cluster) live in @dragonglass/instruments/flight.css
// and are NOT in Dragonglass's runtime.css (that was apps/stock,
// removed in dragonglass@f1339cc1 when DG became pure infrastructure).
// Adopt the sheet here — the documented pattern is the
// `with { type: 'css' }` import attribute (docs/mod-ui.md §CSS), but
// Vite doesn't transform that attribute end-to-end yet. `?inline`
// returns the file contents as a string from Vite's CSS pipeline; we
// then construct the CSSStyleSheet manually and push it onto
// `document.adoptedStyleSheets`. Functionally identical, ~3KB inlined.

import flightCss from '@dragonglass/instruments/flight.css?inline';
import './init';
import FlightHud from './FlightHud.svelte';

const flightSheet = new CSSStyleSheet();
flightSheet.replaceSync(flightCss);
document.adoptedStyleSheets = [...document.adoptedStyleSheets, flightSheet];

export default FlightHud;
