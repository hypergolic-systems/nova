<script lang="ts">
  // Single archived-subject row: variant label, fidelity bar, source
  // vessel chip, received-UT stamp. Used both inline beneath the
  // experiment grids and as the contents of the cell-hover tooltip.
  //
  // Source vessel name comes off the persisted proto field, never
  // resolved at render-time — handled in the C# wire layer.

  import { fmtUt } from '../../util/units';
  import type { ArchiveSubject } from '../../telemetry/nova-topics';

  interface Props {
    subject:       ArchiveSubject;
    /** Human-readable variant label. Caller does the mapping
     *  (atm-profile: layer name, lts: situation name) so this
     *  component stays experiment-agnostic. */
    variantLabel:  string;
  }
  const { subject, variantLabel }: Props = $props();

  const pct = $derived(Math.round(Math.min(1, Math.max(0, subject.fidelity)) * 100));
  const sliceLabel = $derived(subject.slice >= 0 ? `· slice ${subject.slice + 1}` : '');
</script>

<div class="sr">
  <div class="sr__bar" aria-hidden="true">
    <span class="sr__fill" style:width={`${pct}%`}></span>
  </div>
  <span class="sr__label">{variantLabel}<span class="sr__slice">{sliceLabel}</span></span>
  <span class="sr__pct">{pct}%</span>
  {#if subject.sourceVessel}
    <span class="sr__chip" title={subject.sourceVessel}>{subject.sourceVessel}</span>
  {/if}
  <span class="sr__ut">{fmtUt(subject.receivedAtUt)}</span>
</div>

<style>
  .sr {
    display: grid;
    grid-template-columns: 36px 1fr auto auto auto;
    align-items: center;
    column-gap: 8px;
    padding: 2px 4px;
    font-family: var(--font-mono);
    font-size: 10px;
    color: var(--fg-dim);
    line-height: 1.4;
  }

  /* Fidelity meter — flush left, mint fill, dark trough. Always
     rendered so the row's left edge stays aligned even at 0 %. */
  .sr__bar {
    grid-column: 1;
    position: relative;
    height: 5px;
    background: rgba(0, 0, 0, 0.4);
    border: 1px solid var(--line);
    overflow: hidden;
  }
  .sr__fill {
    position: absolute;
    inset: 0 auto 0 0;
    background: linear-gradient(90deg, var(--accent-dim), var(--accent));
    box-shadow: 0 0 4px var(--accent-glow);
  }

  .sr__label {
    color: var(--fg);
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
    letter-spacing: 0.04em;
  }
  .sr__slice {
    color: var(--fg-mute);
    margin-left: 4px;
  }
  .sr__pct {
    color: var(--accent);
    font-variant-numeric: tabular-nums;
  }
  .sr__chip {
    border: 1px solid var(--line);
    color: var(--fg-dim);
    padding: 0 6px;
    line-height: 14px;
    font-size: 9.5px;
    letter-spacing: 0.04em;
    max-width: 14ch;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
    background: rgba(126, 245, 184, 0.04);
  }
  .sr__ut {
    color: var(--fg-mute);
    font-variant-numeric: tabular-nums;
    letter-spacing: 0.06em;
  }
</style>
