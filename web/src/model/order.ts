/**
 * Ordering keys ("fractional indexing"). A task's position is a string that sorts between
 * its neighbours, so moving one task rewrites only that task. That keeps reorders cheap and
 * lets two devices reorder different tasks without overwriting each other.
 *
 * Keys are base-62 fractions (digits 0-9A-Za-z, in ASCII order) that never end in "0".
 * Adding to the top or bottom steps a digit instead of halving, so keys grow by one
 * character only every ~60 additions in the same place.
 */

const DIGITS = '0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz';
const BASE = DIGITS.length;
const MID = 'V';

const digit = (c: string | undefined): number => (c === undefined ? -1 : DIGITS.indexOf(c));

export function isOrderKey(value: unknown): value is string {
  return typeof value === 'string' && /^[0-9A-Za-z]*[1-9A-Za-z]$/.test(value);
}

/** A key that sorts after `a`. */
export function keyAfter(a: string): string {
  for (let i = 0; i < a.length; i++) {
    const d = digit(a[i]);
    if (d < BASE - 1) return a.slice(0, i) + DIGITS[d + 1];
  }
  // All z's: extend, starting low so the next ~60 additions stay this length.
  return a ? a + '1' : MID;
}

/** A key that sorts before `b` (and after the empty key). */
export function keyBefore(b: string): string {
  for (let i = 0; i < b.length; i++) {
    const d = digit(b[i]);
    if (d > 1) return b.slice(0, i) + DIGITS[d - 1];
    if (d === 1) return b.slice(0, i) + '0z';
  }
  // Only reachable for malformed keys made of zeros.
  return '0'.repeat(b.length) + MID;
}

/** A key strictly between `a` and `b`; null means "no bound". Requires a < b. */
export function keyBetween(a: string | null, b: string | null): string {
  if (a === null && b === null) return MID;
  if (a === null) return keyBefore(b!);
  if (b === null) return keyAfter(a);
  if (!(a < b)) throw new RangeError(`keyBetween: "${a}" is not before "${b}"`);

  let prefix = '';
  for (let i = 0; ; i++) {
    const da = i < a.length ? digit(a[i]) : 0;
    const db = i < b.length ? digit(b[i]) : BASE;
    if (da === db) {
      prefix += DIGITS[da];
      continue;
    }
    if (db - da > 1) return prefix + DIGITS[(da + db) >> 1];
    // Adjacent digits: b's digit alone works if b continues past it…
    if (i + 1 < b.length) return prefix + DIGITS[db];
    // …otherwise keep a's digit and go just past the rest of a.
    return prefix + DIGITS[da] + keyAfter(a.slice(i + 1));
  }
}

/**
 * The key for inserting at `index` in a list sorted by key. Tolerates duplicate keys
 * (possible after merging two devices' edits) by placing the item just after the earlier one.
 */
export function keyAt(sortedKeys: readonly string[], index: number): string {
  const a = index > 0 ? sortedKeys[index - 1] ?? null : null;
  const b = index < sortedKeys.length ? sortedKeys[index] ?? null : null;
  if (a !== null && b !== null && !(a < b)) return keyAfter(a);
  return keyBetween(a, b);
}
