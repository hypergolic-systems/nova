// Player-facing names for the wire-format ids that science emits.
// Used by both the FileListModal (decoded subject paths) and the
// Instruments section (capability list under each instrument).

export const EXPERIMENT_LABELS: Record<string, string> = {
  'atm-profile': 'Atmospheric Profile',
  'lts':         'Long-Term Study',
};

export function experimentLabel(id: string): string {
  return EXPERIMENT_LABELS[id] ?? id;
}
