// Nova EDITOR scene entry. See `flight.ts` for the contract — DG's
// hud shell dynamic-imports this on EDITOR entry, mounts the default
// export, unmounts on exit. EditorHud relies on Svelte-scoped component
// styles for layout (no shared layout sheet like flight.css needed).

import './init';
import EditorHud from './EditorHud.svelte';

export default EditorHud;
