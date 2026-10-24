import { MAX_SPACES_LISTED } from '../model/limits';
import { ALL, cleanSpaceName, listed, MAIN } from '../model/spaces';
import { shorten } from '../model/text';
import type { Workspace } from '../model/workspace';
import { prefs, setPrefs } from '../store/prefs';
import { h, svg } from './dom';
import { haptics } from './haptics';
import { icons } from './icons';
import { presentSheet, type SheetHandle } from './sheet';
import type { Toasts } from './toast';

/**
 * Spaces on screen (Work, Dev, Personal...). Nothing here shows until there's a space besides
 * Main: until then Tack looks and behaves exactly as it always has. Then the title says which
 * space is on show and opens the switcher, the task box adds to it, tasks and sheets can be moved,
 * and All shows everything with each task's space beside it.
 *
 * The space on show is this device's own (see Prefs.space), never synced.
 */
export class SpaceUi {
  private readonly listeners = new Set<() => void>();
  private sheet: SheetHandle | null = null;

  constructor(
    private readonly ws: Workspace,
    private readonly toasts: Toasts,
  ) {}

  /** Whether there's a space besides Main (and with it, anything about spaces on screen). */
  get on(): boolean {
    return this.ws.hasSpaces;
  }

  /** The space on show: All, or a space ('main' for Main). A space that's gone shows Main. */
  get show(): string {
    return this.on ? this.ws.shown(prefs().space) : ALL;
  }

  /** Where something new goes from here: the space on show (from All, Main). */
  get here(): string {
    const show = this.show;
    return show === ALL ? MAIN : show;
  }

  label(id: string): string {
    return id === ALL ? 'All' : this.ws.labelOf(id);
  }

  /** Shows a space (or All, `ALL`). */
  setShow(id: string): void {
    if (prefs().space === id && this.show === this.ws.shown(id)) return;
    setPrefs({ space: id });
    this.listeners.forEach((l) => l());
  }

  /** Called whenever the space on show changes. */
  onShow(listener: () => void): () => void {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  }

  /** Makes a space from a typed name; null when the name comes to nothing. */
  create(raw: string): string | null {
    const name = cleanSpaceName(raw);
    if (!name) return null;
    haptics.tick();
    return this.ws.addSpace(name).id;
  }

  // ------------------------------------------------------------------ the switcher

  /** The title's sheet: All, each space with its open tasks, and a new space. */
  openSwitcher(): void {
    if (this.sheet) return;
    const counts = this.ws.openCounts();
    const show = this.show;
    const option = (id: string) => {
      const n = counts.get(id) ?? 0;
      const current = id === show;
      return h(
        'button.space-option',
        { type: 'button', 'data-space': id, 'aria-current': current ? 'true' : null, 'aria-label': `${this.label(id)}, ${n} open`, onclick: () => this.pickShow(id) },
        h('span.space-mark', {}, current ? svg(icons.tick) : null),
        h('span.space-option-name', { text: this.label(id) }),
        h('span.space-option-count', { 'aria-hidden': 'true', text: String(n) }),
      );
    };
    // Enter (or Add) makes the space and shows it; a name left typed is made without switching, as on the desktop.
    const entry = newSpaceEntry((name, confirmed) => {
      const id = this.create(name);
      if (!id) return false;
      if (confirmed) this.pickShow(id);
      return true;
    });
    const list = h('div.space-list');
    listInPages(list, this.ws.spaceTabs, show, (s) => option(s.id), [option(ALL)], [entry.el]);
    this.sheet = presentSheet('Spaces', [list], {
      onClose: () => {
        // Done (or a tap outside, or a swipe) with a name typed: that's a new space, though not the one on show.
        entry.keep();
        this.sheet = null;
      },
      onEscape: () => !entry.cancel(),
    });
    this.sheet.dialog.querySelector<HTMLElement>('[aria-current="true"]')?.focus();
  }

  private pickShow(id: string): void {
    haptics.tick();
    this.setShow(id);
    this.sheet?.close();
  }

  // ------------------------------------------------------------------ picking a space

  /**
   * A sheet listing the spaces (not All). `current` is ticked; with `disableCurrent` it's also
   * the one there's no moving to. `then` gets the space picked; `closed` runs once the sheet has
   * gone (picked or not), when the page can take the focus again.
   */
  pick(title: string, current: string, then: (id: string) => void, options: { disableCurrent?: boolean; closed?: () => void } = {}): void {
    if (this.sheet) return;
    const list = h('div.space-list');
    listInPages(list, this.ws.spaceTabs, current, (s) => {
      const isCurrent = s.id === current;
      return h(
        'button.space-option',
        {
          type: 'button',
          'data-space': s.id,
          'aria-current': isCurrent ? 'true' : null,
          disabled: isCurrent && options.disableCurrent,
          onclick: () => {
            this.sheet?.close();
            then(s.id);
          },
        },
        h('span.space-mark', {}, isCurrent ? svg(icons.tick) : null),
        h('span.space-option-name', { text: s.label }),
      );
    });
    this.sheet = presentSheet(title, [list], { onClose: () => (this.sheet = null), onClosed: options.closed });
    this.sheet.dialog.querySelector<HTMLElement>('.space-option:not(:disabled)')?.focus();
  }

