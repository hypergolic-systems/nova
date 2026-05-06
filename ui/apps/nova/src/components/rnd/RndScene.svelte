<script lang="ts">
  // Full-page Nova scene that replaces the stock R&D building UI.
  // Routed to from `Hud.svelte` when `NovaSceneTopic.virtualScene === "RND"`.
  //
  // Tabbed shell: the Science archive ships first; Tech tree is a
  // disabled placeholder while that work is under way. Tab chrome
  // mirrors VesselPanel's chip pattern so the visual rhythm matches
  // the rest of Nova's UI.
  //
  // Exiting back to the KSC: the title bar's Exit button (or Esc)
  // sends `setScene("")` to the mod side, which clears the topic and
  // the router falls back to whatever real KSP scene is loaded.

  import { onMount } from 'svelte';
  import { getKsp } from '@dragonglass/telemetry/svelte';
  import { NovaSceneTopic } from '../../telemetry/nova-topics';
  import ScienceArchiveView from './ScienceArchiveView.svelte';

  type TabId = 'science' | 'tech';

  interface Tab {
    id:      TabId;
    short:   string;
    label:   string;
    enabled: boolean;
  }

  const tabs: Tab[] = [
    { id: 'science', short: 'SCI',  label: 'Science',   enabled: true  },
    { id: 'tech',    short: 'TECH', label: 'Tech tree', enabled: false },
  ];

  let activeTab = $state<TabId>('science');

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
      <span class="rnd__subtitle">Nova</span>
    </div>
    <button
      type="button"
      class="rnd__exit"
      aria-label="Exit"
      onclick={exit}
    >EXIT</button>
  </header>

  <nav class="rnd__tabs" aria-label="R&amp;D sections">
    {#each tabs as t (t.id)}
      <button
        type="button"
        class="rnd__chip"
        class:rnd__chip--active={activeTab === t.id}
        class:rnd__chip--disabled={!t.enabled}
        disabled={!t.enabled}
        title={t.label}
        onclick={() => t.enabled && (activeTab = t.id)}
      >
        <span class="rnd__chip-short">{t.short}</span>
        <span class="rnd__chip-label">{t.label}</span>
      </button>
    {/each}
  </nav>

  <main class="rnd__body">
    {#if activeTab === 'science'}
      <ScienceArchiveView />
    {:else}
      <div class="rnd__placeholder">
        <p class="rnd__lead">Tech tree coming soon</p>
      </div>
    {/if}
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

  /* Tab chips. Pinned beneath the title bar — when content scrolls,
     the chips stay visible so the player can pivot tabs without
     scrolling back. Visual language mirrors VesselPanel's `.vp__chip`
     so the two surfaces read as the same Nova grammar. */
  .rnd__tabs {
    flex: 0 0 auto;
    display: flex;
    gap: 6px;
    padding: 6px 14px;
    border-bottom: 1px solid var(--line);
    background: var(--bg-elev);
  }
  .rnd__chip {
    flex: 0 0 auto;
    display: inline-flex;
    align-items: baseline;
    gap: 8px;
    padding: 4px 10px;
    background: transparent;
    border: 1px solid var(--line);
    color: var(--fg-dim);
    font-family: var(--font-display);
    font-size: 11px;
    letter-spacing: 0.16em;
    cursor: pointer;
    transition: color 160ms ease, border-color 160ms ease, background 160ms ease;
  }
  .rnd__chip:hover:not(.rnd__chip--disabled) {
    color: var(--accent);
    border-color: var(--accent-dim);
  }
  .rnd__chip--active {
    color: var(--accent);
    border-color: var(--accent);
    background: rgba(126, 245, 184, 0.08);
    text-shadow: 0 0 6px var(--accent-glow);
  }
  .rnd__chip--disabled {
    color: var(--fg-mute);
    cursor: not-allowed;
    opacity: 0.5;
  }
  .rnd__chip-short {
    font-variant-numeric: tabular-nums;
  }
  .rnd__chip-label {
    font-size: 9.5px;
    color: var(--fg-mute);
    letter-spacing: 0.20em;
    text-transform: uppercase;
  }
  .rnd__chip--active .rnd__chip-label {
    color: var(--accent-soft);
  }

  .rnd__body {
    flex: 1 1 auto;
    min-height: 0;
    display: flex;
    flex-direction: column;
    overflow: hidden;
  }

  .rnd__placeholder {
    flex: 1 1 auto;
    display: flex;
    align-items: center;
    justify-content: center;
    padding: 24px;
  }
  .rnd__lead {
    margin: 0;
    color: var(--fg-mute);
    font-family: var(--font-display);
    font-size: 13px;
    letter-spacing: 0.20em;
    text-transform: uppercase;
  }

  @keyframes rnd-in {
    from { opacity: 0; transform: translateY(-4px); }
    to   { opacity: 1; transform: none; }
  }
</style>
