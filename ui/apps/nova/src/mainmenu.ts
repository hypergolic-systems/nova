// Nova MAINMENU scene entry. Mirrors flight/editor/rnd: shared `./init`
// declares capabilities, the default export is the Svelte component DG's
// hud shell mounts on scene entry. The status panel doubles as a live
// diagnostic — it's anchored bottom-left, shows real ws.readyState, and
// catches exactly the silent-failure mode where the bundle loads but
// no scene routing arrived.

import './init';
import MainMenuStatus from './MainMenuStatus.svelte';

export default MainMenuStatus;
