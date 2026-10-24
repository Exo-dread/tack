/**
 * How much one device holds and passes on, far beyond any real use (thousands of tasks are well
 * under 2 MB). Every device refuses a sync file over these (sync/devicefile.ts), so a device never
 * takes in more than its own file may carry: not from an import, a paste, or other devices' files.
 * Tack for Windows uses the same numbers (DeviceFile.cs).
 */

/** Records in one device file: tasks + sheets + spaces + deletions. */
export const MAX_RECORDS = 200_000;
export const MAX_TASK_TEXT = 100_000;
export const MAX_NOTE_TEXT = 1_000_000;
/**
 * Records a device holds before it takes no more in bulk from elsewhere (other devices' files,
 * imports): the rest is room for its own edits and deletions, so its file stays within MAX_RECORDS.
 */
export const MAX_TAKEN = MAX_RECORDS - 20_000;
/**
 * New records one device file may still bring past MAX_TAKEN (a peer's everyday edits), up to
 * MAX_RECORDS: so devices that share a lot keep syncing, while a file that would add more at once
 * (a whole workspace, a flood) is held to MAX_TAKEN.
 */
export const MAX_TRICKLE = 2_000;
/**
 * Spaces one list draws at once (the switcher, Move to, Settings; Tack for Windows' strip and menus
 * too): "More spaces" draws as many again. Only a crafted file or import brings more than a few
 * dozen, and every one is kept and synced: a limit on what a device takes in would stop devices
 * that differ by a single space taking each other's files, so the screen is protected instead.
 */
export const MAX_SPACES_LISTED = 1_000;
/** Lines one paste (or one "Make task" from a note) turns into tasks. */
export const MAX_PASTE_LINES = 10_000;
