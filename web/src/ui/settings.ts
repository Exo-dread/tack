import { exportFile, FileError, importFile } from '../model/file';
import { parseDate } from '../model/time';
import type { Workspace } from '../model/workspace';
import type { Snapshot } from '../store/db';
import { isPersisted, persistenceNeedsPrompt, requestPersistence, type Store } from '../store/persist';
import { prefs, setPrefs, type ThemePref } from '../store/prefs';
import type { SyncController, SyncStatus } from '../sync/controller';
import { h, svg } from './dom';
import { hapticsSupported, setHaptics } from './haptics';
import { icons } from './icons';
import { installKind, onInstallChange, promptInstall } from './install';
import { afterAnimation, animate, EASE_IN, SPRING } from './motion';
import { ACCENTS } from './theme';
import type { Toasts } from './toast';

const VERSION = __APP_VERSION__;
const SOURCE_URL = 'https://github.com/Exo-dread/tack';
const PRIVACY_URL = '/privacy/';

/**
 * Settings, as a sheet that slides up from the bottom (within thumb reach) and can be swiped
 * away. Appearance, feel, your data, installing, and what Tack Focus is (and isn't).
 */
export class Settings {
  private dialog: HTMLDialogElement | null = null;
  private cleanups: (() => void)[] = [];
  private dataStatus: HTMLElement | null = null;

  constructor(
    private readonly ws: Workspace,
    private readonly store: Store,
    private readonly toasts: Toasts,
    private readonly sync: SyncController,
  ) {}

  open(): void {
    if (this.dialog) return;
    const panel = h('div.sheet-panel', {}, ...this.build());
    const dialog = h('dialog.sheet', { 'aria-labelledby': 'settings-title' }, panel);
    this.dialog = dialog;
    document.body.append(dialog);
    dialog.showModal();
    dialog.addEventListener('cancel', (e) => {
      e.preventDefault();
      this.close();
    });
    dialog.addEventListener('click', (e) => {
      if (e.target === dialog) this.close();
    });
    this.dragToDismiss(panel);
    animate(panel, [{ transform: 'translateY(100%)' }, { transform: 'none' }], { duration: SPRING.duration, easing: SPRING.easing });
  }

  close(): void {
    const dialog = this.dialog;
    if (!dialog) return;
    this.dialog = null;
    this.cleanups.splice(0).forEach((c) => c());
    dialog.classList.add('closing');
    const panel = dialog.querySelector<HTMLElement>('.sheet-panel')!;
    const from = getComputedStyle(panel).transform;
    const a = animate(panel, [{ transform: from === 'none' ? 'none' : from }, { transform: 'translateY(100%)' }], {
      duration: 220,
      easing: EASE_IN,
      fill: 'forwards',
    });
    const finish = () => {
      dialog.close();
      dialog.remove();
    };
    afterAnimation(a, finish, 400);
  }

