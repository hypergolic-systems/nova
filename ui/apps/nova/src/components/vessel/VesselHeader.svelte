<script lang="ts">
  // Always-visible header at the top of the rack — just the vessel
  // name as the panel's identity anchor. The situation, mass, parts,
  // and crew counts that used to live here moved out: situation
  // belongs in the top-bar status strip, and the other stats are
  // either visible in their respective panels (parts surface as
  // hardware in PWR/THM/PRP; crew in SYS) or telemetry the player
  // rarely needs to scan from the top of the rack.

  import { useNovaVesselState } from '../../telemetry/use-nova-vessel-state.svelte';
  import type { NovaVesselState } from '../../telemetry/nova-topics';

  interface Props {
    vesselId: string | (() => string | undefined);
  }

  let { vesselId }: Props = $props();

  const stateRef = useNovaVesselState(() =>
    typeof vesselId === 'function' ? vesselId() : vesselId,
  );
  const state = $derived<NovaVesselState | undefined>(stateRef.current);
  const name = $derived(state?.name ?? '');
</script>

<header class="vh">
  <div class="vh__name" title={name || 'No vessel'}>{name || '—'}</div>
</header>

<style>
  .vh {
    padding: 12px 14px 11px 14px;
    /* Inscribed bottom seam so the header reads as a distinct
       chassis bay above the accordion. Gradient fades the etched
       line at the edges so it doesn't read as a hard rule. */
    background:
      linear-gradient(
        to bottom,
        rgba(126, 245, 184, 0.05) 0%,
        rgba(126, 245, 184, 0.00) 100%
      ),
      linear-gradient(
        to right,
        transparent 0%,
        var(--line-accent) 18%,
        var(--line-accent) 82%,
        transparent 100%
      );
    background-position: top left, bottom left;
    background-size: 100% 100%, 100% 1px;
    background-repeat: no-repeat;
  }

  .vh__name {
    font-family: var(--font-display);
    font-size: 17px;
    line-height: 1.05;
    letter-spacing: 0.12em;
    text-transform: uppercase;
    color: var(--accent);
    text-shadow: 0 0 10px var(--accent-glow);
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
    margin-top: -1px;
  }
</style>
