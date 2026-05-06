<script lang="ts">
  // 6×12 cell grid — rows = LTS situations (canonical order from
  // LongTermStudyExperiment.SupportedSituations), columns = body-year
  // slice 0..11. Each cell is a small square: mint-filled when archived
  // (fidelity > 0), outline-only otherwise. Click pins the SubjectRow
  // detail directly beneath the grid. Tight density on purpose — a
  // whole body's LTS state is meant to be scannable in one glance.

  import type { ArchiveSubject } from '../../telemetry/nova-topics';
  import SubjectRow from './SubjectRow.svelte';

  interface Props {
    subjects: ArchiveSubject[];
  }
  const { subjects }: Props = $props();

  // Wire row order is (situation × slice) per AllSubjectsFor enumeration.
  // Group by situation to render one row per situation in input order.
  const rows = $derived.by(() => {
    const map = new Map<string, ArchiveSubject[]>();
    for (const s of subjects) {
      const list = map.get(s.variant) ?? [];
      list.push(s);
      map.set(s.variant, list);
    }
    return Array.from(map.entries()).map(([situation, slices]) => ({
      situation,
      slices: [...slices].sort((a, b) => a.slice - b.slice),
    }));
  });

  const sliceCount = $derived(rows[0]?.slices.length ?? 12);

  // Pinned cell — same pattern as AtmLayerStack. Track by (situation,
  // slice) so the right SubjectRow renders below.
  let active = $state<{ situation: string; slice: number } | null>(null);
  function pick(situation: string, slice: number): void {
    if (active && active.situation === situation && active.slice === slice) {
      active = null;
    } else {
      active = { situation, slice };
    }
  }

  const activeSubject = $derived.by<ArchiveSubject | null>(() => {
    if (!active) return null;
    const row = rows.find((r) => r.situation === active!.situation);
    return row?.slices.find((s) => s.slice === active!.slice) ?? null;
  });

  // Compact display labels. Long stock names ("SrfSplashed") wouldn't
  // fit a left rail; pre-shorten here.
  const SITUATION_SHORT: Record<string, string> = {
    SrfLanded:   'SRF·LAND',
    SrfSplashed: 'SRF·SPLA',
    FlyingLow:   'FLY·LO',
    FlyingHigh:  'FLY·HI',
    InSpaceLow:  'SPC·LO',
    InSpaceHigh: 'SPC·HI',
  };
  function shortSituation(name: string): string {
    return SITUATION_SHORT[name] ?? name;
  }
</script>

<div class="lts">
  <div class="lts__cols">
    <span class="lts__row-label" aria-hidden="true"></span>
    {#each Array(sliceCount) as _, i (i)}
      <span class="lts__col-label" class:lts__col-label--milestone={i % 3 === 0}>{i + 1}</span>
    {/each}
  </div>
  {#each rows as row (row.situation)}
    <div class="lts__row">
      <span class="lts__row-label">{shortSituation(row.situation)}</span>
      {#each row.slices as s (s.slice)}
        {@const archived = s.fidelity > 0}
        {@const isActive = active?.situation === row.situation && active?.slice === s.slice}
        <button
          type="button"
          class="lts__cell"
          class:lts__cell--archived={archived}
          class:lts__cell--active={isActive}
          aria-label={`${row.situation} slice ${s.slice + 1}${archived ? ` ${Math.round(s.fidelity * 100)}%` : ' unstudied'}`}
          onclick={() => pick(row.situation, s.slice)}
        ></button>
      {/each}
    </div>
  {/each}

  {#if activeSubject}
    <div class="lts__detail">
      <SubjectRow
        subject={activeSubject}
        variantLabel={`${shortSituation(activeSubject.variant)}`}
      />
    </div>
  {/if}
</div>

<style>
  .lts {
    display: flex;
    flex-direction: column;
    gap: 1px;
    padding-left: 2px;
  }

  /* Two zones in each row: a left label column then 12 cells. The
     column-label header mirrors the same template so labels align
     above the cells. */
  .lts__cols,
  .lts__row {
    display: grid;
    grid-template-columns: 5.4em repeat(12, 1fr);
    column-gap: 2px;
    align-items: center;
  }
  .lts__cols {
    margin-bottom: 2px;
    height: 12px;
  }
  .lts__col-label {
    font-family: var(--font-display);
    font-size: 8px;
    color: var(--fg-mute);
    text-align: center;
    letter-spacing: 0.04em;
  }
  .lts__col-label--milestone {
    color: var(--accent-dim);
  }
  .lts__row-label {
    font-family: var(--font-display);
    font-size: 8.5px;
    color: var(--fg-dim);
    letter-spacing: 0.14em;
    text-transform: uppercase;
  }
  .lts__cell {
    appearance: none;
    aspect-ratio: 1 / 1;
    min-width: 0;
    height: 12px;
    width: 12px;
    background: transparent;
    border: 1px solid var(--line);
    cursor: pointer;
    transition: background 140ms ease, border-color 140ms ease,
                box-shadow 140ms ease, transform 100ms ease;
    padding: 0;
    justify-self: center;
  }
  .lts__cell:hover,
  .lts__cell:focus-visible {
    border-color: var(--accent-dim);
    background: rgba(126, 245, 184, 0.05);
    outline: none;
  }
  .lts__cell--archived {
    background: linear-gradient(135deg, var(--accent-dim) 0%, var(--accent) 100%);
    border-color: var(--accent);
    box-shadow: inset 0 0 0 1px rgba(0, 0, 0, 0.25),
                0 0 4px var(--accent-glow);
  }
  .lts__cell--active {
    transform: scale(1.15);
    box-shadow: 0 0 8px var(--accent-glow),
                inset 0 0 0 1px rgba(255, 255, 255, 0.2);
    border-color: var(--accent-soft);
  }

  .lts__detail {
    margin: 6px 0 2px 4px;
    padding: 4px 4px 4px 8px;
    border-left: 1px solid var(--accent-dim);
    background: rgba(126, 245, 184, 0.03);
  }
</style>