  private build(): Node[] {
    const p = prefs();
    const title = h('div.sheet-head', {}, h('h2#settings-title', { text: 'Settings' }), h('button.text-button', { type: 'button', text: 'Done', onclick: () => this.close() }));

    // --- appearance
    const themes: [ThemePref, string][] = [
      ['system', 'Auto'],
      ['light', 'Light'],
      ['dark', 'Dark'],
    ];
    const themeGroup = h('div.segmented', { role: 'radiogroup', 'aria-label': 'Theme' });
    for (const [value, label] of themes) {
      const b = h('button', { type: 'button', role: 'radio', 'aria-checked': String(p.theme === value), text: label });
      b.addEventListener('click', () => {
        setPrefs({ theme: value });
        themeGroup.querySelectorAll('button').forEach((x) => x.setAttribute('aria-checked', String(x === b)));
      });
      themeGroup.append(b);
    }
    const swatches = h('div.swatches', { role: 'radiogroup', 'aria-label': 'Accent colour' });
    for (const a of ACCENTS) {
      const b = h('button.swatch', { type: 'button', role: 'radio', 'aria-checked': String(p.accent === a.id), 'aria-label': a.name, title: a.name, 'data-accent': a.id });
      b.addEventListener('click', () => {
        setPrefs({ accent: a.id });
        swatches.querySelectorAll('button').forEach((x) => x.setAttribute('aria-checked', String(x === b)));
      });
      swatches.append(b);
    }
    const appearance = group('Appearance', row('Theme', themeGroup), row('Accent', swatches));

    // --- feel
    const feel = hapticsSupported()
      ? group(
          'Feel',
          row(
            'Haptics',
            toggle('Haptics', p.haptics, (on) => {
              setPrefs({ haptics: on });
              setHaptics(on);
            }),
            'A light tap when you tick, swipe or reorder.',
          ),
        )
      : null;

    // --- data
    const persistLine = h('p.note', { text: 'Checking storage…' });
    void this.describeStorage(persistLine);
    const fileInput = h('input', { type: 'file', accept: '.json,application/json', hidden: true });
    fileInput.addEventListener('change', () => {
      const file = fileInput.files?.[0];
      if (file) void this.importFile(file);
      fileInput.value = '';
    });
    const snapList = h('div.snapshots', { hidden: true });
    // Messages about export and import appear here: toasts would sit behind this sheet.
    this.dataStatus = h('p.note.small.data-status', { role: 'status', hidden: true });
    const data = group(
      'Your data',
      h('p.note', {
        text: this.sync.status.enabled
          ? 'Your tasks and notes are kept on this device, and in your OneDrive while sync is on. Tack Focus has no servers of its own.'
          : 'Your tasks and notes are kept on this device only. Tack Focus has no servers and sends nothing anywhere.',
      }),
      persistLine,
      h(
        'div.button-row',
        {},
        h('button.button', { type: 'button', onclick: () => void this.exportData() }, svg(icons.download), 'Export'),
        h('button.button', { type: 'button', onclick: () => fileInput.click() }, svg(icons.upload), 'Import'),
        h('button.button', { type: 'button', 'aria-expanded': 'false', onclick: (e: Event) => void this.toggleSnapshots(snapList, e.currentTarget as HTMLElement) }, svg(icons.restore), 'Restore'),
      ),
      this.dataStatus,
      h('p.note.small', { text: 'Export saves a tack.json file that Tack for Windows can read. Import adds what’s new from either app’s file. Where both changed, the newer edit wins, and a note written in both places is kept twice.' }),
      snapList,
      fileInput,
    );

    // --- install
    const install = h('div');
    const renderInstall = () => install.replaceChildren(...this.installSection());
    renderInstall();
    this.cleanups.push(onInstallChange(renderInstall));

    // --- about
    const about = group(
      'About',
      h('p.note', {}, h('strong', { text: `Tack Focus ${VERSION}` }), ' · the pocket companion to Tack for Windows.'),
      h('p.note', { text: 'Free and open source under the MIT licence. No accounts, no ads, no tracking, no paid tier.' }),
      h('p.note', {}, h('a', { href: SOURCE_URL, target: '_blank', rel: 'noopener noreferrer', text: 'Source code' })),
      h(
        'details.shortcuts',
        {},
        h('summary', { text: 'Keyboard shortcuts' }),
        h(
          'dl',
          {},
          ...[
            ['N or /', 'New task'],
            ['Enter / Ctrl+Enter', 'Add task / add it as your focus'],
            ['1 / 2', 'Tasks / Notes'],
            ['↑ ↓', 'Move between tasks'],
            ['Alt+↑ / Alt+↓', 'Move a task'],
            ['Ctrl+Enter', 'Focus on a task'],
            ['Ctrl+D', 'Mark done'],
            ['Delete', 'Delete a task'],
            ['Ctrl+Z', 'Undo the last delete'],
            ['Ctrl+Enter (notes)', 'Make a task from the line'],
            ['F5 (notes)', 'Insert the date and time'],
          ].flatMap(([k, v]) => [h('dt', {}, h('kbd', { text: k })), h('dd', { text: v })]),
        ),
      ),
    );

    return [h('div.sheet-grip', { 'aria-hidden': 'true' }), title, h('div.sheet-scroll', {}, appearance, feel, this.syncSection(), install, data, about)];
  }

  // ------------------------------------------------------------------ sync

  private syncSection(): HTMLElement | null {
    if (!this.sync.status.available) return null;
    const body = h('div.sync');
    const render = () => body.replaceChildren(...this.syncBody(this.sync.status));
    render();
    this.cleanups.push(this.sync.onStatus(render));
    const tick = setInterval(render, 30_000); // "2 min ago" stays true while you look
    this.cleanups.push(() => clearInterval(tick));
    return group('Sync', body);
  }

