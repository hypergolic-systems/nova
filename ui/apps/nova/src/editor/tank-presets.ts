// Tank preset menu entries surfaced by the editor's right-click PAW.
// Mirror of the C# catalog at:
//   mod/Nova.Core/Components/Propulsion/TankPresets.cs
// Preset ids are the wire contract for the `setTankConfig` op — keep
// the strings identical between the two files.

export interface TankPresetEntry {
  readonly id: string;
  readonly label: string;
}

export const TANK_PRESETS: readonly TankPresetEntry[] = [
  { id: 'n2h4',     label: 'Hydrazine' },
  { id: 'rp1',      label: 'RP-1' },
  { id: 'lox',      label: 'Liquid Oxygen' },
  { id: 'lh2',      label: 'Liquid Hydrogen' },
  { id: 'kerolox',  label: 'RP-1 + LOx' },
  { id: 'hydrolox', label: 'LH2 + LOx' },
];
