// Nova RND virtual-scene entry. Dynamic-imported by DG's hud shell
// when VirtualSceneTopic is set to "RND" — RND isn't a stock KSP
// scene, it's Nova's full-page replacement for the R&D building UI,
// activated by patching RnDBuilding.OnClicked to publish to
// VirtualSceneTopic instead of opening stock R&D.

import './init';
import RndScene from './components/rnd/RndScene.svelte';

export default RndScene;
