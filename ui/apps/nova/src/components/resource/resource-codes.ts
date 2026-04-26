// Per-resource display metadata: short code, unit symbol, and a hue
// triple used to tint the code tile and the segment gauge. Codes are
// capped at 4 chars so tiles can lock to a uniform width.
//
// The wire sends canonical resource names (`Resource.Name` from
// Nova.Core.Resources) so the table is keyed on those. A stub for
// each stock-KSP equivalent is included for the case where a part
// hasn't yet been re-keyed to Nova's registry — same code, same
// hue, just a different lookup key.
//
// Hues are picked to be visually distinct from each other AND from
// the severity colours (warn=amber, alert=red), so a low-fill gauge
// can still flip to severity tint without ambiguity.

export interface ResourceMeta {
  /** Short display code, ≤4 chars. */
  code: string;
  /** Unit symbol for stored amounts (e.g. J for Electric Charge,
   *  L for fuels). Distinct from `rateUnit` because for energy-style
   *  resources the rate is a power (W) rather than a per-second
   *  count of the storage unit. */
  unit: string;
  /** Unit symbol for flow rates. For most resources this is just
   *  `<unit>/s`; for Electric Charge it's W (Watts = J/s). */
  rateUnit: string;
  /** Per-resource hue. Used as the gauge's OK colour via
   *  `--sg-color-tint` and as the code-tile foreground. */
  color: string;
  /** Glow halo for lit gauge cells. ~0.45 alpha of `color`. */
  glow: string;
  /** Subtle tile background tint. ~0.07 alpha. */
  tint: string;
}

// EC is energy: stored quantity is Joules; flow rate is Watts (J/s).
// Every other resource we currently model is a count-or-volume where
// the rate is just the storage unit per second, so they get a default
// rateUnit derived from the storage unit.
const ELECTRIC: ResourceMeta = {
  code: 'EC',
  unit: 'J',
  rateUnit: 'W',
  color: 'var(--accent)',
  glow: 'var(--accent-glow)',
  tint: 'rgba(126, 245, 184, 0.07)',
};

const FUEL: ResourceMeta = {
  // RP-1 / LiquidFuel / SolidFuel — kerosene/copper.
  code: 'LF',
  unit: 'L',
  rateUnit: 'L/s',
  color: '#ff8c5c',
  glow: 'rgba(255, 140, 92, 0.45)',
  tint: 'rgba(255, 140, 92, 0.08)',
};

const OXIDIZER: ResourceMeta = {
  // LOX / Oxidizer — pale cyan, "cold" / oxygen.
  code: 'OX',
  unit: 'L',
  rateUnit: 'L/s',
  color: '#5cd8e8',
  glow: 'rgba(92, 216, 232, 0.45)',
  tint: 'rgba(92, 216, 232, 0.08)',
};

const HYDROGEN: ResourceMeta = {
  // LH2 — ice blue, "cryogenic".
  code: 'LH2',
  unit: 'L',
  rateUnit: 'L/s',
  color: '#7ab8ff',
  glow: 'rgba(122, 184, 255, 0.45)',
  tint: 'rgba(122, 184, 255, 0.08)',
};

const HYPERGOL: ResourceMeta = {
  // Hydrazine / MonoPropellant — magenta-pink, "exotic".
  code: 'N2H4',
  unit: 'L',
  rateUnit: 'L/s',
  color: '#e878d8',
  glow: 'rgba(232, 120, 216, 0.45)',
  tint: 'rgba(232, 120, 216, 0.08)',
};

const XENON: ResourceMeta = {
  // Xenon — violet.
  code: 'Xe',
  unit: 'L',
  rateUnit: 'L/s',
  color: '#b886ff',
  glow: 'rgba(184, 134, 255, 0.45)',
  tint: 'rgba(184, 134, 255, 0.08)',
};

const NEUTRAL: ResourceMeta = {
  // Catch-all for resources without a curated tint (intake air,
  // ablator, ore, modded miscellany). Cool slate so it reads as
  // metadata without competing with the curated palette.
  code: '?',
  unit: 'U',
  rateUnit: 'U/s',
  color: '#a8b8c8',
  glow: 'rgba(168, 184, 200, 0.40)',
  tint: 'rgba(168, 184, 200, 0.06)',
};

const META: Record<string, ResourceMeta> = {
  // Nova canonical names.
  'Electric Charge': ELECTRIC,
  'Liquid Hydrogen': HYDROGEN,
  'Liquid Oxygen': { ...OXIDIZER, code: 'LOX' },
  'RP-1': { ...FUEL, code: 'RP1' },
  'Hydrazine': HYPERGOL,
  'Xenon': XENON,

  // Stock KSP names — share hue with their Nova equivalents. Stock
  // resources are quantified as generic units ("U") with no real-
  // world unit mapping, so they don't get the J/W treatment EC does.
  'ElectricCharge':  ELECTRIC,
  'LiquidFuel':      FUEL,
  'Oxidizer':        OXIDIZER,
  'MonoPropellant':  { ...HYPERGOL, code: 'MP', unit: 'U', rateUnit: 'U/s' },
  'XenonGas':        XENON,
  'IntakeAir':       { ...NEUTRAL, code: 'AIR' },
  'SolidFuel':       { ...FUEL, code: 'SF', unit: 'U', rateUnit: 'U/s' },
  'Ablator':         { ...NEUTRAL, code: 'ABL' },
  'Ore':             { ...NEUTRAL, code: 'ORE' },
};

export function resourceMeta(name: string): ResourceMeta {
  const hit = META[name];
  if (hit) return hit;
  // Synthesize a code from words. Single word: first 4 chars; multi-
  // word: initials, max 4. Keeps the tile width invariant.
  const words = name.split(/[\s\-_]+/).filter(Boolean);
  let code: string;
  if (words.length === 0) code = '?';
  else if (words.length === 1) code = words[0].slice(0, 4).toUpperCase();
  else code = words.slice(0, 4).map((w) => w[0].toUpperCase()).join('');
  return { ...NEUTRAL, code };
}

export function resourceCode(name: string): string {
  return resourceMeta(name).code;
}

// Sort key: pin Electric Charge first (universal resource every
// vessel touches), then alphabetical by canonical name.
export function resourceSortKey(name: string): [number, string] {
  return [name === 'Electric Charge' || name === 'ElectricCharge' ? 0 : 1, name];
}
