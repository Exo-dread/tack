/**
 * Dates are stored the way Tack for Windows stores them: ISO 8601 with the local offset
 * (e.g. 2026-10-17T21:40:12.345+01:00), so files stay readable and "done today" means
 * today where the task was finished.
 */

const pad = (n: number, width = 2) => String(n).padStart(width, '0');

export function isoLocal(d: Date = new Date()): string {
  const offset = -d.getTimezoneOffset();
  const sign = offset >= 0 ? '+' : '-';
  const abs = Math.abs(offset);
  return (
    `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}` +
    `T${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}.${pad(d.getMilliseconds(), 3)}` +
    `${sign}${pad(Math.floor(abs / 60))}:${pad(abs % 60)}`
  );
}

const ISO = /^(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(?::\d{2})?)(?:[.,](\d+))?(Z|[+-]\d{2}:?\d{2})?$/i;

/**
 * Parses ISO dates, including .NET's seven-digit fractions ("…12.3456789+01:00"),
 * which some JavaScript engines reject. Returns null for anything unreadable.
 */
export function parseDate(value: string | null | undefined): Date | null {
  if (!value) return null;
  const m = ISO.exec(value.trim());
  let d: Date;
  if (m) {
    const ms = (m[2] ?? '').padEnd(3, '0').slice(0, 3);
    let zone = m[3] ?? '';
    if (/^[+-]\d{4}$/.test(zone)) zone = `${zone.slice(0, 3)}:${zone.slice(3)}`;
    const base = m[1].length === 16 ? `${m[1]}:00` : m[1];
    d = new Date(`${base}.${ms}${zone.toUpperCase()}`);
  } else {
    d = new Date(value);
  }
  return Number.isNaN(d.getTime()) ? null : d;
}

export function sameLocalDay(a: Date, b: Date): boolean {
  return a.getFullYear() === b.getFullYear() && a.getMonth() === b.getMonth() && a.getDate() === b.getDate();
}

/** Milliseconds until just after the next local midnight. */
export function msUntilMidnight(now: Date = new Date()): number {
  const next = new Date(now.getFullYear(), now.getMonth(), now.getDate() + 1, 0, 0, 2);
  return next.getTime() - now.getTime();
}

/** "Today" style key for daily snapshots, in local time. */
export function dayKey(d: Date = new Date()): string {
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
}