  private syncBody(st: SyncStatus): Node[] {
    const privacy = h('a', { href: PRIVACY_URL, target: '_blank', rel: 'noopener', text: 'Privacy' });
    if (!st.enabled) {
      const nodes: (HTMLElement | null)[] = [
        h('p.note', { text: 'Keep Tack Focus and Tack for Windows in step through your own OneDrive. There’s no Tack server in between: your tasks go straight between your devices and your OneDrive.' }),
        h('div.button-row', {}, h('button.button.primary', { type: 'button', onclick: () => void this.sync.turnOn() }, svg(icons.cloud), 'Turn on sync')),
        st.message ? h('p.note.small.warn', { role: 'status', text: st.message }) : null,
        h(
          'p.note.small',
          {},
          'You’ll sign in with Microsoft. Tack can open only its own folder in your OneDrive (Apps › Tack Focus), nothing else. What’s on this device and what’s already there are combined. ',
          privacy,
        ),
      ];
      return nodes.filter((n): n is HTMLElement => n !== null);
    }

    const action = (label: string, run: () => void) => h('button.text-button.inline', { type: 'button', text: label, onclick: run });
    const status = h('p.note.sync-status', { role: 'status', 'data-synced': String(st.lastSync) });
    switch (st.phase) {
      case 'syncing':
        status.append('Syncing…');
        break;
      case 'offline':
        status.append('Offline. Changes are kept here and sync when you’re back online.');
        break;
      case 'renew':
        status.append('Paused until Tack checks in with Microsoft, which it does by itself the next time you open it. ', action('Reconnect now', () => void this.sync.reconnect()));
        break;
      case 'signin':
        status.classList.add('warn');
        status.append('Microsoft asked you to sign in again. ', action('Sign in', () => void this.sync.signInAgain()));
        break;
      case 'error':
        status.classList.add('warn');
        status.append(`${st.message ?? 'Sync stopped.'} `, action('Try again', () => this.sync.syncNow()));
        break;
      default:
        status.append(st.lastSync ? `Up to date · ${ago(st.lastSync)}` : 'Up to date');
    }

    const nodes: Node[] = [
      h('div.setting', {}, h('div.setting-label', {}, h('span', { text: 'OneDrive' }), h('small', { text: st.account?.username || st.account?.name || 'Signed in' })), svg(icons.cloud)),
      status,
    ];
    if (st.peers.length) {
      nodes.push(h('p.note.small', { text: `Also syncing: ${st.peers.map((p) => `${p.name || 'another device'} (${ago(p.written)})`).join(', ')}.` }));
    }
    for (const problem of st.problems) {
      const who = st.peers.find((p) => p.device === problem.device)?.name || 'another device';
      nodes.push(
        h('p.note.small.warn', {
          text:
            problem.reason === 'newer'
              ? `${who} has a newer Tack. Update Tack here to keep syncing with it.`
              : `The sync file from ${who} couldn’t be read. It’ll be written again the next time that device syncs.`,
        }),
      );
    }
    nodes.push(
      h(
        'div.button-row',
        {},
        h('button.button', { type: 'button', onclick: () => this.sync.syncNow() }, 'Sync now'),
        h('button.button', {
          type: 'button',
          text: 'Turn off',
          onclick: () => void this.sync.turnOff().then(() => this.say('Sync is off. Your tasks stay on this device, and in OneDrive for your other devices.')),
        }),
      ),
      h(
        'p.note.small',
        {},
        'Your tasks are saved in your OneDrive as ordinary files: readable by you, by Microsoft as your storage provider, and by any app you’ve given your whole OneDrive to. They aren’t end-to-end encrypted yet. ',
        privacy,
      ),
    );
    return nodes;
  }

  private installSection(): Node[] {
    const kind = installKind();
    if (kind === 'installed') return [];
    if (kind === 'prompt') {
      return [
        group(
          'Install',
          h('p.note', { text: 'Install Tack Focus for one-tap capture. It opens like an app, works offline, and its storage is protected.' }),
          h('div.button-row', {}, h('button.button.primary', { type: 'button', onclick: () => void promptInstall() }, svg(icons.addHome), 'Install Tack')),
        ),
      ];
    }
    if (kind === 'ios') {
      return [
        group(
          'Add to Home Screen',
          h('p.note', {}, 'In Safari, tap ', h('span.inline-icon', {}, svg(icons.share)), ' Share, then ', h('strong', { text: 'Add to Home Screen' }), '.'),
          h('p.note.small', { text: 'Tack then opens full screen and works offline, and Safari won’t clear its storage if you haven’t visited for a while.' }),
        ),
      ];
    }
    return [group('Install', h('p.note', { text: 'Use your browser’s menu (Install app, or Add to Home Screen) to keep Tack one tap away and its storage protected.' }))];
  }

