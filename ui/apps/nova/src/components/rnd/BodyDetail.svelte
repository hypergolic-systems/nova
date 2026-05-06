<script lang="ts">
  // Right pane of the Science archive. One section per experiment that
  // has at least one possible subject for the selected body. Section
  // header (Unica One) + the matching grid component (atm stack / lts
  // 6×12). Body header up top: name, parent body, and the body's own
  // archived/possible roll-up.

  import type { ArchiveSubject, BodySummary, NovaScienceArchive } from '../../telemetry/nova-topics';
  import { experimentLabel } from '../../util/science-labels';
  import AtmLayerStack from './AtmLayerStack.svelte';
  import LtsSliceGrid from './LtsSliceGrid.svelte';

  interface Props {
    summary: BodySummary | undefined;
    archive: NovaScienceArchive;
  }
  const { summary, archive }: Props = $props();

  const sections = $derived.by<{ experimentId: string; subjects: ArchiveSubject[] }[]>(() => {
    if (!summary) return [];
    const perBody = archive.subjects.get(summary.bodyName);
    if (!perBody) return [];
    return Array.from(perBody.entries())
      .filter(([, subjects]) => subjects.length > 0)
      .map(([experimentId, subjects]) => ({ experimentId, subjects }));
  });

  const completionPct = $derived(
    summary && summary.possibleCount > 0
      ? Math.round((summary.archivedCount / summary.possibleCount) * 100)
      : 0,
  );
</script>

<section class="bd">
  {#if summary}
    <header class="bd__head">
      <h2 class="bd__title">{summary.bodyName}</h2>
      {#if summary.parentName}
        <span class="bd__parent">orbiting {summary.parentName}</span>
      {/if}
      <span class="bd__roll">
        <span class="bd__roll-num">{summary.archivedCount}</span>
        <span class="bd__roll-sep">/</span>
        <span class="bd__roll-den">{summary.possibleCount}</span>
        <span class="bd__roll-pct">·  {completionPct}%</span>
      </span>
    </header>

    {#if sections.length === 0}
      <p class="bd__empty">No experiments configured for this body.</p>
    {:else}
      {#each sections as section (section.experimentId)}
        <div class="bd__section">
          <h3 class="bd__section-title">{experimentLabel(section.experimentId)}</h3>
          {#if section.experimentId === 'atm-profile'}
            <AtmLayerStack subjects={section.subjects} />
          {:else if section.experimentId === 'lts'}
            <LtsSliceGrid subjects={section.subjects} />
          {/if}
        </div>
      {/each}
    {/if}
  {:else}
    <p class="bd__empty">Select a body to view its archive.</p>
  {/if}
</section>

<style>
  .bd {
    display: flex;
    flex-direction: column;
    min-height: 0;
    overflow-y: auto;
    padding: 14px 18px;
    color: var(--fg);
  }

  .bd__head {
    display: flex;
    align-items: baseline;
    flex-wrap: wrap;
    gap: 12px;
    padding-bottom: 8px;
    margin-bottom: 14px;
    border-bottom: 1px solid var(--line);
  }
  .bd__title {
    margin: 0;
    font-family: var(--font-display);
    font-size: 22px;
    letter-spacing: 0.16em;
    color: var(--accent);
    text-shadow: 0 0 8px var(--accent-glow);
    text-transform: uppercase;
  }
  .bd__parent {
    font-family: var(--font-mono);
    font-size: 10px;
    color: var(--fg-mute);
    letter-spacing: 0.10em;
    text-transform: uppercase;
  }
  .bd__roll {
    margin-left: auto;
    display: inline-flex;
    align-items: baseline;
    gap: 4px;
    font-family: var(--font-mono);
    font-size: 12px;
    color: var(--fg-dim);
    font-variant-numeric: tabular-nums;
    letter-spacing: 0.04em;
  }
  .bd__roll-num {
    color: var(--accent);
    font-size: 14px;
  }
  .bd__roll-sep,
  .bd__roll-den {
    color: var(--fg-mute);
  }
  .bd__roll-pct {
    color: var(--accent-dim);
    margin-left: 8px;
  }

  .bd__section {
    margin-bottom: 18px;
  }
  .bd__section-title {
    margin: 0 0 8px;
    font-family: var(--font-display);
    font-size: 11px;
    letter-spacing: 0.22em;
    color: var(--fg-dim);
    text-transform: uppercase;
    border-bottom: 1px dashed var(--line);
    padding-bottom: 4px;
  }

  .bd__empty {
    margin: 24px 4px;
    color: var(--fg-mute);
    font-size: 11px;
    letter-spacing: 0.04em;
  }

  /* Themed scrollbar — matches the vessel-panel scroll treatment. */
  .bd::-webkit-scrollbar {
    width: 8px;
    height: 8px;
  }
  .bd::-webkit-scrollbar-track {
    background: rgba(0, 0, 0, 0.25);
    border-left: 1px solid var(--line);
  }
  .bd::-webkit-scrollbar-thumb {
    background: rgba(126, 245, 184, 0.18);
    border: 1px solid transparent;
    background-clip: padding-box;
  }
  .bd::-webkit-scrollbar-thumb:hover {
    background: rgba(126, 245, 184, 0.42);
    background-clip: padding-box;
  }
</style>
