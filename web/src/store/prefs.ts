/**
 * Preferences for this device only (theme, accent, which tab was open…). Kept in localStorage
 * so the boot script can apply the theme before the first paint. Never synced: each device
 * keeps its own look, like Tack for Windows keeps its own window position.
 */

export type ThemePref = 'system' | 'light' | 'dark';
export type Mode = 'tasks' | 'notes';

export interface Prefs {
  theme: ThemePref;
  accent: string;
  haptics: boolean;
  doneExpanded: boolean;
  mode: Mode;
  activeSheet: string | null;
  seeded: boolean;
  installHintDismissed: boolean;
  /** The local day Tack was first opened on this device (for the install suggestion). */
  firstDay: string | null;
}

const KEY = 'tack.prefs';
const DRAFT_KEY = 'tack.draft';

const DEFAULTS: Prefs = {
  theme: 'system',
  accent: 'teal',
  haptics: true,
  doneExpanded: false,
  mode: 'tasks',
  activeSheet: null,
  seeded: false,
  installHintDismissed: false,
  firstDay: null,
};

let current: Prefs = load();
const listeners = new Set<(p: Prefs) => void>();

function load(): Prefs {
  try {
    const raw = localStorage.getItem(KEY);
    const parsed = raw ? (JSON.parse(raw) as Partial<Prefs>) : {};
    const p = { ...DEFAULTS, ...parsed };
    if (!['system', 'light', 'dark'].includes(p.theme)) p.theme = 'system';
    if (p.mode !== 'notes') p.mode = 'tasks';
    return p;
  } catch {
    return { ...DEFAULTS };
  }
}

export function prefs(): Readonly<Prefs> {
  return current;
}

export function setPrefs(patch: Partial<Prefs>): void {
  current = { ...current, ...patch };
  try {
    localStorage.setItem(KEY, JSON.stringify(current));
  } catch {
    /* private mode: preferences last for this visit */
  }
  listeners.forEach((l) => l(current));
}

export function onPrefs(listener: (p: Prefs) => void): () => void {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

/** The half-typed task, so closing the app mid-thought loses nothing. */
export function loadDraft(): string {
  try {
    return localStorage.getItem(DRAFT_KEY) ?? '';
  } catch {
    return '';
  }
}

export function saveDraft(text: string): void {
  try {
    if (text) localStorage.setItem(DRAFT_KEY, text);
    else localStorage.removeItem(DRAFT_KEY);
  } catch {
    /* ignore */
  }
}
