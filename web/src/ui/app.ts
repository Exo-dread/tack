import { encode } from '../model/hlc';
import { MAX_PASTE_LINES, MAX_TASK_TEXT } from '../model/limits';
import { addShared, type Shared } from '../model/share';
import { ALL } from '../model/spaces';
import { pastedLines } from '../model/text';
import { dayKey, msUntilMidnight } from '../model/time';
import { untouchedWelcome } from '../model/welcome';
import type { Workspace } from '../model/workspace';
import type { Store } from '../store/persist';
import type { SyncController } from '../sync/controller';
import { loadDraft, prefs, saveDraft, setPrefs, type Mode } from '../store/prefs';
import { coarsePointer, h, isEditable, isIOS, isStandalone, svg } from './dom';
import { haptics, setHaptics } from './haptics';
import { icons } from './icons';
import { installKind, onInstallChange, promptInstall, watchInstall } from './install';
import { animate, EASE_OUT, POP } from './motion';
import { NotesView } from './notes';
import { Orb } from './orb';
import { SearchUi } from './search';
import { Settings } from './settings';
import { openMenu, type MenuItem } from './sheet';
import { SpaceUi } from './spaces';
import { TasksView } from './tasks';
import { watchTheme } from './theme';
import { Toasts } from './toast';
import { trackViewport } from './viewport';

const WIDE = matchMedia('(min-width: 900px)');

/** Macs type characters with Option+digit (¡ ™ £ ¢ …): there, Option+digit switches spaces only outside text fields. */
const APPLE = /Mac|iPhone|iPad/.test(navigator.platform) || /Mac OS X/.test(navigator.userAgent);

/**
 * The app shell. On a phone: a slim header (the orb, where you are, and Settings), one page
 * at a time, and a dock at the bottom within reach of your thumb, holding the Tasks/Notes
 * switch and whatever you need next: the task box, the sheet tabs, or the writing tools.
 * On wide screens Tasks and Notes sit side by side.
 */
export class App {
  private readonly root: HTMLElement;
  private readonly toasts = new Toasts();
  private readonly orb = new Orb('orb-header');
  private readonly tasks: TasksView;
  private readonly notes: NotesView;
  private readonly settings: Settings;
  private readonly composer: HTMLFormElement;
  private readonly input: HTMLInputElement;
  private readonly dock: HTMLElement;
  private readonly dockMain: HTMLElement;
  private readonly subtitle: HTMLElement;
  private readonly sheetDelete: HTMLButtonElement;
  private readonly tabs: Record<Mode, HTMLButtonElement>;
  private readonly glow: HTMLElement;
  private readonly gear = h('button.icon-button.settings-open', { type: 'button', 'aria-label': 'Settings', title: 'Settings (,)', onclick: () => this.settings.open() }, svg(icons.gear));
  /** Search, beside Settings on a phone (wide screens have Ctrl+F). */
  private readonly searchButton = h('button.icon-button.search-open', { type: 'button', 'aria-label': 'Search', title: 'Search (Ctrl+F)', 'aria-haspopup': 'dialog', onclick: () => this.search.open() }, svg(icons.search));
  private readonly search: SearchUi;
  private readonly spaces: SpaceUi;
  /** Once there's a space besides Main: the title shows the space on show and switches it. */
  private readonly spaceTitle: HTMLButtonElement;
  /** Notes ⋯: Move sheet to… */
  private readonly sheetMore: HTMLButtonElement;
  private mode: Mode = prefs().mode;
  private midnight: ReturnType<typeof setTimeout> | null = null;

