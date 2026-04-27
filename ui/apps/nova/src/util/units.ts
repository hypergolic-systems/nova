// SI-prefix scaling for display. Picks k/M/G when the magnitude clears
// 1e3/1e6/1e9 so a 1,200 J reading renders as "1.20 kJ" instead of an
// ever-widening digit run. Both values in a pair (stored/cap, current/
// max) are scaled by the same prefix — derived from the larger of the
// two — so the divisor and dividend share units.
//
// Formatting splits magnitude from prefix. The caller composes the
// final string with whatever base unit applies (J/W for energy, L for
// volume, raw count for unitless), so the same helper drives both
// energy and volumetric resources.

export interface Prefix {
  /** Letter prepended to the base unit. Empty string for the base scale. */
  letter: string;
  /** Divisor applied to the value before formatting (1, 1e3, 1e6, 1e9). */
  div: number;
}

export function siPrefix(referenceValue: number): Prefix {
  const abs = Math.abs(referenceValue);
  if (abs >= 1e9) return { letter: 'G', div: 1e9 };
  if (abs >= 1e6) return { letter: 'M', div: 1e6 };
  if (abs >= 1e3) return { letter: 'k', div: 1e3 };
  return { letter: '', div: 1 };
}

// Per-magnitude precision: 100+ shows no decimals, 10..99 one decimal,
// otherwise two. Sub-eps rounds to "0.00" so a tiny residual flow
// doesn't spam digits.
export function fmtMag(value: number): string {
  const abs = Math.abs(value);
  if (abs < 0.005) return '0.00';
  if (abs >= 100) return value.toFixed(0);
  if (abs >= 10) return value.toFixed(1);
  return value.toFixed(2);
}
