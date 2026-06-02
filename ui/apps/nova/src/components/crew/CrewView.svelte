<script lang="ts">
  // Crew section — one Subheading per crewed part, listing the
  // kerbals aboard with their trait. Joins two topics: the per-vessel
  // crew roster (kerbal identity + assigned partId) and the per-vessel
  // structure (partId → display title). Per-part subheading reads
  // "{partTitle}" with a small "{N}" summary on the right (the number
  // of kerbals in that part); each row inside is "{traitGlyph} {name}".
  //
  // Trait glyphs are single-letter pills (P / E / S / T) tinted per
  // role — deliberately *not* the accent-green / warn-amber palette so
  // they read as kerbal-trait labels, not as status indicators that
  // mean "good" or "bad". Tourists fall back to dim grey because
  // they're not a functional role.

  import { useNovaCrewRoster } from '../../telemetry/use-nova-crew-roster.svelte';
  import { useNovaVesselStructure } from '../../telemetry/use-nova-vessel-structure.svelte';
  import type { KerbalTrait, NovaKerbal } from '../../telemetry/nova-topics';
  import Subheading from '../common/Subheading.svelte';

  interface Props {
    vesselId: string;
    /** Bound out: true when at least one kerbal is aboard. */
    hasContent?: boolean;
  }
  let { vesselId, hasContent = $bindable(true) }: Props = $props();

  const rosterRef = useNovaCrewRoster(() => vesselId);
  const structRef = useNovaVesselStructure(() => vesselId);

  // Lookup: persistent partId → display title. Empty until structure
  // arrives; rows render with the raw id as a fallback so the player
  // sees *something* during the first-frame join.
  const partTitleById = $derived.by(() => {
    const m = new Map<string, string>();
    const s = structRef.current;
    if (s) for (const p of s.parts) m.set(p.id, p.title);
    return m;
  });

  // Group kerbals by partId, preserving the order they arrive on the
  // wire (the mod emits parts in vessel-part-list order, so groups
  // come out in the same order the player sees the vessel laid out
  // in stage/tree views).
  interface CrewGroup {
    partId: string;
    partTitle: string;
    kerbals: NovaKerbal[];
  }
  const groups = $derived.by((): CrewGroup[] => {
    const roster = rosterRef.current;
    if (!roster || roster.crew.length === 0) return [];
    const byPart = new Map<string, CrewGroup>();
    const order: string[] = [];
    for (const k of roster.crew) {
      let g = byPart.get(k.partId);
      if (!g) {
        g = { partId: k.partId, partTitle: partTitleById.get(k.partId) ?? k.partId, kerbals: [] };
        byPart.set(k.partId, g);
        order.push(k.partId);
      }
      g.kerbals.push(k);
    }
    return order.map((id) => byPart.get(id)!);
  });

  $effect(() => {
    hasContent = groups.length > 0;
  });

  function traitGlyph(t: KerbalTrait): string {
    switch (t) {
      case 'Pilot':     return 'P';
      case 'Engineer':  return 'E';
      case 'Scientist': return 'S';
      case 'Tourist':   return 'T';
      default:          return '?';
    }
  }
  function traitClass(t: KerbalTrait): string {
    return 'crw__trait--' + t.toLowerCase();
  }
</script>

<section class="crw">
  {#each groups as group (group.partId)}
    <Subheading title={group.partTitle}>
      {#snippet summary()}
        <span class="crw__count">{group.kerbals.length}</span>
      {/snippet}
      <ul class="crw__rows">
        {#each group.kerbals as k (k.name)}
          <li class="crw__row">
            <span class="crw__trait {traitClass(k.trait)}"
                  title={k.trait}>
              {traitGlyph(k.trait)}
            </span>
            <span class="crw__name">
              {k.name}{#if k.veteran}<em class="crw__veteran" title="Veteran">★</em>{/if}
            </span>
            <span class="crw__trait-label">{k.trait}</span>
          </li>
        {/each}
      </ul>
    </Subheading>
  {/each}
</section>

<style>
  .crw {
    display: flex;
    flex-direction: column;
  }

  /* Multiple Subheadings stack inside Crew — same breathing rule we
     use in Power so the per-part labels read as distinct groups. */
  .crw :global(.sh ~ .sh) {
    margin-top: 10px;
  }

  /* Per-part summary: just the kerbal count. Bordered pill so it has
     enough presence to register at a glance without competing with
     the part name. */
  .crw__count {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    min-width: 14px;
    padding: 0 4px;
    border: 1px solid var(--line-accent);
    color: var(--fg-dim);
    font-family: var(--font-mono);
    font-size: 9px;
    letter-spacing: 0;
    line-height: 1.4;
    border-radius: 1px;
  }

  .crw__rows {
    list-style: none;
    margin: 0;
    padding: 0;
    display: flex;
    flex-direction: column;
  }

  /* Per-kerbal row: trait pill | name | trait label. */
  .crw__row {
    display: flex;
    align-items: center;
    gap: 8px;
    padding: 4px 6px;
    margin: 0 -6px;
    position: relative;
    border-bottom: 1px solid rgba(26, 35, 53, 0.55);
    transition: background 200ms cubic-bezier(0.4, 0, 0.2, 1);
  }
  .crw__row:last-child {
    border-bottom: 0;
  }
  .crw__row:hover {
    background: rgba(126, 245, 184, 0.04);
  }

  /* Trait glyph — single-letter caps in a tinted square. Each trait
     gets its own hue, deliberately stepped off Nova's accent-green /
     warn-amber semantic palette so a player doesn't read a Pilot as
     "warning" or a Scientist as "OK". Tourists wear no colour. */
  .crw__trait {
    flex: 0 0 18px;
    display: inline-flex;
    align-items: center;
    justify-content: center;
    width: 18px;
    height: 14px;
    font-family: var(--font-display);
    font-size: 10px;
    letter-spacing: 0;
    border: 1px solid currentColor;
    border-radius: 1px;
    color: var(--fg-dim);
    user-select: none;
  }
  .crw__trait--pilot {
    color: #e8b04a; /* amber-gold — pilot's blazon */
  }
  .crw__trait--engineer {
    color: #e07746; /* copper — engineer's rust */
  }
  .crw__trait--scientist {
    color: #5bc0d8; /* aqua — instrument-panel cyan */
  }
  .crw__trait--tourist {
    color: var(--fg-mute);
  }

  .crw__name {
    flex: 1 1 auto;
    min-width: 0;
    color: var(--fg);
    font-family: var(--font-mono);
    font-size: 11px;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }
  .crw__veteran {
    font-style: normal;
    color: #e8b04a;
    margin-left: 4px;
    font-size: 9px;
    text-shadow: 0 0 4px rgba(232, 176, 74, 0.5);
  }

  /* Trait label — quiet dim caps at the row's right edge. The pill
     already carries the colour cue; the label is here for readability
     on first-meeting (before the glyph→trait mapping is internalised).
     Drops to mute on hover to clean up the row. */
  .crw__trait-label {
    flex: 0 0 auto;
    font-family: var(--font-display);
    font-size: 9px;
    letter-spacing: 0.18em;
    text-transform: uppercase;
    color: var(--fg-mute);
  }
</style>
