<script lang="ts">
  // Nova HUD scene router. Combines KSP's real `LoadedScene` (from
  // Dragonglass's GameTopic) with Nova's virtual-scene topic — when a
  // virtual scene is set (e.g. "RND" while the player is in the R&D
  // view), it overrides the real scene for routing purposes; KSP
  // itself stays in whatever scene it was already in. Closing the
  // virtual scene clears the override and the router falls back to
  // the real scene seamlessly.

  import { useGame, getKsp } from '@dragonglass/telemetry/svelte';
  import { DragonglassRoot } from '@dragonglass/instruments';
  import FlightHud from './FlightHud.svelte';
  import EditorHud from './EditorHud.svelte';
  import RndScene from './components/rnd/RndScene.svelte';
  import { NovaSceneTopic, decodeNovaScene } from './telemetry/nova-topics';

  const game = useGame();

  const ksp = getKsp();
  let virtualScene = $state('');
  $effect(() => {
    return ksp.subscribe(NovaSceneTopic, (frame) => {
      virtualScene = decodeNovaScene(frame).virtualScene;
    });
  });

  // Effective scene — virtual takes precedence when non-empty.
  const scene = $derived(virtualScene || game.scene || '');
  const label = $derived(scene ? scene.toLowerCase() : 'connecting…');
</script>

<DragonglassRoot>
  {#if scene === 'FLIGHT'}
    <FlightHud />
  {:else if scene === 'EDITOR'}
    <EditorHud />
  {:else if scene === 'RND'}
    <RndScene />
  {:else}
    <div class="placeholder">
      <div class="placeholder__brand">NOVA</div>
      <div class="placeholder__scene">{label}</div>
    </div>
  {/if}
</DragonglassRoot>

<style>
  .placeholder {
    position: fixed;
    inset: 0;
    display: flex;
    flex-direction: column;
    align-items: center;
    justify-content: center;
    gap: 0.5rem;
    color: #d97aff;
    font-family: 'Share Tech Mono', ui-monospace, monospace;
    text-transform: uppercase;
    letter-spacing: 0.3em;
    pointer-events: none;
  }
  .placeholder__brand {
    font-size: 1.25rem;
    opacity: 0.6;
  }
  .placeholder__scene {
    font-size: 2rem;
  }
</style>
