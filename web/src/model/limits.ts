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
 * Records a device holds before it takes no more from elsewhere (other devices' files, imports):
 * the rest is room for its own edits and deletions, so its file always stays within MAX_RECORDS.
 */
export const MAX_TAKEN = MAX_RECORDS - 20_000;
/** Spaces (deleted ones included) a device takes in from elsewhere: every live one is a button on screen. */
export const MAX_SPACES = 1_000;
/** Lines one paste (or one "Make task" from a note) turns into tasks. */
export const MAX_PASTE_LINES = 10_000;
