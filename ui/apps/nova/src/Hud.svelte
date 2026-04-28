<script lang="ts">
  import { useGame } from '@dragonglass/telemetry/svelte';
  import FlightHud from './FlightHud.svelte';
  import EditorHud from './EditorHud.svelte';

  const game = useGame();
  const label = $derived(game.scene ? game.scene.toLowerCase() : 'connecting…');
</script>

{#if game.scene === 'FLIGHT'}
  <FlightHud />
{:else if game.scene === 'EDITOR'}
  <EditorHud />
{:else}
  <div class="placeholder">
    <div class="placeholder__brand">NOVA</div>
    <div class="placeholder__scene">{label}</div>
  </div>
{/if}

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
