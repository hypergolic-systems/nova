<script lang="ts">
  // Top-level layout for the Science tab in the R&D scene. Two-pane
  // shell: a vertical solar-system tree on the left, the per-body
  // detail pane on the right. A thin completion strip across the top
  // shows the program's total archived/possible roll-up — a glance at
  // "how much of the known science space have I explored?".

  import { useNovaScienceArchive } from '../../telemetry/use-nova-science-archive.svelte';
  import BodyList from './BodyList.svelte';
  import BodyDetail from './BodyDetail.svelte';

  const archiveRef = useNovaScienceArchive();
  const archive = $derived(archiveRef.current);

  let selected = $state<string>('Kerbin');

  // If the archive lands and Kerbin isn't there (mod-pack edge case),
  // fall back to whatever first body the wire reports.
  $effect(() => {
    const bodies = archive.bodies;
    if (bodies.length === 0) return;
    if (!bodies.some((b) => b.bodyName === selected)) {
      selected = bodies[0].bodyName;
    }
  });

  const totals = $derived.by(() => {
    let archived = 0, possible = 0;
    for (const b of archive.bodies) {
      archived += b.archivedCount;
      possible += b.possibleCount;
    }
    return { archived, possible };
  });
  const totalPct = $derived(
    totals.possible > 0 ? totals.archived / totals.possible : 0,
  );

  const selectedSummary = $derived(
    archive.bodies.find((b) => b.bodyName === selected),
  );
</script>

<div class="sa">
  <header class="sa__strip">
    <span class="sa__strip-label">SCIENCE PROGRAM</span>
    <span class="sa__strip-count">
      <span class="sa__strip-num">{totals.archived}</span>
      <span class="sa__strip-sep">/</span>
      <span class="sa__strip-den">{totals.possible}</span>
    </span>
    <span class="sa__strip-bar" aria-hidden="true">
      <span class="sa__strip-fill" style:width={`${Math.round(totalPct * 100)}%`}></span>
    </span>
    <span class="sa__strip-pct">{Math.round(totalPct * 100)}%</span>
  </header>

  <div class="sa__split">
    <BodyList
      bodies={archive.bodies}
      {selected}
      onSelect={(b) => (selected = b)}
    />
    <BodyDetail summary={selectedSummary} {archive} />
  </div>
</div>

<style>
  .sa {
    display: flex;
    flex-direction: column;
    width: 100%;
    height: 100%;
    min-height: 0;
  }

  .sa__strip {
    flex: 0 0 auto;
    display: grid;
    grid-template-columns: auto auto 1fr auto;
    align-items: center;
    column-gap: 12px;
    padding: 8px 18px;
    border-bottom: 1px solid var(--line);
    background: var(--bg-elev);
  }
  .sa__strip-label {
    font-family: var(--font-display);
    font-size: 10px;
    letter-spacing: 0.24em;
    color: var(--fg-dim);
    text-transform: uppercase;
  }
  .sa__strip-count {
    font-family: var(--font-mono);
    font-size: 11px;
    font-variant-numeric: tabular-nums;
    color: var(--fg);
    letter-spacing: 0.04em;
  }
  .sa__strip-num   { color: var(--accent); }
  .sa__strip-sep,
  .sa__strip-den   { color: var(--fg-mute); }

  .sa__strip-bar {
    position: relative;
    height: 4px;
    background: rgba(0, 0, 0, 0.35);
    border: 1px solid var(--line);
    overflow: hidden;
  }
  .sa__strip-fill {
    position: absolute;
    inset: 0 auto 0 0;
    background: linear-gradient(90deg, var(--accent-dim), var(--accent));
    box-shadow: 0 0 6px var(--accent-glow);
    transition: width 280ms cubic-bezier(0.4, 0, 0.2, 1);
  }
  .sa__strip-pct {
    font-family: var(--font-mono);
    font-size: 11px;
    color: var(--accent);
    font-variant-numeric: tabular-nums;
    letter-spacing: 0.04em;
    min-width: 4ch;
    text-align: right;
  }

  .sa__split {
    flex: 1 1 0;
    min-height: 0;
    display: grid;
    grid-template-columns: 220px minmax(0, 1fr);
  }
</style>
