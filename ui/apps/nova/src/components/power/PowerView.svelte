<script lang="ts">
  // Power tree: three sections (Generation / Consumption / Storage),
  // one row per part with the relevant tag, totals at the section
  // header. Each row binds to the live NovaPart state via the
  // useNovaPartsByTag hook — the hook subscribes per-part on
  // structure changes and tears down when the view unmounts.
  //
  // When the vessel carries multiple solar panels, they collapse into
  // a SOLAR sub-group inside Generation so the section's eye-line
  // doesn't drown in identical-looking rows.

  import { useNovaPartsByTag } from '../../telemetry/use-nova-parts-by-tag.svelte';
  import type { NovaTaggedPart } from '../../telemetry/use-nova-parts-by-tag.svelte';
  import ComponentIcon, { type ComponentKind } from '../ComponentIcon.svelte';

  interface Props {
    vesselId: string;
  }
  const { vesselId }: Props = $props();

  const generators = useNovaPartsByTag(() => vesselId, 'power-gen');
  const consumers  = useNovaPartsByTag(() => vesselId, 'power-consume');
  const storage    = useNovaPartsByTag(() => vesselId, 'power-store');

  function generationRate(p: NovaTaggedPart): number {
    if (!p.state) return 0;
    let total = 0;
    for (const s of p.state.solar) total += s.rate;
    for (const e of p.state.engine) total += e.alternatorMaxRate * e.thrustFraction;
    return total;
  }

  function consumptionRate(p: NovaTaggedPart): number {
    if (!p.state) return 0;
    let total = 0;
    for (const w of p.state.wheel) total += w.maxEcRate * w.activity;
    for (const l of p.state.light) total += l.maxEcRate * l.activity;
    return total;
  }

  function batteryStored(p: NovaTaggedPart): number {
    if (!p.state) return 0;
    let total = 0;
    for (const b of p.state.battery) total += b.soc * b.capacity;
    return total;
  }

  function batteryCapacity(p: NovaTaggedPart): number {
    if (!p.state) return 0;
    let total = 0;
    for (const b of p.state.battery) total += b.capacity;
    return total;
  }

  function batteryRate(p: NovaTaggedPart): number {
    if (!p.state) return 0;
    let total = 0;
    for (const b of p.state.battery) total += b.rate;
    return total;
  }

  // Per-row icon choice. State-driven when loaded; falls back to the
  // section's dominant kind so first-frame rows aren't iconless.
  function genKind(p: NovaTaggedPart): ComponentKind {
    if (p.state && p.state.engine.length > 0 && p.state.solar.length === 0) return 'engine';
    return 'solar';
  }
  function consumeKind(p: NovaTaggedPart): ComponentKind {
    if (p.state && p.state.light.length > 0 && p.state.wheel.length === 0) return 'light';
    return 'wheel';
  }

  // A part is "solar" for grouping purposes when it carries solar
  // components and no engine alternator. Engine-with-alternator parts
  // stay top-level so they don't get hidden inside a SOLAR header.
  function isSolarPart(p: NovaTaggedPart): boolean {
    return !!p.state && p.state.solar.length > 0 && p.state.engine.length === 0;
  }

  const genGroups = $derived.by(() => {
    const solar: NovaTaggedPart[] = [];
    const other: NovaTaggedPart[] = [];
    for (const p of generators.current) {
      (isSolarPart(p) ? solar : other).push(p);
    }
    const groupSolar = solar.length > 1;
    return {
      solar,
      other,
      groupSolar,
      // When solar isn't grouped, fold it back into the inline list
      // so render order matches the original (solar parts first).
      inline: groupSolar ? other : [...solar, ...other],
      solarTotal: solar.reduce((a, p) => a + generationRate(p), 0),
    };
  });

  const totals = $derived({
    gen: generators.current.reduce((a, p) => a + generationRate(p), 0),
    consume: consumers.current.reduce((a, p) => a + consumptionRate(p), 0),
    stored: storage.current.reduce((a, p) => a + batteryStored(p), 0),
    capacity: storage.current.reduce((a, p) => a + batteryCapacity(p), 0),
    netStorage: storage.current.reduce((a, p) => a + batteryRate(p), 0),
  });

  function fmtRate(value: number): string {
    const abs = Math.abs(value);
    if (abs < 0.005) return '0.00';
    if (abs >= 100) return value.toFixed(0);
    if (abs >= 10) return value.toFixed(1);
    return value.toFixed(2);
  }

  function fmtSoc(stored: number, capacity: number): string {
    if (capacity <= 0) return '—';
    return ((stored / capacity) * 100).toFixed(0) + '%';
  }
