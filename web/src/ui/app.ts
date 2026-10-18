import { encode } from '../model/hlc';
import { pastedLines } from '../model/text';
import { dayKey, msUntilMidnight } from '../model/time';
import type { Workspace } from '../model/workspace';
import type { Store } from '../store/persist';
import type { SyncController } from '../sync/controller';
import { loadDraft, prefs, saveDraft, setPrefs, type Mode } from '../store/prefs';
import { coarsePointer, h, isEditable, isIOS, isStandalone, svg } from './dom';
import { haptics, setHaptics } from './haptics';
import { icons } from './icons';
import { installKind, onInstallChange, promptInstall, watchInstall } from './install';
import { animate, POP } from './motion';
import { NotesView } from './notes';
import { Orb } from './orb';
import { Settings } from './settings';
import { TasksView } from './tasks';
import { watchTheme } from './theme';
import { Toasts } from './toast';
import { trackViewport } from './viewport';

const WIDE = matchMedia('(min-width: 900px)');

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

    this.tasks = new TasksView(ws, {
      toasts: this.toasts,
      celebrate: () => this.celebrate(),
      dockInset: () => this.dockInset(),
    });
    this.notes = new NotesView(ws, {
      toasts: this.toasts,
      sentToTasks: () => this.nudgeTasksTab(),
    });
    this.settings = new Settings(ws, store, this.toasts, sync);

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
      this.tasks.primeEnter(this.input.getBoundingClientRect());
      const { added, undo } = this.ws.addTasks(lines);
      this.toasts.show(`Added ${added.length} tasks`, undo);
      haptics.tick();
    });
    this.input.value = loadDraft();
    this.composer.classList.toggle('has-text', this.input.value.trim().length > 0);

    // ---- header
    this.subtitle = h('p.subtitle');
    this.sheetDelete = h('button.icon-button.sheet-delete', { type: 'button', 'aria-label': 'Delete this sheet', title: 'Delete this sheet', onclick: () => this.notes.deleteActive() }, svg(icons.trash));
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
      h('div.actions', {}, this.sheetDelete, this.gear),
    );
    // A small dot on Settings when sync needs you (and only then: syncing itself is silent).
    const attention = () => {
      const p = sync.status.phase;
      const needs = p === 'signin' || p === 'error';
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
      const tasksChanged = change.tasks.size > 0 || change.focus;
      if (tasksChanged) this.tasks.render(change.origin !== 'storage' || !document.hidden);
      if (change.sheets.size) this.notes.render(change.origin);
      // Typing in notes touches only sheets: skip the task summary on every keystroke.
      if (tasksChanged) this.renderSummary();
      else this.renderSubtitle();
    });
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
    const task = this.ws.addTask(text, { asNow });
    this.input.value = '';
    saveDraft('');
    this.composer.classList.remove('has-text');
    if (task) {
      haptics.tick();
      this.toasts.announce(asNow ? `Added and focused: ${task.text}` : `Added: ${task.text}`);
    }
  }

  focusComposer(): void {
    if (this.mode !== 'tasks') this.setMode('tasks');
    this.input.focus();
  }

  // ================================================================== summary

  private renderSummary(): void {
    const s = this.ws.summary();
    this.orb.update(s.open, s.doneToday, s.progress);
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

  /** Just back from signing in to turn sync on: say so, where the result will show. */
  syncTurnedOn(): void {
    this.toasts.show('Sync is on. Your tasks will appear on your other devices.', undefined, { duration: 5000 });
  }

  // ================================================================== launch and keys

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
    if (e.defaultPrevented || document.querySelector('dialog[open]')) return;
    const typing = isEditable(document.activeElement);
    const mod = e.ctrlKey || e.metaKey;
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
 * touched stay behind (deleted here), instead of turning up on every device.
 */
export function dropUntouchedWelcome(ws: Workspace): void {
  const welcome = prefs().welcome;
  if (!welcome) return;
  const tasks = Object.entries(welcome.tasks).filter(([id, text]) => ws.tasks.get(id)?.text === text).map(([id]) => id);
  const sheets = Object.entries(welcome.sheets).filter(([id, text]) => ws.sheets.get(id)?.text === text).map(([id]) => id);
  if (tasks.length || sheets.length) ws.discard({ tasks, sheets }, 'local');
  setPrefs({ welcome: null });
}