  constructor(
    private readonly ws: Workspace,
    private readonly store: Store,
    sync: SyncController,
  ) {
    watchTheme();
    watchInstall();
    setHaptics(prefs().haptics);

    this.spaces = new SpaceUi(ws, this.toasts);
    this.tasks = new TasksView(ws, {
      toasts: this.toasts,
      celebrate: () => this.celebrate(),
      dockInset: () => this.dockInset(),
      spaces: this.spaces,
      focusComposer: () => this.focusComposer(),
    });
    this.notes = new NotesView(ws, {
      toasts: this.toasts,
      sentToTasks: () => this.nudgeTasksTab(),
      spaces: this.spaces,
      activeChanged: () => this.renderSheetActions(),
    });
    this.settings = new Settings(ws, store, this.toasts, sync, this.spaces);
    this.search = new SearchUi(ws, { spaces: this.spaces, tasks: this.tasks, notes: this.notes, setMode: (mode) => this.setMode(mode) });

    // ---- composer: the fastest way into Tack
    this.input = h('input.composer-input', {
      type: 'text',
      placeholder: 'Add a task',
      'aria-label': 'Add a task',
      enterkeyhint: 'enter',
      autocomplete: 'off',
      autocapitalize: 'sentences',
      spellcheck: 'true',
      maxlength: '2000',
    });
    const send = h('button.send', { type: 'submit', 'aria-label': 'Add task', title: 'Add (Enter). Ctrl+Enter adds it as your focus' }, svg(icons.send));
    this.composer = h('form.composer', { 'aria-label': 'New task' }, h('span.composer-plus', { 'aria-hidden': 'true' }, svg(icons.plus)), this.input, send);
    this.composer.addEventListener('submit', (e) => {
      e.preventDefault();
      this.submit(false);
    });
    this.input.addEventListener('keydown', (e) => {
      if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) {
        e.preventDefault();
        this.submit(true);
      } else if (e.key === 'Escape') {
        this.input.blur();
      }
    });
    this.input.addEventListener('input', () => {
      saveDraft(this.input.value);
      this.composer.classList.toggle('has-text', this.input.value.trim().length > 0);
    });
    this.input.addEventListener('paste', (e) => {
      const text = e.clipboardData?.getData('text/plain') ?? '';
      const lines = pastedLines(text);
      if (lines.length <= 1) return;
      e.preventDefault();
      if (lines.length > MAX_PASTE_LINES) {
        this.toasts.show(`Paste up to ${MAX_PASTE_LINES.toLocaleString('en')} lines at once`);
        return;
      }
      this.tasks.primeEnter(this.input.getBoundingClientRect());
      // Into the space on show (from All, Main).
      const { added, undo } = this.ws.addTasks(lines, this.spaces.here);
      this.toasts.show(`Added ${added.length} tasks`, undo);
      haptics.tick();
    });
    this.input.value = loadDraft();
    this.composer.classList.toggle('has-text', this.input.value.trim().length > 0);

    // ---- header
    this.subtitle = h('p.subtitle');
    this.sheetDelete = h('button.icon-button.sheet-delete', { type: 'button', 'aria-label': 'Delete this sheet', title: 'Delete this sheet', onclick: () => this.notes.deleteActive() }, svg(icons.trash));
    this.sheetMore = h('button.icon-button.sheet-more', { type: 'button', 'aria-label': 'More for this sheet', title: 'More', 'aria-haspopup': 'menu', 'aria-expanded': 'false' }, svg(icons.more));
    this.sheetMore.addEventListener('click', () => {
      const items: MenuItem[] = [{ label: 'Move sheet to…', icon: svg(icons.move), disabled: !this.notes.active, run: () => this.spaces.moveSheet(this.notes.active, () => this.sheetMenuDone()) }];
      // On a phone the ⋯ holds Delete too, leaving the header room for the space's name.
      if (!this.sheetDelete.isConnected) {
        items.push({
          label: 'Delete sheet',
          icon: svg(icons.trash),
          danger: true,
          disabled: !this.notes.active,
          run: () => {
            this.notes.deleteActive();
            this.sheetMenuDone();
          },
        });
      }
      openMenu(this.sheetMore, items);
    });
    this.spaceTitle = h('button.space-title', { type: 'button', 'aria-haspopup': 'dialog', onclick: () => this.spaces.openSwitcher() }, h('span.space-title-name'), svg(icons.chevron));
    const header = h(
      'header.top',
      {},
      h('div.brand', {}, this.orb.el),
      h(
        'div.titles',
        {},
        h('h1.title', {}, h('span.t-tasks', { text: 'Tasks' }), h('span.t-notes', { text: 'Notes' }), h('span.t-wide', { text: 'Tack' })),
        this.subtitle,
      ),
      h('div.actions', {}, this.sheetDelete, this.searchButton, this.gear),
    );
    // A small dot on Settings when sync needs you (and only then: syncing itself is silent).
    const attention = () => {
      const p = sync.status.phase;
      const needs = p === 'signin' || p === 'error' || sync.status.problems.some((x) => x.reason === 'newer');
      this.gear.classList.toggle('attention', needs);
      this.gear.setAttribute('aria-label', needs ? 'Settings (sync needs attention)' : 'Settings');
    };
    sync.onStatus(attention);
    attention();

