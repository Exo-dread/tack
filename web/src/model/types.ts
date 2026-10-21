import type { Stamp } from './hlc';

/**
 * A task. The first five fields mirror Tack for Windows (tack.json); the rest make it safe to
 * merge edits from several devices: a sort key instead of an array position, a tombstone
 * instead of removal, and a clock per field so independent edits never overwrite each other.
 */
export interface Task {
  id: string;
  text: string;
  /** ISO 8601 with offset. Never changes. */
  created: string;
  /** ISO 8601 with offset, or null while open. */
  completed: string | null;
  /** Position among open tasks (see order.ts); lower sorts first. */
  order: string;
  /** Deleted tasks are kept as tombstones so the deletion can reach other devices. */
  deleted: boolean;
  /** The space it's in (see spaces.ts): '' if never put in one, else a space id ('main' for Main). */
  space: string;
  clk: TaskClocks;
}

export interface TaskClocks {
  text: Stamp;
  completed: Stamp;
  order: Stamp;
  deleted: Stamp;
  /** ZERO exactly when `space` is ''. */
  space: Stamp;
}

/** A sheet of scratch paper; its title is its first line. */
export interface Sheet {
  id: string;
  text: string;
  /** ISO 8601 with offset: when the text last changed. */
  updated: string;
  order: string;
  deleted: boolean;
  space: string;
  clk: SheetClocks;
}

export interface SheetClocks {
  text: Stamp;
  order: Stamp;
  deleted: Stamp;
  space: Stamp;
}

/**
 * A space (Work, Dev, Personal...) that tasks and sheets can be put in. Main, where everything
 * starts, needs no record; it gets one (id 'main') only once it's renamed.
 */
export interface Space {
  id: string;
  /** One line, like a task's text; may be empty. */
  name: string;
  /** Position among spaces (see order.ts). */
  order: string;
  deleted: boolean;
  clk: SpaceClocks;
}

export interface SpaceClocks {
  name: Stamp;
  order: Stamp;
  deleted: Stamp;
}

/** The single task in focus ("Now"), as one register so devices can't disagree. */
export interface Focus {
  id: string | null;
  clk: Stamp;
}

export interface WorkspaceData {
  tasks: Task[];
  sheets: Sheet[];
  focus: Focus;
  spaces: Space[];
}

export type TaskField = keyof TaskClocks;
export type SheetField = keyof SheetClocks;
export type SpaceField = keyof SpaceClocks;

export const TASK_FIELDS: readonly TaskField[] = ['text', 'completed', 'order', 'deleted', 'space'];
export const SHEET_FIELDS: readonly SheetField[] = ['text', 'order', 'deleted', 'space'];
export const SPACE_FIELDS: readonly SpaceField[] = ['name', 'order', 'deleted'];