  private async describeStorage(line: HTMLElement): Promise<void> {
    if (this.store.backend.kind === 'memory') {
      line.textContent = 'This browser isn’t letting Tack save anything, so changes will be lost when you close it. Private browsing can cause this.';
      line.classList.add('warn');
      return;
    }
    let persisted = await isPersisted();
    if (persisted === false && !persistenceNeedsPrompt()) persisted = await requestPersistence();
    if (persisted) {
      line.textContent = 'Storage is protected: the browser won’t clear it to free up space.';
    } else if (persisted === false) {
      line.textContent =
        'The browser may clear site data if space runs low or the site goes unused. Installing Tack protects it; exporting now and then keeps a copy.';
      if (persistenceNeedsPrompt()) {
        line.append(
          ' ',
          h('button.text-button.inline', {
            type: 'button',
            text: 'Protect storage',
            onclick: () => void requestPersistence().then(() => this.describeStorage(line)),
          }),
        );
      }
    } else {
      line.textContent = '';
    }
  }

  private async exportData(): Promise<void> {
    await this.store.flush();
    const json = JSON.stringify(exportFile(this.ws), null, 2);
    const day = new Date().toISOString().slice(0, 10);
    const name = `tack-${day}.json`;
    const file = new File([json], name, { type: 'application/json' });
    // On phones, the share sheet is the natural way to save a file (Files, Drive, AirDrop…).
    const nav = navigator as Navigator & { canShare?: (d: ShareData) => boolean };
    if (matchMedia('(pointer: coarse)').matches && nav.canShare?.({ files: [file] })) {
      try {
        await navigator.share({ files: [file], title: 'Tack export' });
        return;
      } catch (error) {
        if ((error as DOMException).name === 'AbortError') return;
      }
    }
    const url = URL.createObjectURL(file);
    const a = h('a', { href: url, download: name });
    document.body.append(a);
    a.click();
    a.remove();
    setTimeout(() => URL.revokeObjectURL(url), 10000);
    this.say(`Saved ${name} to your downloads.`);
  }

  /** A message in the data section of the sheet (export and import results, errors). */
  private say(text: string, warn = false): void {
    const el = this.dataStatus;
    if (!el) return;
    el.textContent = text;
    el.classList.toggle('warn', warn);
    el.hidden = false;
  }

  private async importFile(file: File): Promise<void> {
    if (file.size > 20 * 1024 * 1024) {
      this.say('That file is too large to be a Tack file.', true);
      return;
    }
    try {
      const text = await file.text();
      const before = this.ws.toData();
      const live = (d: { tasks: { deleted: boolean }[]; sheets: { deleted: boolean }[] }) => ({
        tasks: d.tasks.filter((t) => !t.deleted).length,
        sheets: d.sheets.filter((s) => !s.deleted).length,
      });
      const a = live(before);
      importFile(this.ws, text, { lastModified: file.lastModified || undefined });
      const after = this.ws.toData();
      const b = live(after);
      const added = b.tasks - a.tasks;
      const sheets = b.sheets - a.sheets;
      const parts = [added ? `${added} task${added === 1 ? '' : 's'}` : '', sheets ? `${sheets} sheet${sheets === 1 ? '' : 's'}` : ''].filter(Boolean);
      const changed = JSON.stringify(after) !== JSON.stringify(before);
      if (!changed) {
        this.say('Nothing new in that file: everything in it is already here.');
        return;
      }
      // Close the sheet so you can watch the tasks arrive, with Undo in reach (as Restore does).
      const message = parts.length ? `Imported ${parts.join(' and ')}` : 'Imported the latest changes';
      this.close();
      this.toasts.show(message, () => this.ws.restore(before), { duration: 8000 });
    } catch (error) {
      this.say(error instanceof FileError ? error.message : 'Couldn’t read that file.', true);
    }
  }

