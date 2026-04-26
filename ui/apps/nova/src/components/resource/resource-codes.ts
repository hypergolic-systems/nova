// Resource name → short display code. The wire sends canonical resource
// names (`Resource.Name` from Nova.Core.Resources, or the stock KSP
// resource name when a non-Nova resource happens to slip through), so
// the lookup keys both forms.
//
// When a code isn't in the table we fall back to a deterministic
// abbreviation: first letter of each whitespace-or-dash-separated word
// (max 3), or the first 3 chars if there's only one word. So new
// modded resources still get a sane tile without code changes — they
// just don't get the curated 2-letter symbol.

const CODES: Record<string, string> = {
  // Nova.Core.Resources canonical names.
  'Electric Charge': 'EC',
  'Liquid Hydrogen': 'LH2',
  'Liquid Oxygen': 'LOX',
  'RP-1': 'RP1',
  'Hydrazine': 'N2H4',
  'Xenon': 'Xe',
  // Stock KSP names — sometimes still arrive on parts that haven't
  // been re-keyed to Nova's resource registry yet.
  'ElectricCharge': 'EC',
  'LiquidFuel': 'LF',
  'Oxidizer': 'OX',
  'MonoPropellant': 'MP',
  'XenonGas': 'Xe',
  'IntakeAir': 'AIR',
  'SolidFuel': 'SF',
  'Ablator': 'ABL',
  'Ore': 'ORE',
};

export function resourceCode(name: string): string {
  const hit = CODES[name];
  if (hit) return hit;
  const words = name.split(/[\s\-_]+/).filter(Boolean);
  if (words.length === 0) return '?';
  if (words.length === 1) return words[0].slice(0, 3).toUpperCase();
  return words.slice(0, 3).map((w) => w[0].toUpperCase()).join('');
}

// Sort key: pin Electric Charge first (it's the universal resource
// every vessel touches), then alphabetical. Used by the Resource view's
// BY-RESOURCE list so EC stays at the top regardless of vessel layout.
export function resourceSortKey(name: string): [number, string] {
  return [name === 'Electric Charge' || name === 'ElectricCharge' ? 0 : 1, name];
}
