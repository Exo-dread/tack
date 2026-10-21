import { ZERO } from '../../src/model/hlc';

/**
 * The vectors as they're written to tests/vectors: records the way files carry them, with a task's
 * or sheet's `space` (and its clock) only once it has been put in one, and a workspace's `spaces`
 * only if it has any. Tack for Windows reads a missing field as ('', ZERO) and missing spaces as
 * none, so nothing is lost; and everything recorded before spaces reads exactly as it did then.
 * Device files as written (`written`) are left exactly as they are.
 */
export function unsetSpacesLeftOut<T>(value: T): T {
  return leaveOut(value) as T;
}

function leaveOut(value: unknown): unknown {
  if (Array.isArray(value)) return value.map(leaveOut);
  if (!value || typeof value !== 'object') return value;
  const v = value as Record<string, unknown>;
  const record = typeof v.id === 'string' && isPlain(v.clk);
  const workspace = Array.isArray(v.tasks) && Array.isArray(v.sheets);
  const out: Record<string, unknown> = {};
  for (const [k, x] of Object.entries(v)) {
    if (k === 'written') out[k] = x;
    else if (record && k === 'space' && x === '') continue;
    else if (record && k === 'clk') out[k] = withoutZeroSpace(x as Record<string, unknown>);
    else if (workspace && k === 'spaces' && Array.isArray(x) && x.length === 0) continue;
    else out[k] = leaveOut(x);
  }
  return out;
}

function withoutZeroSpace(clk: Record<string, unknown>): Record<string, unknown> {
  const out: Record<string, unknown> = {};
  for (const [k, x] of Object.entries(clk)) if (!(k === 'space' && x === ZERO)) out[k] = x;
  return out;
}

const isPlain = (v: unknown): boolean => !!v && typeof v === 'object' && !Array.isArray(v);
