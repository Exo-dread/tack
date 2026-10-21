import { derivedId } from './ids';
import { ZERO, type Stamp } from './hlc';
import { keyAfter } from './order';
import { spaceField } from './spaces';
import { SHEET_FIELDS, SPACE_FIELDS, TASK_FIELDS, type Focus, type Sheet, type Space, type Task } from './types';

/**
 * Merging two copies of the same record, field by field: the change with the later clock wins.
 * Merges are commutative, associative and idempotent, so devices that have seen the same
 * changes end up identical, whatever order the changes arrived in.
 *
 * Used today to reconcile tabs (and data on disk) and by import; sync reuses it unchanged.
 */

export function mergeTask(local: Task | undefined, remote: Task): { value: Task; changed: boolean } {
  if (!local) return { value: cloneTask(remote), changed: true };
  let value = local;
  for (const field of TASK_FIELDS) {
    // A copy from a version without spaces has no clock for it: ZERO, so it never undoes an assignment.
    if (wins(remote.clk[field] ?? ZERO, remote[field], local.clk[field] ?? ZERO, local[field])) {
      if (value === local) value = cloneTask(local);
      (value as unknown as Record<string, unknown>)[field] = remote[field];
      value.clk[field] = remote.clk[field] ?? ZERO;
    }
  }
  // Creation time never changes; if copies disagree (hand edits), keep the earlier one. A
  // deletion that arrived before the task did (device files carry deletions bare) has none.
  if (remote.created && (!value.created || remote.created < value.created)) {
    if (value === local) value = cloneTask(local);
    value.created = remote.created;
  }
  return { value, changed: value !== local };
}

/**
 * Were these two versions of a sheet's text written without either device having seen the other?
 * Sync answers it from what each device has seen of the others (see sync/engine.ts).
 */
export type ConcurrentText = (local: Sheet, remote: Sheet) => boolean;

export interface SheetMerge {
  value: Sheet;
  changed: boolean;
  /** When both sides rewrote the text since `base`, the losing text is kept as a new sheet. */
  conflict?: Sheet;
}

/**
 * Merges a sheet. Notes are prose, so a plain last-writer-wins would silently drop one
 * device's paragraph. Given the clock both devices last agreed on (`base`), or a test that knows
 * what each side had seen, concurrent edits keep the later text in place and the other as a
 * copy. The copy's id is derived from the losing edit, so every device creates the same copy
 * and they still converge.
 */
export function mergeSheet(local: Sheet | undefined, remote: Sheet, base?: Stamp | ConcurrentText): SheetMerge {
  if (!local) return { value: cloneSheet(remote), changed: true };
  let value = local;
  let conflict: Sheet | undefined;

  const textConcurrent =
    base !== undefined &&
    local.text !== remote.text &&
    (typeof base === 'function'
      ? local.clk.text !== remote.clk.text && base(local, remote)
      : local.clk.text > base && remote.clk.text > base);

  for (const field of SHEET_FIELDS) {
    if (wins(remote.clk[field] ?? ZERO, remote[field], local.clk[field] ?? ZERO, local[field])) {
      if (value === local) value = cloneSheet(local);
      (value as unknown as Record<string, unknown>)[field] = remote[field];
      value.clk[field] = remote.clk[field] ?? ZERO;
      if (field === 'text') value.updated = remote.updated;
    }
  }

  if (textConcurrent) {
    const loser = value.text === local.text ? remote : local;
    if (loser.text.trim()) {
      // The copy stays in the space the losing text was written in.
      const { space, clk } = spaceField(loser.space, loser.clk.space);
      conflict = {
        id: derivedId(`${local.id}:${loser.clk.text}`),
        text: loser.text,
        updated: loser.updated,
        order: keyAfter(value.order),
        deleted: false,
        space,
        clk: { text: loser.clk.text, order: loser.clk.text, deleted: loser.clk.text, space: clk },
      };
    }
  }
  return { value, changed: value !== local, conflict };
}

/** Merges a space, field by field (no copies: a name is short, and the later one wins). */
export function mergeSpace(local: Space | undefined, remote: Space): { value: Space; changed: boolean } {
  if (!local) return { value: cloneSpace(remote), changed: true };
  let value = local;
  for (const field of SPACE_FIELDS) {
    if (wins(remote.clk[field], remote[field], local.clk[field], local[field])) {
      if (value === local) value = cloneSpace(local);
      (value as unknown as Record<string, unknown>)[field] = remote[field];
      value.clk[field] = remote.clk[field];
    }
  }
  return { value, changed: value !== local };
}

export function mergeFocus(local: Focus, remote: Focus): { value: Focus; changed: boolean } {
  if (wins(remote.clk, remote.id, local.clk, local.id)) return { value: { ...remote }, changed: true };
  return { value: local, changed: false };
}

/**
 * Does the remote value replace the local one? Later clock wins. Equal clocks mean the same
 * write (a clock includes its device id); if values still differ (a hand-edited file), the
 * larger serialised value wins so both sides agree.
 */
function wins(remoteClk: Stamp, remoteValue: unknown, localClk: Stamp, localValue: unknown): boolean {
  if (remoteClk !== localClk) return remoteClk > localClk;
  if (remoteValue === localValue) return false;
  return JSON.stringify(remoteValue) > JSON.stringify(localValue);
}

export const cloneTask = (t: Task): Task => ({ ...t, clk: { ...t.clk } });
export const cloneSheet = (s: Sheet): Sheet => ({ ...s, clk: { ...s.clk } });
export const cloneSpace = (s: Space): Space => ({ ...s, clk: { ...s.clk } });
