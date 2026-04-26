// Dev-only entry. Pulls in the Dragonglass runtime stylesheets the
// sidecar auto-links in production (tokens, flight.css) so the
// Vite dev server renders the same as inside KSP, then delegates to
// the regular hud bootstrap. Production lib build still entry-points
// `hud.ts` directly.

import '@dragonglass/instruments/theme/tokens.css';
import '@dragonglass/instruments/flight.css';
import './hud';