    // ---- dock
    const tab = (mode: Mode, label: string, icon: string) => {
      const b = h('button.mode-tab', { id: `tab-${mode}`, type: 'button', role: 'tab', 'aria-controls': `page-${mode}`, 'aria-label': label, title: label }, svg(icon));
      b.addEventListener('click', () => this.setMode(mode));
      return b;
    };
    this.tabs = { tasks: tab('tasks', 'Tasks', icons.tasks), notes: tab('notes', 'Notes', icons.notes) };
    const switcher = h('div.mode-switch', { role: 'tablist', 'aria-label': 'Tasks or notes' }, h('span.mode-thumb', { 'aria-hidden': 'true' }), this.tabs.tasks, this.tabs.notes);
    switcher.addEventListener('keydown', (e) => {
      if (e.key === 'ArrowLeft' || e.key === 'ArrowRight') {
        e.preventDefault();
        const next = this.mode === 'tasks' ? 'notes' : 'tasks';
        this.setMode(next);
        this.tabs[next].focus();
      }
    });
    this.dockMain = h('div.dock-main');
    this.dock = h('div.dock', {}, switcher, this.dockMain);

    this.glow = h('div.glow', { 'aria-hidden': 'true' });
    this.root = h(
      'div.app',
      {},
      header,
      h('main.pages', {}, this.tasks.el, this.notes.el),
      this.dock,
      this.toasts.el,
    );
    document.body.prepend(this.glow, this.root);

    // ---- wiring
    ws.subscribe((change) => {
      // Spaces coming, going or renamed change what every list shows.
      const spaces = change.spaces.size > 0;
      if (spaces) this.renderSpaces();
      const tasksChanged = change.tasks.size > 0 || change.focus || spaces;
      if (tasksChanged) this.tasks.render(change.origin !== 'storage' || !document.hidden);
      if (change.sheets.size || spaces) this.notes.render(change.origin);
      // Typing in notes touches only sheets: skip the task summary on every keystroke.
      if (tasksChanged) this.renderSummary();
      else this.renderSubtitle();
    });
    this.spaces.onShow(() => this.showChanged());
    store.onStatus(() => this.renderSubtitle());
    store.onBeforeFlush(() => this.tasks.commitPending());
    trackViewport(() => this.onViewport());
    WIDE.addEventListener('change', () => this.layout());
    document.addEventListener('keydown', this.onKeyDown);
    document.addEventListener('visibilitychange', () => {
      if (!document.hidden) {
        this.renderSummary();
        this.scheduleMidnight();
      }
    });
    this.root.querySelector('.pages')!.addEventListener('pointerdown', (e) => {
      // Tapping the list (not a field) puts the keyboard away.
      if (document.activeElement === this.input && !(e.target as HTMLElement).closest('input, textarea, button')) this.input.blur();
    });

