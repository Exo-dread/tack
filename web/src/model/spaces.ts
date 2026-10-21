import { isPlausibleStamp, isStamp, ZERO, type Stamp } from './hlc';
import { cleanTaskText } from './text';
import type { Sheet, Space, Task } from './types';

/**
 * Spaces (Work, Dev, Personal...): tasks and sheets each say which one they're in. Groundwork
 * only for now: the lists don't show them yet, but every device already keeps, merges and syncs
 * them, so they're there when the UI arrives.
 *
 * A task's `space` is '' until it's first put somewhere (it's in Main), else a space id; Main's own
 * id is 'main', for an explicit move back to Main. '' always goes with a ZERO clock, and a ZERO clock
 * with '', so a copy from a version without spaces (which has neither) never undoes an assignment.
 */

/** Main's id: an explicit move back to Main, and Main's own record once it's renamed. */
export const MAIN = 'main';

/**
 * The view of everything, as a device's choice of what to show ('all', 'main' or a space id).
 * Not a space: nothing is ever put in it.
 */
export const ALL = 'all';

/** A space as it's shown: Main is "Main" until it's given a name of its own. */
export function spaceLabel(id: string, name: string): string {
  return name || (id === MAIN ? 'Main' : 'Untitled');
}

/** Space ids follow the record id rule. */
const ID = /^[\w-]{1,64}$/;

export const isSpaceId = (v: unknown): v is string => typeof v === 'string' && ID.test(v);

/** The longest space name, in UTF-16 units (as JavaScript counts; Tack for Windows counts the same). */
export const MAX_SPACE_NAME = 100;

/** Control characters (C0, DEL and C1): invisible, or a line break where U+0085 is one (WPF). */
const CONTROL = /[\u0000-\u001f\u007f-\u009f]/g;

/**
 * A space name as every device keeps it, wherever it comes from (typed, a file, storage): one
 * line, with no control characters (each is a space), and no longer than MAX_SPACE_NAME, cut where
 * a character ends (an emoji is never halved) and tidied again. Cleaning a clean name changes
 * nothing, so devices that read the same name hold the same name.
 */
export function cleanSpaceName(raw: string): string {
  const name = cleanTaskText(raw.replace(CONTROL, ' '));
  if (name.length <= MAX_SPACE_NAME) return name;
  const high = name.charCodeAt(MAX_SPACE_NAME - 1);
  return cleanTaskText(name.slice(0, high >= 0xd800 && high <= 0xdbff ? MAX_SPACE_NAME - 1 : MAX_SPACE_NAME));
}

/**
 * A record's space and its clock: a valid id with a real, non-ZERO clock, or else ('', ZERO). Given
 * `now`, the clock must also be plausible (records read from files). Anything else, a missing field
 * included, means "never assigned": a record is never refused over these two fields. An id that
 * names no known space is kept (its space record may not have arrived yet).
 */
export function spaceField(space: unknown, clk: unknown, now?: number): { space: string; clk: Stamp } {
  const real = clk !== ZERO && (now === undefined ? isStamp(clk) : isPlausibleStamp(clk, now));
  return isSpaceId(space) && real ? { space, clk: clk as Stamp } : { space: '', clk: ZERO };
}

/** A task as memory keeps it, its space normalised (the same object if it already was). */
export function normalTask(t: Task): Task {
  const { space, clk } = spaceField(t.space, t.clk.space);
  if (space === t.space && clk === t.clk.space) return t;
  const c = t.clk;
  return {
    id: t.id, text: t.text, created: t.created, completed: t.completed, order: t.order, deleted: t.deleted, space,
    clk: { text: c.text, completed: c.completed, order: c.order, deleted: c.deleted, space: clk },
  };
}

export function normalSheet(s: Sheet): Sheet {
  const { space, clk } = spaceField(s.space, s.clk.space);
  if (space === s.space && clk === s.clk.space) return s;
  const c = s.clk;
  return { id: s.id, text: s.text, updated: s.updated, order: s.order, deleted: s.deleted, space, clk: { text: c.text, order: c.order, deleted: c.deleted, space: clk } };
}

/**
 * The space a task or sheet shows in: its own if that's a live space, else Main (never assigned,
 * moved back to Main, its space deleted, or a space not known here yet). All's id is never a
 * space, even with a record (only a hand-made file has one).
 */
export function effectiveSpace(space: string, spaces: Iterable<Space>): string {
  if (!space || space === MAIN || space === ALL) return MAIN;
  for (const s of spaces) if (s.id === space && !s.deleted) return space;
  return MAIN;
}
