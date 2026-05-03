<script lang="ts">
  // Full-page Nova scene that replaces the stock R&D building UI.
  // Routed to from `Hud.svelte` when `NovaSceneTopic.virtualScene === "RND"`.
  // The actual tech-tree / archive content is future work — step 1
  // ships the routing plumbing and a stub body that proves it.
  //
  // Exiting back to the KSC: the title bar's Exit button (or Esc)
  // sends `setScene("")` to the mod side, which clears the topic and
  // the router falls back to whatever real KSP scene is loaded.

  import { onMount } from 'svelte';
  import { getKsp } from '@dragonglass/telemetry/svelte';
  import { NovaSceneTopic } from '../../telemetry/nova-topics';

  const ksp = getKsp();

  function exit(): void {
    ksp.send(NovaSceneTopic, 'setScene', '');
  }

  function onKeydown(e: KeyboardEvent): void {
    if (e.key === 'Escape') {
      e.stopPropagation();
      exit();
    }
  }

  onMount(() => {
    window.addEventListener('keydown', onKeydown);
    return () => window.removeEventListener('keydown', onKeydown);
  });
</script>

<div class="rnd" role="document">
  <header class="rnd__head">
    <div class="rnd__head-text">
      <span class="rnd__title">Research &amp; Development</span>
      <span class="rnd__subtitle">Nova replacement · stub</span>
    </div>
    <button
      type="button"
      class="rnd__exit"
      aria-label="Exit"
      onclick={exit}
    >EXIT</button>
  </header>

  <main class="rnd__body">
    <div class="rnd__pane">
      <p class="rnd__lead">Tech tree and science archive will live here.</p>
      <p class="rnd__sub">
        Stock R&amp;D UI suppressed. Routing confirmed: the building click
        flips Nova's virtual-scene topic, the Hud router navigates to
        this view, and Exit / Esc round-trips back through
        <code>setScene('')</code> to the mod side.
      </p>
    </div>
  </main>
</div>

<style>
  .rnd {
    position: fixed;
    inset: 0;
    z-index: 100;
    display: flex;
    flex-direction: column;
    background: var(--bg-panel-strong);
    color: var(--fg);
    font-family: var(--font-mono);
    font-size: 11px;
    letter-spacing: 0.02em;
    backdrop-filter: blur(14px) saturate(125%);
    -webkit-backdrop-filter: blur(14px) saturate(125%);
    animation: rnd-in 220ms cubic-bezier(0.16, 1, 0.3, 1);
  }

  .rnd__head {
    flex: 0 0 auto;
    display: flex;
    align-items: baseline;
    gap: 12px;
    padding: 10px 14px 8px 16px;
    border-bottom: 1px solid var(--line);
    background: var(--bg-elev);
  }
  .rnd__head-text {
    flex: 1 1 auto;
    display: flex;
    align-items: baseline;
    gap: 14px;
    min-width: 0;
  }
  .rnd__title {
    font-family: var(--font-display);
    font-size: 14px;
    letter-spacing: 0.20em;
    color: var(--accent);
    text-shadow: 0 0 6px var(--accent-glow);
    text-transform: uppercase;
  }
  .rnd__subtitle {
    font-family: var(--font-mono);
    font-size: 10px;
    color: var(--fg-mute);
    letter-spacing: 0.14em;
    text-transform: uppercase;
    white-space: nowrap;
  }
  .rnd__exit {
    flex: 0 0 auto;
    appearance: none;
    background: transparent;
    border: 1px solid var(--accent-dim);
    color: var(--accent);
    font-family: var(--font-display);
    font-size: 10px;
    letter-spacing: 0.22em;
    line-height: 1;
    padding: 6px 12px;
    cursor: pointer;
    transition:
      color 160ms ease,
      border-color 160ms ease,
      background 160ms ease;
  }
  .rnd__exit:hover,
  .rnd__exit:focus-visible {
    color: var(--bg-panel-strong);
    background: var(--accent);
    border-color: var(--accent);
    outline: none;
  }

  .rnd__body {
    flex: 1 1 auto;
    display: flex;
    align-items: center;
    justify-content: center;
    padding: 24px;
    min-height: 0;
    overflow: auto;
  }
  .rnd__pane {
    max-width: 520px;
    text-align: center;
    display: flex;
    flex-direction: column;
    gap: 14px;
    color: var(--fg-dim);
    line-height: 1.55;
  }
  .rnd__lead {
    margin: 0;
    color: var(--accent);
    font-family: var(--font-display);
    font-size: 14px;
    letter-spacing: 0.18em;
    text-transform: uppercase;
    text-shadow: 0 0 6px var(--accent-glow);
  }
  .rnd__sub {
    margin: 0;
    font-size: 12px;
    color: var(--fg-mute);
  }
  .rnd__sub code {
    color: var(--fg);
    background: rgba(126, 245, 184, 0.08);
    padding: 1px 4px;
    border-radius: 2px;
  }

  @keyframes rnd-in {
    from { opacity: 0; transform: translateY(-4px); }
    to   { opacity: 1; transform: none; }
  }
</style>
