<script lang="ts">
  // Solar-system tree rail. Bodies indented under their orbital parent
  // (Sun → Kerbin → Mun, etc.); each row carries a thin horizontal
  // completion meter so the player sees at a glance which corners of
  // the system have any archived science. Click a row to select.

  import type { BodySummary } from '../../telemetry/nova-topics';

  interface Props {
    bodies:   BodySummary[];
    selected: string;
    onSelect: (bodyName: string) => void;
  }
  const { bodies, selected, onSelect }: Props = $props();

  // Build (parent → children[]) map and depth-first walk it. Children
  // emit in the body roster's input order, which mirrors
  // `FlightGlobals.Bodies` and so reads as the canonical solar-system
  // sequence.
  type Row = { body: BodySummary; depth: number };
  const rows = $derived.by<Row[]>(() => {
    const childrenOf = new Map<string, BodySummary[]>();
    const byName = new Map<string, BodySummary>();
    for (const b of bodies) {
      byName.set(b.bodyName, b);
      const list = childrenOf.get(b.parentName) ?? [];
      list.push(b);
      childrenOf.set(b.parentName, list);
    }
    const out: Row[] = [];
    function walk(parentKey: string, depth: number): void {
      const kids = childrenOf.get(parentKey) ?? [];
      for (const child of kids) {
        out.push({ body: child, depth });
        walk(child.bodyName, depth + 1);
      }
    }
    walk('', 0);
    // Surface any orphans (parent not in the roster) at root depth so
    // they don't disappear from the tree.
    const placed = new Set(out.map((r) => r.body.bodyName));
    for (const b of bodies) {
      if (!placed.has(b.bodyName)) out.push({ body: b, depth: 0 });
    }
    return out;
  });
</script>

<nav class="bl" aria-label="Bodies">
  {#each rows as row (row.body.bodyName)}
    {@const pct = row.body.possibleCount > 0
      ? row.body.archivedCount / row.body.possibleCount
      : 0}
    {@const isSel = row.body.bodyName === selected}
    <button
      type="button"
      class="bl__row"
      class:bl__row--active={isSel}
      style:--depth={row.depth}
      onclick={() => onSelect(row.body.bodyName)}
    >
      <span class="bl__name">{row.body.bodyName}</span>
      <span class="bl__count">
        <span class="bl__count-num">{row.body.archivedCount}</span><span class="bl__count-sep">/</span><span class="bl__count-den">{row.body.possibleCount}</span>
      </span>
      <span class="bl__bar" aria-hidden="true">
        <span class="bl__fill" style:width={`${Math.round(pct * 100)}%`}></span>
      </span>
    </button>
  {/each}
</nav>

<style>
  .bl {
    display: flex;
    flex-direction: column;
    overflow-y: auto;
    padding: 8px 4px 8px 6px;
    background: var(--bg-elev);
    border-right: 1px solid var(--line);
  }
  .bl::-webkit-scrollbar {
    width: 6px;
  }
  .bl::-webkit-scrollbar-track {
    background: rgba(0, 0, 0, 0.20);
  }
  .bl::-webkit-scrollbar-thumb {
    background: rgba(126, 245, 184, 0.12);
  }

  .bl__row {
    appearance: none;
    background: transparent;
    border: none;
    border-left: 2px solid transparent;
    color: inherit;
    font: inherit;
    text-align: left;
    cursor: pointer;
    display: grid;
    grid-template-columns: 1fr auto;
    grid-template-rows: auto auto;
    column-gap: 6px;
    row-gap: 2px;
    padding: 5px 6px 5px calc(8px + var(--depth, 0) * 12px);
    transition: background 140ms ease, border-left-color 140ms ease;
    position: relative;
  }
  /* Subtle indent guide line — only on indented rows. Lives in the
     padding so it doesn't fight content. */
  .bl__row::before {
    content: '';
    position: absolute;
    left: calc(2px + var(--depth, 0) * 12px);
    top: 4px;
    bottom: 4px;
    width: 1px;
    background: var(--line);
    opacity: 0.6;
  }
  .bl__row[style*="--depth: 0"]::before { display: none; }

  .bl__row:hover,
  .bl__row:focus-visible {
    background: rgba(126, 245, 184, 0.04);
    border-left-color: var(--accent-dim);
    outline: none;
  }
  .bl__row--active {
    background: rgba(126, 245, 184, 0.08);
    border-left-color: var(--accent);
    box-shadow: inset 0 0 0 1px rgba(126, 245, 184, 0.06);
  }

  .bl__name {
    grid-column: 1;
    grid-row: 1;
    font-family: var(--font-display);
    font-size: 11px;
    letter-spacing: 0.14em;
    color: var(--fg-dim);
    text-transform: uppercase;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }
  .bl__row--active .bl__name {
    color: var(--accent);
    text-shadow: 0 0 4px var(--accent-glow);
  }
  .bl__count {
    grid-column: 2;
    grid-row: 1;
    font-family: var(--font-mono);
    font-size: 10px;
    color: var(--fg-mute);
    font-variant-numeric: tabular-nums;
    letter-spacing: 0.02em;
  }
  .bl__count-num {
    color: var(--accent-dim);
  }
  .bl__row--active .bl__count-num {
    color: var(--accent);
  }

  .bl__bar {
    grid-column: 1 / -1;
    grid-row: 2;
    position: relative;
    height: 3px;
    background: rgba(0, 0, 0, 0.4);
    overflow: hidden;
  }
  .bl__fill {
    position: absolute;
    inset: 0 auto 0 0;
    background: var(--accent-dim);
    transition: width 240ms cubic-bezier(0.4, 0, 0.2, 1);
  }
  .bl__row--active .bl__fill {
    background: var(--accent);
    box-shadow: 0 0 4px var(--accent-glow);
  }
</style>