  /**
   * Tap a task → Move: pick a space; it goes there (nothing else about it changes), with Undo.
   * `closed` runs once the sheet has gone, moved or not (see pick).
   */
  moveTask(id: string, closed?: () => void): void {
    const t = this.ws.tasks.get(id);
    if (!t) return;
    this.pick('Move to…', this.ws.spaceOf(t), (to) => {
      const before = this.ws.tasks.get(id)?.space || MAIN;
      this.ws.moveTaskToSpace(id, to);
      haptics.tick();
      this.toasts.show(`Moved to ${shorten(this.label(to), 24)}`, () => this.ws.moveTaskToSpace(id, before));
    }, { disableCurrent: true, closed });
  }

  /** Notes ⋯ → Move sheet to…: the same, for the sheet. */
  moveSheet(id: string, closed?: () => void): void {
    const s = this.ws.sheets.get(id);
    if (!s) return;
    this.pick('Move sheet to…', this.ws.spaceOf(s), (to) => {
      const before = this.ws.sheets.get(id)?.space || MAIN;
      this.ws.moveSheetToSpace(id, to);
      haptics.tick();
      this.toasts.show(`Moved to ${shorten(this.label(to), 24)}`, () => this.ws.moveSheetToSpace(id, before));
    }, { disableCurrent: true, closed });
  }
}

/**
 * Fills `el` with a row per space (`row`) between `before` and `after`, drawing MAX_SPACES_LISTED
 * spaces at a time: past that, a "More spaces" button draws as many again. `keep` (the space on
 * show) is always drawn. Only a crafted file or import makes that many.
 */
export function listInPages<T extends { id: string }>(el: HTMLElement, tabs: readonly T[], keep: string | undefined, row: (tab: T) => HTMLElement, before: Node[] = [], after: Node[] = []): void {
  let limit = MAX_SPACES_LISTED;
  const fill = () => {
    const { list, more } = listed(tabs, limit, keep);
    const button = more > 0
      ? moreSpaces(more, () => {
          // As many again; the first of them takes the focus (the button goes, or moves down).
          const first = limit;
          limit += MAX_SPACES_LISTED;
          fill();
          (el.children[before.length + first] as HTMLElement | undefined)?.focus();
        })
      : null;
    el.replaceChildren(...before, ...list.map(({ tab }) => row(tab)), ...(button ? [button] : []), ...after);
  };
  fill();
}

/** "More spaces", with how many aren't drawn yet. */
export function moreSpaces(more: number, run: () => void): HTMLElement {
  const label = `${more.toLocaleString()} more`;
  return h(
    'button.space-option.space-add.space-more',
    { type: 'button', 'aria-label': `More spaces, ${label}`, onclick: run },
    h('span.space-mark', {}, svg(icons.more)),
    h('span.space-option-name', { text: 'More spaces' }),
    h('span.space-option-count', { 'aria-hidden': 'true', text: label }),
  );
}

/**
 * "+ New space", which turns into a name field: Enter (or Add) makes the space, Escape puts the
 * button back. A name typed is never thrown away: leaving the field for something else on the
 * page, or closing what it's in (`keep`), makes the space too, as on the desktop. `make` gets
 * `confirmed` true for Enter or Add (false for a name kept on the way out), and returns false
 * when the name comes to nothing.
 */
export function newSpaceEntry(make: (name: string, confirmed: boolean) => boolean): { el: HTMLElement; cancel(): boolean; keep(): void } {
  const el = h('div.space-new');
  const button = h('button.space-option.space-add', { type: 'button' }, h('span.space-mark', {}, svg(icons.plus)), h('span.space-option-name', { text: 'New space' }));
  const input = h('input.space-new-name', { type: 'text', maxlength: '100', 'aria-label': 'Name of the new space', placeholder: 'Name', enterkeyhint: 'done', autocomplete: 'off', autocapitalize: 'words', spellcheck: 'false' });
  const add = h('button.text-button', { type: 'button', text: 'Add' });
  const form = h('div.space-new-form', {}, input, add);
  const editing = () => form.isConnected;
  const reset = () => {
    input.value = '';
    el.replaceChildren(button);
  };
  const confirm = () => {
    const name = input.value;
    if (!cleanSpaceName(name)) {
      input.focus();
      return;
    }
    // The field goes first, so closing what it's in (which keeps a name typed) can't make it twice.
    reset();
    make(name, true);
    // Ready for the next one.
    button.focus();
  };
  /** Makes the space from what's typed, if anything, and puts the button back (the focus stays where it went). */
  const keep = () => {
    if (!editing()) return;
    const name = input.value;
    reset();
    if (cleanSpaceName(name)) make(name, false);
  };
  button.addEventListener('click', () => {
    el.replaceChildren(form);
    input.focus();
  });
  add.addEventListener('click', confirm);
  form.addEventListener('focusout', (e) => {
    const next = e.relatedTarget as Node | null;
    // Still naming (on to Add), or the whole window lost the focus (another app): nothing to do yet.
    if ((next && form.contains(next)) || (!next && !document.hasFocus())) return;
    keep();
  });
  input.addEventListener('keydown', (e) => {
    if (e.key === 'Enter' && !e.isComposing) {
      e.preventDefault();
      confirm();
    } else if (e.key === 'Escape') {
      e.preventDefault();
      e.stopPropagation();
      reset();
      button.focus();
    }
  });
  el.append(button);
  return {
    el,
    /** Escape while naming: back to the button (true), else nothing to cancel (false). */
    cancel: () => {
      if (!editing()) return false;
      reset();
      button.focus();
      return true;
    },
    keep,
  };
}
