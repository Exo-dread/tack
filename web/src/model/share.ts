import { MAX_TASK_TEXT } from './limits';
import { cleanTaskText, pastedLines, wellFormed } from './text';
import type { Task } from './types';
import type { Undo, Workspace } from './workspace';

/**
 * What another app shared with Tack Focus through the system share sheet (the manifest's
 * share_target, on Android): three plain strings, any of them empty. They come from a page
 * address, so anyone could have written them: they only ever become task text, through the same
 * cleaning and limits as a paste, and are never shown as markup, followed or opened.
 */
export interface Shared {
  title: string;
  text: string;
  url: string;
}

/** The fields of a share, as the manifest's share_target names them in the address. */
const FIELDS = ['title', 'text', 'url'] as const;

/** A line that is one web address and nothing else. */
const LINK = /^https?:\/\/\S+$/i;

/**
 * The share in a page address's query (`location.search`), or null if it isn't one. The
 * manifest's action is "/?share", but a browser may put the shared fields in place of that query
 * (Android does), so any of the fields marks a share too.
 */
export function readShare(search: string): Shared | null {
  const params = new URLSearchParams(search);
  if (!params.has('share') && !FIELDS.some((f) => params.has(f))) return null;
  const field = (f: (typeof FIELDS)[number]) => wellFormed(params.get(f) ?? '');
  return { title: field('title'), text: field('text'), url: field('url') };
}

/** A share kept in browser storage (see ui/share.ts), read back as strictly as one from the address. */
export function keptShare(value: unknown): Shared | null {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) return null;
  const v = value as Record<string, unknown>;
  if (!FIELDS.every((f) => typeof v[f] === 'string')) return null;
  return { title: wellFormed(v.title as string), text: wellFormed(v.text as string), url: wellFormed(v.url as string) };
}

/**
 * The tasks a share makes, before any limit. One line of text is one task: the title and the
 * text (whichever says more, or both), with the address added if it isn't there already. Android
 * sends a shared page as its title and, for text, its address. Text of several lines is one task
 * per line, cleaned as a paste is (bullets and numbering stripped, blanks dropped), with the title
 * first if the text doesn't already say it; a line that is only an address joins the line above
 * it (unless that's an address too), so "a place, then its link" stays together.
 */
export function sharedLines(shared: Shared): string[] {
  const title = cleanTaskText(shared.title);
  let url = cleanTaskText(shared.url);
  const lines = pastedLines(shared.text);

  if (lines.length <= 1) {
    let text = lines[0] ?? '';
    if (!url && LINK.test(text)) {
      url = text;
      text = '';
    }
    let task = combine(title, text);
    if (url && !mentions(task, url)) task = task ? `${task} ${url}` : url;
    return task ? [task] : [];
  }

  if (title && !lines.join(' ').toLowerCase().includes(title.toLowerCase())) lines.unshift(title);
  if (url && !lines.some((line) => mentions(line, url))) lines.push(url);
  const tasks: string[] = [];
  /** Can the last task still take an address (a line of words that hasn't had one joined yet)? */
  let open = false;
  for (const line of lines) {
    const link = LINK.test(line);
    const above = tasks.at(-1);
    if (link && open && above !== undefined && above.length + 1 + line.length <= MAX_TASK_TEXT) {
      tasks[tasks.length - 1] = `${above} ${line}`;
      open = false;
    } else {
      tasks.push(line);
      open = !link;
    }
  }
  return tasks;
}

/** A title and one line of text as one task: both, unless one already says the other. */
function combine(title: string, text: string): string {
  if (!title || !text) return title || text;
  const t = title.toLowerCase();
  const x = text.toLowerCase();
  if (x.includes(t)) return text;
  if (t.includes(x)) return title;
  return /[:;,.!?…–—-]$/.test(title) ? `${title} ${text}` : `${title}: ${text}`;
}

/** Does this task text already hold that address (give or take a closing slash)? */
function mentions(task: string, url: string): boolean {
  const bare = url.replace(/\/+$/, '');
  return task.split(' ').some((word) => word.replace(/\/+$/, '') === bare);
}

/**
 * Lines one share turns into tasks: the same as one drop onto Tack for Windows' orb
 * (DropLimits.MaxItems). A share is something you just saw, not a list to import, and anyone can
 * write a link to /?share, so it's small, and it's only ever added once you tap Add.
 */
export const MAX_SHARE_LINES = 20;

export type SharePreview =
  /** The tasks Add would make (each one a task can be), at most MAX_SHARE_LINES. */
  | { kind: 'ready'; lines: string[] }
  /** Nothing to make a task of. */
  | { kind: 'empty' }
  /** More lines than one share takes (MAX_SHARE_LINES): nothing to add. */
  | { kind: 'too-many' }
  /** Only lines longer than a task can be (MAX_TASK_TEXT), which a paste skips too. */
  | { kind: 'too-long' };

/** What a share would add, without adding anything: what the card asking "Add?" shows. */
export function previewShare(shared: Shared): SharePreview {
  const lines = sharedLines(shared);
  if (lines.length === 0) return { kind: 'empty' };
  if (lines.length > MAX_SHARE_LINES) return { kind: 'too-many' };
  const fit = lines.filter((line) => line.length <= MAX_TASK_TEXT);
  return fit.length ? { kind: 'ready', lines: fit } : { kind: 'too-long' };
}

export type ShareResult = { kind: 'added'; added: Task[]; undo: Undo } | Exclude<SharePreview, { kind: 'ready' }>;

/**
 * Adds what was shared as tasks in `space` (as a paste adds them), with one Undo for them all.
 * Only once you've said so: the app shows `previewShare` first and calls this on Add.
 */
export function addShared(ws: Workspace, shared: Shared, space?: string): ShareResult {
  const preview = previewShare(shared);
  if (preview.kind !== 'ready') return preview;
  const { added, undo } = ws.addTasks(preview.lines, space);
  return added.length ? { kind: 'added', added, undo } : { kind: 'too-long' };
}