  private async toggleSnapshots(list: HTMLElement, button: HTMLElement): Promise<void> {
    const show = list.hidden;
    button.setAttribute('aria-expanded', String(show));
    if (!show) {
      list.hidden = true;
      return;
    }
    let snaps: Snapshot[] = [];
    try {
      snaps = await this.store.snapshots();
    } catch {
      /* none */
    }
    const fmt = new Intl.DateTimeFormat(undefined, { weekday: 'short', day: 'numeric', month: 'short' });
    list.replaceChildren(
      h('p.note.small', { text: 'Tack keeps a copy of your data from the start of each of the last seven days you used it. Restoring one replaces what’s here now (and can be undone).' }),
      ...(snaps.length
        ? snaps.map((s) => {
            const tasks = s.data.tasks.filter((t) => !t.deleted).length;
            const sheets = s.data.sheets.filter((x) => !x.deleted).length;
            const when = parseDate(s.savedAt) ?? new Date(s.day);
            return h(
              'div.snapshot',
              {},
              h('span', {}, h('strong', { text: fmt.format(when) }), ` · ${tasks} task${tasks === 1 ? '' : 's'}, ${sheets} sheet${sheets === 1 ? '' : 's'}`),
              h('button.text-button', {
                type: 'button',
                text: 'Restore',
                onclick: (e: Event) => {
                  // Two taps: restoring replaces everything since that day (Undo stays available).
                  const b = e.currentTarget as HTMLButtonElement;
                  if (b.dataset.armed !== 'true') {
                    b.dataset.armed = 'true';
                    b.textContent = 'Tap to restore';
                    setTimeout(() => {
                      b.dataset.armed = '';
                      b.textContent = 'Restore';
                    }, 4000);
                    return;
                  }
                  const before = this.ws.toData();
                  this.ws.restore(s.data);
                  this.close();
                  this.toasts.show(`Restored ${fmt.format(when)}`, () => this.ws.restore(before), { duration: 8000 });
                },
              }),
            );
          })
        : [h('p.note', { text: 'No snapshots yet.' })]),
    );
    list.hidden = false;
  }

  private dragToDismiss(panel: HTMLElement): void {
    let start: { y: number; t: number; id: number } | null = null;
    let dy = 0;
    const grip = panel.querySelector<HTMLElement>('.sheet-grip')!;
    const head = panel.querySelector<HTMLElement>('.sheet-head')!;
    const begin = (e: PointerEvent) => {
      if ((e.target as HTMLElement).closest('button')) return;
      start = { y: e.clientY, t: e.timeStamp, id: e.pointerId };
      dy = 0;
      (e.currentTarget as HTMLElement).setPointerCapture(e.pointerId);
    };
    const move = (e: PointerEvent) => {
      if (!start || e.pointerId !== start.id) return;
      dy = Math.max(0, e.clientY - start.y);
      panel.style.transform = `translateY(${dy}px)`;
    };
    const end = (e: PointerEvent) => {
      if (!start || e.pointerId !== start.id) return;
      const v = dy / Math.max(1, e.timeStamp - start.t);
      start = null;
      if (dy > 110 || (v > 0.6 && dy > 30)) {
        this.close();
      } else {
        const from = panel.style.transform;
        panel.style.transform = '';
        animate(panel, [{ transform: from || 'none' }, { transform: 'none' }], { duration: SPRING.duration, easing: SPRING.easing });
      }
    };
    for (const el of [grip, head]) {
      el.addEventListener('pointerdown', begin);
      el.addEventListener('pointermove', move);
      el.addEventListener('pointerup', end);
      el.addEventListener('pointercancel', end);
    }
  }
}

/** "just now", "5 min. ago", "yesterday"... */
function ago(ms: number, now = Date.now()): string {
  const s = Math.max(0, Math.round((now - ms) / 1000));
  if (s < 60) return 'just now';
  const rtf = new Intl.RelativeTimeFormat(undefined, { numeric: 'auto', style: 'short' });
  if (s < 3600) return rtf.format(-Math.round(s / 60), 'minute');
  if (s < 86400) return rtf.format(-Math.round(s / 3600), 'hour');
  return rtf.format(-Math.round(s / 86400), 'day');
}

function group(title: string, ...children: (Node | null)[]): HTMLElement {
  return h('section.group', {}, h('h3.group-title', { text: title }), h('div.group-body', {}, ...children));
}

function row(label: string, control: Node, hint?: string): HTMLElement {
  return h('div.setting', {}, h('div.setting-label', {}, h('span', { text: label }), hint ? h('small', { text: hint }) : null), control);
}

function toggle(label: string, on: boolean, onChange: (on: boolean) => void): HTMLElement {
  const b = h('button.switch', { type: 'button', role: 'switch', 'aria-checked': String(on), 'aria-label': label }, h('span.knob'));
  b.addEventListener('click', () => {
    const next = b.getAttribute('aria-checked') !== 'true';
    b.setAttribute('aria-checked', String(next));
    onChange(next);
  });
  return b;
}