</script>

<section class="pwr">
  <!-- Generation -------------------------------------------------- -->
  <header class="pwr__section-head">
    <span class="pwr__section-title">GENERATION</span>
    <span class="pwr__total" class:pwr__total--zero={totals.gen <= 0}>
      {fmtRate(totals.gen)}<em>EC/s</em>
    </span>
  </header>
  {#if generators.current.length === 0}
    <p class="pwr__empty">No generators on this vessel.</p>
  {:else}
    <ul class="pwr__rows">
      {#if genGroups.groupSolar}
        <li class="pwr__subgroup">
          <header class="pwr__subgroup-head">
            <span class="pwr__row-icon"><ComponentIcon kind="solar" /></span>
            <span class="pwr__subgroup-title">
              SOLAR <em>· {genGroups.solar.length}</em>
            </span>
            <span class="pwr__row-rate"
                  class:pwr__row-rate--zero={genGroups.solarTotal <= 0}>
              {fmtRate(genGroups.solarTotal)}<em>EC/s</em>
            </span>
          </header>
          <ul class="pwr__rows pwr__rows--nested">
            {#each genGroups.solar as p (p.struct.id)}
              <li class="pwr__row pwr__row--nested">
                <span class="pwr__row-icon"><ComponentIcon kind="solar" /></span>
                <span class="pwr__row-name">{p.struct.title}</span>
                <span class="pwr__row-rate">
                  {fmtRate(generationRate(p))}<em>EC/s</em>
                </span>
              </li>
            {/each}
          </ul>
        </li>
      {/if}
      {#each genGroups.inline as p (p.struct.id)}
        <li class="pwr__row">
          <span class="pwr__row-icon"><ComponentIcon kind={genKind(p)} /></span>
          <span class="pwr__row-name">{p.struct.title}</span>
          <span class="pwr__row-rate">
            {fmtRate(generationRate(p))}<em>EC/s</em>
          </span>
        </li>
      {/each}
    </ul>
  {/if}

  <!-- Consumption ------------------------------------------------- -->
  <header class="pwr__section-head">
    <span class="pwr__section-title">CONSUMPTION</span>
    <span class="pwr__total" class:pwr__total--zero={totals.consume <= 0}>
      {fmtRate(totals.consume)}<em>EC/s</em>
    </span>
  </header>
  {#if consumers.current.length === 0}
    <p class="pwr__empty">No consumers on this vessel.</p>
  {:else}
    <ul class="pwr__rows">
      {#each consumers.current as p (p.struct.id)}
        <li class="pwr__row">
          <span class="pwr__row-icon"><ComponentIcon kind={consumeKind(p)} /></span>
          <span class="pwr__row-name">{p.struct.title}</span>
          <span class="pwr__row-rate pwr__row-rate--neg">
            {fmtRate(consumptionRate(p))}<em>EC/s</em>
          </span>
        </li>
      {/each}
    </ul>
  {/if}

  <!-- Storage ----------------------------------------------------- -->
  <header class="pwr__section-head">
    <span class="pwr__section-title">STORAGE</span>
    <span class="pwr__total" class:pwr__total--zero={totals.capacity <= 0}>
      {fmtSoc(totals.stored, totals.capacity)}
      <em>· {fmtRate(totals.netStorage)} EC/s</em>
    </span>
  </header>
  {#if storage.current.length === 0}
    <p class="pwr__empty">No batteries on this vessel.</p>
  {:else}
    <ul class="pwr__rows">
      {#each storage.current as p (p.struct.id)}
        <li class="pwr__row">
          <span class="pwr__row-icon"><ComponentIcon kind="battery" /></span>
          <span class="pwr__row-name">{p.struct.title}</span>
          <span class="pwr__row-rate">
            {fmtSoc(batteryStored(p), batteryCapacity(p))}
            <em>· {fmtRate(batteryRate(p))}</em>
          </span>
        </li>
      {/each}
    </ul>
  {/if}
</section>

<style>
  .pwr {
    display: flex;
    flex-direction: column;
    gap: 0;
  }

  .pwr__section-head {
    display: flex;
    align-items: baseline;
    justify-content: space-between;
    margin: 10px 0 4px;
    padding-bottom: 2px;
    border-bottom: 1px solid var(--line);
    font-family: var(--font-display);
    letter-spacing: 0.18em;
  }
  .pwr__section-head:first-child {
    margin-top: 0;
  }
  .pwr__section-title {
    font-size: 11px;
    color: var(--fg-dim);
  }
  .pwr__total {
    font-size: 11px;
    color: var(--accent);
    text-shadow: 0 0 6px var(--accent-glow);
    font-variant-numeric: tabular-nums;
  }
  .pwr__total--zero {
    color: var(--fg-mute);
    text-shadow: none;
  }
  .pwr__total em {
    font-style: normal;
    font-size: 9px;
    color: var(--fg-mute);
    margin-left: 2px;
    letter-spacing: 0.12em;
  }

  .pwr__rows {
    list-style: none;
    margin: 0;
    padding: 0;
    display: flex;
    flex-direction: column;
  }
  .pwr__row {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 6px;
    padding: 3px 0;
    border-bottom: 1px dashed rgba(26, 35, 53, 0.6);
  }
  .pwr__row:last-child {
    border-bottom: 0;
  }
  .pwr__row-icon {
    flex: 0 0 12px;
    display: inline-flex;
    align-items: center;
    color: var(--fg-mute);
  }
  .pwr__row-name {
    flex: 1 1 auto;
    min-width: 0;
    color: var(--fg);
    font-family: var(--font-mono);
    font-size: 11px;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }
  .pwr__row-rate {
    flex: 0 0 auto;
    color: var(--accent);
    font-family: var(--font-mono);
    font-size: 11px;
    font-variant-numeric: tabular-nums;
  }
  .pwr__row-rate--neg {
    color: var(--warn);
  }
  .pwr__row-rate--zero {
    color: var(--fg-mute);
  }
  .pwr__row-rate em {
    font-style: normal;
    font-size: 8px;
    color: var(--fg-mute);
    margin-left: 2px;
    letter-spacing: 0.12em;
  }

  /* Solar sub-group: a soft header inside Generation, then panels
     listed beneath at the same row rhythm but indented to advertise
     the hierarchy. Borrow accent tint on the icon to set it apart
     from the row icons below. */
  .pwr__subgroup {
    list-style: none;
    margin: 0;
    padding: 0;
    border-bottom: 1px dashed rgba(26, 35, 53, 0.6);
  }
  .pwr__subgroup-head {
    display: flex;
    align-items: center;
    gap: 6px;
    padding: 3px 0;
  }
  .pwr__subgroup-head .pwr__row-icon {
    color: var(--accent);
  }
  .pwr__subgroup-title {
    flex: 1 1 auto;
    font-family: var(--font-display);
    font-size: 10px;
    letter-spacing: 0.16em;
    color: var(--fg-dim);
  }
  .pwr__subgroup-title em {
    font-style: normal;
    color: var(--fg-mute);
    letter-spacing: 0.12em;
    margin-left: 2px;
  }
  .pwr__rows--nested {
    padding-left: 14px;
    border-left: 1px solid var(--line);
    margin: 0 0 2px 5px;
  }
  .pwr__row--nested .pwr__row-name {
    color: var(--fg-dim);
  }

  .pwr__empty {
    margin: 4px 0 4px;
    font-size: 10px;
    color: var(--fg-mute);
    text-align: center;
    letter-spacing: 0.12em;
  }
</style>
