import { derivedId } from './ids';
import type { Stamp } from './hlc';
import { keyAfter } from './order';
import { SHEET_FIELDS, TASK_FIELDS, type Focus, type Sheet, type Task } from './types';

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
    if (wins(remote.clk[field], remote[field], local.clk[field], local[field])) {
      if (value === local) value = cloneTask(local);
      (value as unknown as Record<string, unknown>)[field] = remote[field];
      value.clk[field] = remote.clk[field];
    }
  }
  // Creation time never changes; if copies disagree (hand edits), keep the earlier one.
  if (remote.created && remote.created < value.created) {
    if (value === local) value = cloneTask(local);
    value.created = remote.created;
  }
  return { value, changed: value !== local };
}

export interface SheetMerge {
  value: Sheet;
  changed: boolean;
  /** When both sides rewrote the text since `base`, the losing text is kept as a new sheet. */
  conflict?: Sheet;
}

/**
 * Merges a sheet. Notes are prose, so a plain last-writer-wins would silently drop one
 * device's paragraph. Given the clock both devices last agreed on (`base`), concurrent edits
 * keep the later text in place and the other as a copy. The copy's id is derived from the
 * losing edit, so every device creates the same copy and they still converge.
 */
export function mergeSheet(local: Sheet | undefined, remote: Sheet, base?: Stamp): SheetMerge {
  if (!local) return { value: cloneSheet(remote), changed: true };
  let value = local;
  let conflict: Sheet | undefined;

  const textConcurrent =
    base !== undefined && local.clk.text > base && remote.clk.text > base && local.text !== remote.text;

  for (const field of SHEET_FIELDS) {
    if (wins(remote.clk[field], remote[field], local.clk[field], local[field])) {
      if (value === local) value = cloneSheet(local);
      (value as unknown as Record<string, unknown>)[field] = remote[field];
      value.clk[field] = remote.clk[field];
      if (field === 'text') value.updated = remote.updated;
    }
  }

  if (textConcurrent) {
    const loser = value.text === local.text ? remote : local;
    if (loser.text.trim()) {
      conflict = {
        id: derivedId(`${local.id}:${loser.clk.text}`),
        text: loser.text,
        updated: loser.updated,
        order: keyAfter(value.order),
        deleted: false,
        clk: { text: loser.clk.text, order: loser.clk.text, deleted: loser.clk.text },
      };
    }
  }
  return { value, changed: value !== local, conflict };
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
