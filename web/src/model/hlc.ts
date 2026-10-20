/**
 * Hybrid logical clock. Every field change is stamped, and stamps compare as plain strings:
 *
 *   <ms since epoch, base 36, 9 chars>.<counter, base 36, 4 chars>.<device id>
 *
 * Stamps keep increasing on one device even if the wall clock jumps backwards, and after
 * seeing another device's stamp a device never issues an older one. Ties between devices are
 * broken by device id, so every device picks the same winner (the basis of sync merges).
 */

export type Stamp = string;

export const ZERO: Stamp = '000000000.0000.0';

const MS_WIDTH = 9;
const COUNTER_WIDTH = 4;
const MAX_COUNTER = 36 ** COUNTER_WIDTH - 1;
/**
 * Stamps further ahead than this (a year and a day) can only be corrupt: files holding them are
 * refused and clocks don't adopt them. Anything nearer is, even if another device's clock is
 * wrong: whatever you do next must win over what you've seen. Files and clocks use the same
 * window, so a device whose clock once ran far ahead isn't left stamping what no one accepts.
 */
export const MAX_AHEAD_MS = 366 * 24 * 60 * 60 * 1000;

export function encode(ms: number, counter: number, node: string): Stamp {
  return `${ms.toString(36).padStart(MS_WIDTH, '0')}.${counter.toString(36).padStart(COUNTER_WIDTH, '0')}.${node}`;
}

export function decode(stamp: Stamp): { ms: number; counter: number; node: string } | null {
  const [ms, counter, node] = stamp.split('.');
  if (!ms || !counter || node === undefined) return null;
  const m = parseInt(ms, 36);
  const c = parseInt(counter, 36);
  if (!Number.isFinite(m) || !Number.isFinite(c)) return null;
  return { ms: m, counter: c, node };
}

/** Wall-clock time of a stamp (0 if unreadable). */
export function stampTime(stamp: Stamp): number {
  return decode(stamp)?.ms ?? 0;
}

export function isStamp(value: unknown): value is Stamp {
  return typeof value === 'string' && /^[0-9a-z]{9}\.[0-9a-z]{4}\.[0-9a-zA-Z_-]{0,32}$/.test(value);
}

/** A stamp from a file that's plausible: well-formed and not more than a year in the future. */
export function isPlausibleStamp(value: unknown, now = Date.now()): value is Stamp {
  return isStamp(value) && stampTime(value) <= now + MAX_AHEAD_MS;
}

export class Hlc {
  private ms = 0;
  private counter = 0;

  constructor(
    private _node: string,
    private readonly wallClock: () => number = Date.now,
  ) {}

  /** This device's id, the last part of every stamp it issues. */
  get node(): string {
    return this._node;
  }

  /**
   * Starts stamping as a different device (sync found another device using this id, e.g. a
   * copied data folder). The clock itself carries on, so new stamps still sort after old ones.
   */
  renew(node: string): void {
    this._node = node;
  }

  /** A new stamp, later than every stamp issued or observed before. */
  now(): Stamp {
    const wall = this.wallClock();
    if (wall > this.ms) {
      this.ms = wall;
      this.counter = 0;
    } else if (this.counter < MAX_COUNTER) {
      this.counter++;
    } else {
      this.ms++;
      this.counter = 0;
    }
    return encode(this.ms, this.counter, this.node);
  }

  /** Moves the clock past a stamp seen elsewhere (another device, or our own saved data). */
  observe(stamp: Stamp): void {
    const d = decode(stamp);
    if (!d) return;
    if (d.ms > this.wallClock() + MAX_AHEAD_MS) return;
    if (d.ms > this.ms || (d.ms === this.ms && d.counter > this.counter)) {
      this.ms = d.ms;
      this.counter = d.counter;
    }
  }
}
