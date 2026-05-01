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

// Bytes → "1.40 MB" / "120 KB" / "512 B" — binary KiB, but labelled with
// the colloquial K/M/G letters that everyone reads as "size on disk".
export function fmtBytes(bytes: number): string {
  const abs = Math.abs(bytes);
  if (abs >= 1024 * 1024 * 1024) return `${fmtMag(bytes / (1024 * 1024 * 1024))} GB`;
  if (abs >= 1024 * 1024)        return `${fmtMag(bytes / (1024 * 1024))} MB`;
  if (abs >= 1024)               return `${fmtMag(bytes / 1024)} KB`;
  return `${Math.round(bytes)} B`;
}

// Seconds → "1d 4h" / "3h 22m" / "12m 04s" / "42s". Whichever two
// adjacent units render the magnitude legibly. Used for "produced …
// ago" and "ETA …" strings.
export function fmtDuration(seconds: number): string {
  const s = Math.max(0, Math.round(seconds));
  if (s >= 86400) {
    const d = Math.floor(s / 86400);
    const h = Math.floor((s % 86400) / 3600);
    return `${d}d ${h}h`;
  }
  if (s >= 3600) {
    const h = Math.floor(s / 3600);
    const m = Math.floor((s % 3600) / 60);
    return `${h}h ${m.toString().padStart(2, '0')}m`;
  }
  if (s >= 60) {
    const m = Math.floor(s / 60);
    const sec = s % 60;
    return `${m}m ${sec.toString().padStart(2, '0')}s`;
  }
  return `${s}s`;
}