    this.renderSpaces();
    this.layout();
    this.setMode(this.mode, false);
    this.tasks.render(false);
    this.notes.render();
    this.renderSummary();
    this.scheduleMidnight();
    this.handleLaunchIntent();
    this.suggestInstall();
    if (this.store.backend.kind === 'memory') {
      this.toasts.show('This browser isn’t allowing storage, so nothing will be saved', undefined, { duration: 8000 });
    }
  }

  // ================================================================== layout and modes

  private layout(): void {
    const wide = WIDE.matches;
    this.root.classList.toggle('wide', wide);
    if (wide) {
      this.tasks.el.querySelector('.page-inner')!.before(this.composer);
      this.notes.el.prepend(h('div.notes-head', {}, this.notes.sheetBar, this.notes.tools, this.sheetDelete));
    } else {
      this.notes.el.querySelector('.notes-head')?.remove();
      this.dockMain.replaceChildren(this.composer, this.notes.sheetBar, this.notes.tools);
      this.root.querySelector('.actions')!.prepend(this.sheetDelete);
    }
    this.placeSheetActions();
    this.applyInert();
  }

  setMode(mode: Mode, animated = true): void {
    const changed = mode !== this.mode;
    this.mode = mode;
    if (changed) setPrefs({ mode });
    this.root.dataset.mode = mode;
    this.root.classList.toggle('animate-modes', animated);
    for (const m of ['tasks', 'notes'] as const) {
      this.tabs[m].setAttribute('aria-selected', String(m === mode));
      this.tabs[m].tabIndex = m === mode ? 0 : -1;
    }
    this.applyInert();
    this.renderSubtitle();
    if (changed && document.activeElement && isEditable(document.activeElement) && !WIDE.matches) {
      (document.activeElement as HTMLElement).blur();
    }
  }

  /** On a phone the page you can't see is inert: no focus, no screen reader. */
  private applyInert(): void {
    const wide = WIDE.matches;
    this.tasks.el.inert = !wide && this.mode !== 'tasks';
    this.notes.el.inert = !wide && this.mode !== 'notes';
  }

  private onViewport(): void {
    const writing = document.activeElement === this.notes.editor;
    this.dock.classList.toggle('writing', writing);
    this.dock.classList.toggle('composing', document.activeElement === this.input);
    if (this.tasks.isEditing) this.tasks.revealEditing();
    // The dock's height changes what the pages should leave room for.
    this.root.style.setProperty('--dock-h', `${this.dock.offsetHeight}px`);
  }

  private dockInset(): number {
    return WIDE.matches ? 0 : this.dock.offsetHeight + 16;
  }

  // ================================================================== capture

  private submit(asNow: boolean): void {
    const text = this.input.value;
    if (!text.trim()) {
      // Return on an empty box puts the keyboard away: type, return, return, done.
      this.input.blur();
      return;
    }
    if (this.mode !== 'tasks' && !WIDE.matches) this.setMode('tasks');
    if (this.tasks.el.scrollTop > 0) this.tasks.el.scrollTo({ top: 0 });
    this.tasks.primeEnter(this.input.getBoundingClientRect());
    // Into the space on show (from All, Main).
    const task = this.ws.addTask(text, { asNow, space: this.spaces.here });
    this.input.value = '';
    saveDraft('');
    this.composer.classList.remove('has-text');
    if (task) {
      haptics.tick();
      this.toasts.announce(asNow ? `Added and focused: ${task.text}` : `Added: ${task.text}`);
    }
  }

  // ================================================================== spaces

  /** Spaces came, went or were renamed (or the space on show changed): the header and the task box. */
  private renderSpaces(): void {
    const on = this.spaces.on;
    this.root.classList.toggle('spaces', on);
    const title = this.root.querySelector('.title')!;
    if (on) {
      const label = this.spaces.label(this.spaces.show);
      const name = this.spaceTitle.querySelector('span')!;
      if (name.textContent !== label) name.textContent = label;
      this.spaceTitle.setAttribute('aria-label', `Space: ${label}. Switch space`);
      this.spaceTitle.title = 'Switch space (Alt+0 to Alt+9)';
      if (!this.spaceTitle.isConnected) title.append(this.spaceTitle);
    } else {
      this.spaceTitle.remove();
    }
    this.placeSheetActions();
    this.renderComposer();
    this.renderSubtitle();
  }

  /**
   * The sheet's own buttons: Delete (beside the sheets on wide screens, in the header on a phone)
   * and, once there are spaces to move it to, ⋯ (Move sheet to…) beside it. On a phone ⋯ takes
   * Delete in, leaving the header room for the name of the space on show.
   */
  private placeSheetActions(): void {
    const on = this.spaces.on;
    if (WIDE.matches) {
      if (on) this.sheetDelete.before(this.sheetMore);
      else this.sheetMore.remove();
    } else {
      const actions = this.root.querySelector('.actions')!;
      const shown = on ? this.sheetMore : this.sheetDelete;
      (on ? this.sheetDelete : this.sheetMore).remove();
      if (shown.parentElement !== actions) actions.prepend(shown);
    }
    this.renderSheetActions();
  }

  /** A space's blank paper has no sheet yet: nothing to delete or move until the first keystroke makes one. */
  private renderSheetActions(): void {
    if (!this.sheetDelete || !this.sheetMore) return; // still being built
    const blank = !this.notes.active;
    this.sheetDelete.hidden = blank;
    this.sheetMore.hidden = blank;
  }

  /** After the ⋯ menu's Move or Delete: the focus goes back to ⋯ (or to the page, if it's gone). */
  private sheetMenuDone(): void {
    if (this.sheetMore.isConnected && !this.sheetMore.hidden) this.sheetMore.focus();
    else this.notes.el.focus({ preventScroll: true });
  }

  /** The task box says where a task goes: "Add to Work" (in All, or with no spaces: as always). */
  private renderComposer(): void {
    const show = this.spaces.show;
    const space = this.spaces.on && show !== ALL ? this.spaces.label(show) : '';
    const placeholder = space ? `Add to ${space}` : 'Add a task';
    const label = space ? `Add a task to ${space}` : 'Add a task';
    if (this.input.placeholder !== placeholder) this.input.placeholder = placeholder;
    if (this.input.getAttribute('aria-label') !== label) this.input.setAttribute('aria-label', label);
  }

  /** Another space is on show: its tasks, its sheets, its counts. */
  private showChanged(): void {
    // An edit in progress ends, keeping what was typed (Alt+1…9 work while typing).
    const edited = this.tasks.endEditing();
    this.tasks.render(false);
    this.notes.render();
    this.renderSummary();
    this.renderSpaces();
    // The focus stays with that task while it's on show; else typing goes on in the task box.
    if (edited && !this.tasks.focusRow(edited)) this.input.focus();
    // A quick fade, so the switch reads as one.
    for (const el of [this.tasks.el.querySelector('.page-inner'), this.notes.el.querySelector('.paper')]) {
      if (el) animate(el, [{ opacity: 0.25, transform: 'translateY(6px)' }, { opacity: 1, transform: 'none' }], { duration: 220, easing: EASE_OUT });
    }
    this.toasts.announce(`Showing ${this.spaces.label(this.spaces.show)}`);
  }

  focusComposer(): void {
    if (this.mode !== 'tasks') this.setMode('tasks');
    this.input.focus();
  }

  /**
   * Nothing on screen that a reload would drop: no task being edited, no sheet open, a task half
   * typed in the box kept as its draft (which comes back with the page), and, in view, no Undo on
   * offer for a share that just arrived (the app opened for it, untouched: an update would
   * otherwise reload it away before your eyes).
   */
  get settled(): boolean {
    const text = this.input.value;
    return (
      (!text.trim() || loadDraft() === text) &&
      !this.tasks.isEditing &&
      !document.querySelector('dialog[open]') &&
      !(document.visibilityState === 'visible' && this.shareUndo && this.toasts.offers(this.shareUndo))
    );
  }

  /** The Undo of the last share added (see `settled`). */
  private shareUndo: (() => void) | null = null;

  // ================================================================== summary

  private renderSummary(): void {
    // The space on show (All: everything).
    const s = this.ws.summary(this.spaces.show);
    this.orb.update(s.open, s.doneToday, s.progress, this.spaces.on ? this.spaces.label(this.spaces.show) : '');
    this.taskLine =
      s.open === 0 && s.doneToday === 0
        ? 'Nothing open'
        : s.open === 0
          ? `All done · ${s.doneToday} today`
          : `${s.open} open${s.doneToday ? ` · ${s.doneToday} done today` : ''}`;
    document.documentElement.style.setProperty('--progress', String(s.open === 0 && s.doneToday > 0 ? 1 : s.progress));
    this.renderSubtitle();
    const badge = navigator as Navigator & { setAppBadge?: (n: number) => Promise<void>; clearAppBadge?: () => Promise<void> };
    if (isStandalone()) {
      (s.open ? badge.setAppBadge?.(s.open) : badge.clearAppBadge?.())?.catch(() => {});
    }
  }

  private taskLine = '';

  private renderSubtitle(): void {
    const status = this.store.status;
    let text = this.taskLine;
    if (status === 'unsaved') {
      // Saving keeps failing: say so wherever you are, not only in Notes.
      text = 'Not saved yet. Retrying…';
    } else if (WIDE.matches) {
      const date = new Intl.DateTimeFormat(undefined, { weekday: 'long', day: 'numeric', month: 'long' }).format(new Date());
      text = `${date} · ${this.taskLine}`;
    } else if (this.mode === 'notes') {
      text = status === 'saving' ? 'Saving…' : status === 'retrying' ? 'Saving… (retrying)' : this.editedLine();
    }
    // With spaces the title names the space, so the line under it says which page this is.
    if (this.spaces.on && !WIDE.matches && status !== 'unsaved') text = `${this.mode === 'notes' ? 'Notes' : 'Tasks'} · ${text}`;
    if (this.subtitle.textContent !== text) this.subtitle.textContent = text;
    this.subtitle.classList.toggle('warn', status === 'unsaved');
  }

  private editedLine(): string {
    const sheet = this.ws.sheets.get(this.notes.active);
    if (!sheet || !sheet.text) return 'Saved on this device';
    const d = new Date(sheet.updated);
    if (Number.isNaN(d.getTime())) return 'Saved';
    const today = new Date();
    const sameDay = d.toDateString() === today.toDateString();
    const fmt = new Intl.DateTimeFormat(undefined, sameDay ? { hour: 'numeric', minute: '2-digit' } : { day: 'numeric', month: 'short' });
    return `Saved · edited ${fmt.format(d)}`;
  }

  private celebrate(): void {
    this.orb.celebrate();
    this.glow.classList.remove('bloom');
    void this.glow.offsetWidth;
    this.glow.classList.add('bloom');
  }

  private nudgeTasksTab(): void {
    animate(this.tabs.tasks, [{ transform: 'scale(1)' }, { transform: 'scale(1.25)' }, { transform: 'scale(1)' }], { duration: POP.duration, easing: POP.easing });
  }

  /** "Done today" resets at midnight, like the desktop orb. */
  private scheduleMidnight(): void {
    if (this.midnight) clearTimeout(this.midnight);
    this.midnight = setTimeout(() => {
      this.renderSummary();
      this.tasks.render(false);
      this.scheduleMidnight();
    }, msUntilMidnight());
  }

  /**
   * Once, on the second day you use Tack in a browser tab: suggest installing it. An installed app
   * opens in one tap, and (on iPhone especially) its storage is protected from the browser's
   * clean-ups, which can otherwise clear a site's data after a week or so without a visit.
   */
  private suggestInstall(): void {
    const today = dayKey();
    if (!prefs().firstDay) setPrefs({ firstDay: today });
    if (prefs().installHintDismissed || prefs().firstDay === today) return;
    const show = () => {
      const kind = installKind();
      if (this.installCard?.isConnected || prefs().installHintDismissed) {
        if (kind === 'installed') this.installCard?.remove();
        return;
      }
      if (kind === 'prompt' || kind === 'ios') this.showInstallCard(kind);
    };
    show();
    // Chrome offers installation a moment after load.
    onInstallChange(show);
  }

  private installCard: HTMLElement | null = null;

  private showInstallCard(kind: 'prompt' | 'ios'): void {
    const dismiss = () => {
      setPrefs({ installHintDismissed: true });
      card.remove();
    };
    const action =
      kind === 'prompt'
        ? h('button.button.primary', { type: 'button', onclick: () => void promptInstall().then((ok) => ok && dismiss()) }, svg(icons.addHome), 'Install')
        : h('p.note.small', {}, 'Tap ', h('span.inline-icon', {}, svg(icons.share)), ' Share, then ', h('strong', { text: 'Add to Home Screen' }), '.');
    const card = h(
      'aside.install-card',
      { 'aria-label': 'Install Tack' },
      h('div.install-text', {}, h('strong', { text: 'Keep Tack one tap away' }), h('p.note.small', { text: 'Add it to your Home Screen: it opens like an app, works offline, and keeps your tasks safe from the browser’s clean-ups.' })),
      h('div.install-actions', {}, action, h('button.text-button', { type: 'button', text: 'Not now', onclick: dismiss })),
    );
    this.tasks.el.querySelector('.page-inner')!.append(card);
    this.installCard = card;
  }

  /** Just back from signing in to turn sync on (or after being asked to sign in again): say so. */
  syncTurnedOn(again = false): void {
    this.toasts.show(
      again ? 'Signed in again. Sync carries on.' : 'Sync is on. Turn it on in Tack for Windows too, to include your PC.',
      undefined,
      { duration: 6000 },
    );
  }

  // ================================================================== launch and keys

  /**
   * Something shared from another app (Android's share sheet, see main.ts): tasks in the space on
   * show (from All, Main), as a paste adds them, with Undo.
   */
  receiveShare(shared: Shared): void {
    const result = addShared(this.ws, shared, this.spaces.here);
    if (result.kind === 'added') {
      if (this.mode !== 'tasks') this.setMode('tasks', false);
      if (this.tasks.el.scrollTop > 0) this.tasks.el.scrollTo({ top: 0 });
      const n = result.added.length;
      this.shareUndo = result.undo;
      this.toasts.show(n === 1 ? 'Added from Share' : `Added ${n} tasks from Share`, result.undo);
    } else if (result.kind === 'too-many') {
      this.toasts.show(`Share up to ${MAX_PASTE_LINES.toLocaleString('en')} lines at once`);
    } else if (result.kind === 'too-long') {
      this.toasts.show(`A task can be up to ${MAX_TASK_TEXT.toLocaleString('en')} characters`);
    } else {
      this.toasts.show('Nothing to add from Share');
    }
  }

  /** Home-screen shortcuts: /?new opens straight into the task box, /?notes into Notes. */
  private handleLaunchIntent(): void {
    const params = new URLSearchParams(location.search);
    if (params.has('new')) {
      this.setMode('tasks', false);
      requestAnimationFrame(() => this.input.focus());
    } else if (params.has('notes')) {
      this.setMode('notes', false);
    }
    if (location.search || location.hash) history.replaceState(null, '', location.pathname);
  }

  private onKeyDown = (e: KeyboardEvent) => {
    if (e.defaultPrevented) return;
    // Ctrl+F (⌘F): Tack's own search, even while typing. Over Settings and the other sheets,
    // the browser's own find, as before.
    if ((e.ctrlKey || e.metaKey) && !e.altKey && !e.shiftKey && (e.key === 'f' || e.key === 'F')) {
      if (document.querySelector('dialog[open]:not(.search-sheet)')) return;
      e.preventDefault();
      this.search.open();
      return;
    }
    if (document.querySelector('dialog[open]')) return;
    const typing = isEditable(document.activeElement);
    const mod = e.ctrlKey || e.metaKey;
    // Alt+0: All; Alt+1…9: the spaces in order. Even while typing (the draft stays). Only the
    // digits above the letters: Alt with the number pad types characters on Windows.
    const digit = /^Digit(\d)$/.exec(e.code)?.[1];
    if (digit && e.altKey && !mod && !e.shiftKey && this.spaces.on && !(APPLE && typing)) {
      const n = Number(digit);
      const id = n === 0 ? ALL : this.ws.spaceTabs[n - 1]?.id;
      if (id) {
        e.preventDefault();
        this.spaces.setShow(id);
      }
      return;
    }
    if (mod && !e.shiftKey && (e.key === 'z' || e.key === 'Z') && !typing) {
      if (this.toasts.undo()) e.preventDefault();
      return;
    }
    if (typing || mod || e.altKey) return;
    if (e.key === 'n' || e.key === 'N' || e.key === '/') {
      e.preventDefault();
      this.focusComposer();
    } else if (e.key === '1') {
      this.setMode('tasks');
    } else if (e.key === '2') {
      this.setMode('notes');
    } else if (e.key === ',') {
      this.settings.open();
    } else if (e.key === 'Escape' && document.activeElement !== document.body) {
      (document.activeElement as HTMLElement | null)?.blur();
    }
  };
}

/**
 * A first-run list that teaches Tack by using it (as on the desktop). Delete or tick them off.
 * Returns what it wrote (id → text), see Prefs.welcome.
 */
export function seedWelcome(ws: Workspace): { tasks: Record<string, string>; sheets: Record<string, string> } {
  const touch = coarsePointer();
  const tips = touch
    ? [
        'Swipe a task right to focus on it',
        'Swipe left to delete. There’s always Undo',
        'Hold a task, then drag it to reorder',
        'Tap a task to edit it',
      ]
    : [
        'Hover a task and click ◎ to focus on it',
        'Drag a task to reorder it',
        'Click a task to edit it. Press N anywhere to add one',
      ];
  if (!isStandalone()) {
    tips.push(isIOS() ? 'Add Tack to your Home Screen: Share, then Add to Home Screen' : 'Install Tack from your browser’s menu to keep it one tap away');
  }
  const written = { tasks: {} as Record<string, string>, sheets: {} as Record<string, string> };
  ws.batch(() => {
    const add = (text: string, asNow = false) => {
      const t = ws.addTask(text, { asNow });
      if (t) written.tasks[t.id] = t.text;
    };
    for (let i = tips.length - 1; i >= 0; i--) add(tips[i]!);
    add('Tick this circle to finish your first task', true);
    // The earliest clock there is (but still a clock, so it outlasts a reload): whatever you put
    // in focus on another device wins over this one when they first sync, instead of a phone's
    // tutorial replacing your PC's real focus.
    ws.focus = { id: ws.focus.id, clk: encode(1, 0, '') };
    const sheet = ws.ensureSheet();
    if (!sheet.text) {
      ws.editSheet(
        sheet.id,
        'Scrap paper\n\nNotes save as you type. Lists continue themselves:\n- like this\n- press return for the next line\n\nPut the cursor on a line and tap Make task to send it to Tasks.',
      );
      written.sheets[sheet.id] = ws.sheets.get(sheet.id)!.text;
    }
  });
  return written;
}

/**
 * Joining a sync folder your other devices already use: the welcome tasks and sheet you never
 * touched, and a blank sheet never written on, stay behind (deleted here) instead of turning up
 * on every device. A sheet stays if it would be the last one.
 */
export function dropUntouchedWelcome(ws: Workspace): void {
  const { tasks, sheets } = untouchedWelcome(ws, prefs().welcome ?? { tasks: {}, sheets: {} });
  if (tasks.length || sheets.length) ws.discard({ tasks, sheets }, 'local');
  setPrefs({ welcome: null });
}

