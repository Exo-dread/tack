import { newDeviceId } from '../model/ids';
import type { Workspace } from '../model/workspace';
import type { Backend } from '../store/db';
import type { Store } from '../store/persist';
import { DeviceCollision, emptyMeta, SyncEngine, SyncError, takeIn, type CycleResult, type PeerInfo, type SyncFolder, type SyncMeta } from './engine';
import { GoogleAuth, takeGoogleReturn, type GoogleReturn } from './gauth';
import { GoogleDriveFolder } from './googledrive';
import { AuthError, emptyAuth, isSignInReturn, MicrosoftAuth, SCOPES, type Account, type AuthRecord, type Tokens } from './msauth';
import { OneDriveFolder } from './onedrive';

/**
 * Runs sync quietly in the background: once you've turned it on, nothing to press.
 *
 * When: shortly after the app opens, a few seconds after you change something, when the app
 * comes back to the front or back online, and every minute while it's on screen. Never while
 * hidden (a last upload is attempted as it goes). One tab at a time does the work.
 *
 * Staying signed in: see msauth.ts. The controller renews quietly before the 24-hour limit,
 * in a hidden frame where the browser allows it, or else with a quick visit to Microsoft at the
 * next quiet moment (opening the app, or coming back to it after a while).
 *
 * Or Google Drive instead (one provider per device, see gauth.ts and googledrive.ts): staying
 * signed in is the sign-in Worker's job, so none of the above applies, and nothing of it is kept
 * here but the account's address.
 */

export type SyncProvider = 'onedrive' | 'google';

export type SyncPhase =
  /** Not turned on (or not available in this build). */
  | 'off'
  /** On, and working on it right now. */
  | 'syncing'
  /** On, and up to date as of `lastSync`. */
  | 'synced'
  /** On, waiting for a connection. */
  | 'offline'
  /** On, waiting to renew the sign-in with a quick visit to Microsoft (at a quiet moment, or Reconnect). */
  | 'renew'
  /** On, but Microsoft wants you to sign in again (or Google Drive needs connecting again). */
  | 'signin'
  /** On, but something's wrong that may need you (OneDrive full, permission removed...). */
  | 'error';

export interface SyncStatus {
  /** This build can sync (with OneDrive, Google Drive or both). */
  available: boolean;
  /** What this build can sync with: OneDrive needs a Microsoft app id. */
  providers: Record<SyncProvider, boolean>;
  /** The provider sync is on with, or was last on with. */
  provider: SyncProvider;
  enabled: boolean;
  phase: SyncPhase;
  account: Account | null;
  lastSync: number;
  message: string | null;
  peers: PeerInfo[];
  problems: CycleResult['problems'];
  /** Sync was on here once, so copies of this device's tasks and notes may still be in OneDrive (or Google Drive). */
  hasCopies: boolean;
}

interface SyncRecord {
  enabled: boolean;
  meta: SyncMeta | null;
  /** Absent: OneDrive (as in every record written before Google Drive). */
  provider?: SyncProvider;
  /** Google Drive: the account, once connected; null once the Worker says it's signed out. Never a token. */
  google?: { email: string } | null;
}

const VERSION = typeof __APP_VERSION__ === 'string' ? __APP_VERSION__ : 'dev';
const MINUTE = 60_000;
const HOUR = 60 * MINUTE;
/** Hiccups (a dropped request, a busy server) stay quiet until this many in a row. */
const QUIET_FAILURES = 3;
/** Renew this long before the refresh token's 24 hours are up. */
const RENEW_AHEAD = 4 * HOUR;
/** At most one visit to Microsoft's page for renewal in this long. */
const VISIT_GAP = 6 * HOUR;
/** ...or in this long, if the last one never came back (Tack was opened before it did): well before the sign-in runs out. */
const RETRY_GAP = HOUR;
/** Coming back after this long counts as a quiet moment. */
const AWAY = 5 * MINUTE;
/** Setting off to Microsoft takes seconds: still here after this long, the visit didn't happen. */
const LEAVE_WITHIN = 30_000;
const POLL = MINUTE;
const AFTER_EDIT = 3000;
/** The longest wait a timer is given (Retry-After included): browsers fire longer ones at once. */
const MAX_WAIT = 6 * HOUR;
/** After a hidden frame timed out, the next waits this long, twice as long each time... */
const FRAME_RETRY = 15 * MINUTE;
/**
 * ...and after this many in a row, a visit renews instead, before the sign-in runs out. That isn't
 * a refusal: frames still count as working here, and are tried again once a visit has been made.
 */
const FRAME_TIMEOUTS = 3;

/** Hidden frames that timed out in a row since the last renewal visit (earlier ones are history). */
const frameTimeouts = (a: AuthRecord): number => ((a.frameTimedOut ?? 0) > a.lastRenewVisit ? (a.frameTimeouts ?? 0) : 0);

/** Sync was turned off (perhaps in another tab) while something was under way. */
class TurnedOff extends Error {}

export function authConfig(origin = location.origin): MicrosoftAuth['config'] | null {
  const clientId = import.meta.env.VITE_MS_CLIENT_ID?.trim();
  if (!clientId) return null;
  return {
    clientId,
    authority: (import.meta.env.VITE_MS_AUTHORITY?.trim() || 'https://login.microsoftonline.com/common').replace(/\/$/, ''),
    redirectUri: `${origin}/`,
    silentRedirectUri: `${origin}/auth/`,
    scopes: SCOPES,
  };
}

export class SyncController {
  private record: SyncRecord = { enabled: false, meta: null };
  private auth: AuthRecord = emptyAuth();
  private readonly msal: MicrosoftAuth | null;
  private readonly google: GoogleAuth | null;
  private folder: (SyncFolder & { open(): Promise<string> }) | null = null;
  /** Which provider `folder` is for (another tab may have switched since). */
  private folderFor: SyncProvider | null = null;
  private folderKey: string | null = null;
  private timer: ReturnType<typeof setTimeout> | null = null;
  private running: Promise<void> | null = null;
  private again = false;
  private failures = 0;
  /**
   * No sync before this (ms): the wait after a failure Microsoft or OneDrive answered. Edits,
   * coming back to the app and the last exchange on the way out bring a sync forward only to it.
   */
  private notBefore = 0;
  /** ...and Microsoft's Retry-After (not Tack's own backoff on top), which even "Sync now" waits for. */
  private throttledUntil = 0;
  private hiddenAt = 0;
  private renewing: Promise<void> | null = null;
  private readonly listeners = new Set<(s: SyncStatus) => void>();
  private _status: SyncStatus;
  /** Called when this device first joins a folder your other devices already use. */
  onJoin: (() => void) | null = null;

  private constructor(
    private readonly ws: Workspace,
    private readonly store: Store,
    private readonly backend: Backend,
    config: MicrosoftAuth['config'] | null,
    private readonly http?: typeof fetch,
    google?: GoogleAuth | null,
  ) {
    this.msal = config ? new MicrosoftAuth(config, http) : null;
    // Offered only where the sign-in Worker answers on /api/google/ (VITE_GOOGLE_SYNC=1).
    this.google = google === undefined ? (import.meta.env.VITE_GOOGLE_SYNC?.trim() === '1' ? new GoogleAuth(http) : null) : google;
    this._status = {
      available: !!this.msal || !!this.google,
      providers: { onedrive: !!this.msal, google: !!this.google },
      provider: 'onedrive',
      enabled: false,
      phase: 'off',
      account: null,
      lastSync: 0,
      message: null,
      peers: [],
      problems: [],
      hasCopies: false,
    };
    store.keepDeletions = () => this.record.enabled;
  }

  /** `google`: Google Drive's sign-in (null: not offered here). */
  static async create(ws: Workspace, store: Store, backend: Backend, config = authConfig(), http?: typeof fetch, google?: GoogleAuth | null): Promise<SyncController> {
    const c = new SyncController(ws, store, backend, config, http, google);
    try {
      c.record = (await backend.getMeta<SyncRecord>('sync')) ?? c.record;
      c.auth = { ...emptyAuth(), ...((await backend.getMeta<AuthRecord>('auth')) ?? {}) };
    } catch (error) {
      console.warn('Sync settings unreadable', error);
    }
    if (!c.offers(c.provider)) c.record.enabled = false;
    c.update({});
    return c;
  }

  /** The provider in the record: OneDrive unless it says Google Drive. */
  private get provider(): SyncProvider {
    return this.record.provider === 'google' ? 'google' : 'onedrive';
  }

  /** Can this build sync with `provider`? */
  private offers(provider: SyncProvider): boolean {
    return provider === 'google' ? !!this.google : !!this.msal;
  }

  /** Signed in (OneDrive) or connected (Google Drive): there's something to sync with. */
  private connected(): boolean {
    return this.provider === 'google' ? !!this.record.google : !!this.auth.tokens;
  }

  get status(): SyncStatus {
    return this._status;
  }

  onStatus(listener: (s: SyncStatus) => void): () => void {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  }

  // ------------------------------------------------------------------ boot

  /**
   * Finishes a sign-in (or renewal) if this page load is the return from Microsoft, or from
   * connecting Google Drive. Call before the app shows anything: it takes the code (or ?google=)
   * out of the address bar first.
   */
  async handleReturn(): Promise<'signed-in' | 'signed-in-again' | 'renewed' | 'failed' | null> {
    const google = takeGoogleReturn();
    if (google && this.google) return this.googleReturned(google);
    if (!this.msal) return null;
    const result = await this.msal.completeRedirect();
    if (!result) return null;
    if ('error' in result) {
      if (result.error.kind === 'interaction') {
        // Microsoft wants you on its page: say so, and don't visit again by itself.
        if (this.record.enabled) await this.saveAuth({ needsSignIn: true, visitReturned: true });
      } else if (result.kind === 'sign-in' && result.error.kind !== 'cancelled') {
        console.error('Sign-in failed', result.error);
        this.update({ message: 'Couldn’t sign in with Microsoft. Please try again.' });
      } else {
        // A quiet renewal that failed on the way (network, a Microsoft hiccup, or cut short by
        // opening Tack meanwhile): the refresh token may still work, and it's tried again after
        // the short gap, well before that runs out.
        console.warn('Renewal failed', result.error);
      }
      return result.error.kind === 'cancelled' ? null : 'failed';
    }
    if (result.kind === 'renew') {
      const known = this.auth.account;
      // A renewal must come back as the same account, and only while sync is on (with OneDrive).
      if (!this.record.enabled || this.provider !== 'onedrive') return null;
      if (!known || known.id !== result.account.id || known.tenant !== result.account.tenant) {
        await this.saveAuth({ needsSignIn: true, visitReturned: true });
        return 'failed';
      }
      // The frame was refused but this visit worked: it's the browser, so visit from now on.
      await this.saveAuth({ tokens: result.tokens, needsSignIn: false, frameWorks: this.auth.frameRefused ? false : this.auth.frameWorks, frameRefused: 0, frameTimeouts: 0, visitReturned: true });
      return 'renewed';
    }
    const fromGoogle = this.provider === 'google';
    const googleOn = fromGoogle && this.record.enabled;
    const again = this.record.enabled && !fromGoogle;
    const switched = this.auth.account && (this.auth.account.id !== result.account.id || this.auth.account.tenant !== result.account.tenant);
    // A fresh sign-in: learn again whether frames work here.
    await this.saveAuth({ account: result.account, tokens: result.tokens, needsSignIn: false, frameWorks: null, frameRefused: 0, frameTimeouts: 0 });
    if (switched || fromGoogle) this.folderKey = null;
    if (fromGoogle) {
      // OneDrive in place of Google Drive: the record as OneDrive has always written it.
      const { provider: _, google: __, ...rest } = this.record;
      this.record = { ...rest, enabled: true };
      await this.saveRecord();
      if (googleOn) await this.google?.signOut();
    } else if (!this.record.enabled) {
      this.record = { ...this.record, enabled: true };
      await this.saveRecord();
    }
    return again ? 'signed-in-again' : 'signed-in';
  }

  /** Back from connecting Google Drive (through the Worker): on with Google Drive, or why not. */
  private async googleReturned(back: GoogleReturn): Promise<'signed-in' | 'signed-in-again' | 'failed' | null> {
    if (!back.connected) {
      if (back.reason === 'denied') return null; // you said no on Google's page
      console.error('Connecting Google Drive failed', back.reason);
      this.update({
        message:
          back.reason === 'scope'
            ? 'Google Drive wasn’t connected: Tack needs to see the files it makes there. Connect again, and tick the box for Google Drive on Google’s page.'
            : 'Couldn’t connect Google Drive. Please try again.',
      });
      return 'failed';
    }
    // The Worker must hold a session before anything changes here: a link can say ?google=connected,
    // only a sign-in leaves a session. (Unreachable or busy: let sync find out, as for any later visit.)
    try {
      await this.google!.token(true);
    } catch (error) {
      if (error instanceof SyncError && error.kind === 'auth') {
        this.update({ message: 'Couldn’t connect Google Drive. Please try again.' });
        return 'failed';
      }
    }
    const again = this.record.enabled && this.provider === 'google';
    if (!again) {
      // Google Drive in place of OneDrive (if that was on): Microsoft's sign-in is forgotten, as turning sync off does.
      if (this.auth.tokens || this.auth.account) await this.saveAuth(emptyAuth(), true);
      this.folderKey = null;
    }
    this.record = { ...this.record, enabled: true, provider: 'google', google: { email: this.google!.email || (again && this.record.google?.email) || '' } };
    await this.saveRecord();
    this.update({ message: null });
    return again ? 'signed-in-again' : 'signed-in';
  }

  /** Is a renewal visit due before the app starts (see `renewBeforeStart`)? */
  renewalDueAtStart(): boolean {
    return this.visitDue(Date.now() + RENEW_AHEAD);
  }

  /**
   * If a renewal visit is due, starts it (the page goes to Microsoft and comes straight back)
   * and returns true: the app shouldn't start this time round. Done before anything shows, so
   * nothing you type can be lost to it. False if it isn't due, or Microsoft can't be reached.
   */
  async renewBeforeStart(): Promise<boolean> {
    if (!this.renewalDueAtStart()) return false;
    return this.visit();
  }

  start(): void {
    if (!this.msal && !this.google) return;
    this.ws.subscribe((c) => {
      if (c.origin !== 'remote') this.schedule(AFTER_EDIT);
    });
    document.addEventListener('visibilitychange', () => this.onVisibility());
    window.addEventListener('online', () => this.schedule(0));
    window.addEventListener('offline', () => this.status.enabled && this.update({ phase: 'offline' }));
    // Back from the back/forward cache after setting off to Microsoft: not on the way any more.
    window.addEventListener('pageshow', (e) => e.persisted && this.setLeaving(false));
    this.schedule(1500);
  }

  // ------------------------------------------------------------------ turning it on and off

  /** Goes to Microsoft (or to Google, through the sign-in Worker) to sign in; sync starts when you come back. */
  async turnOn(provider: SyncProvider = 'onedrive'): Promise<void> {
    if (provider === 'google') {
      if (!this.google) return;
      await this.store.flushAll();
      await this.google.connect();
      return;
    }
    if (!this.msal) return;
    await this.store.flushAll();
    await this.msal.signIn({ loginHint: this.auth.account?.username });
  }

  /** After Microsoft asked for a sign-in: the same, keeping the account suggested (Google Drive: connect again). */
  signInAgain(): Promise<void> {
    return this.turnOn(this.provider);
  }

  /** Renews the sign-in now with a quick visit to Microsoft (it comes straight back). */
  async reconnect(): Promise<void> {
    if (this.provider === 'google') return this.turnOn('google');
    if (!this.msal || !this.auth.account) return this.turnOn();
    await this.store.flushAll();
    await this.visit(true);
  }

  /**
   * Stops syncing on this device and forgets the sign-in. Nothing is deleted: the tasks stay
   * here, and the files in OneDrive (or Google Drive) stay for your other devices. With Google
   * Drive, it also signs out at the Worker, which revokes Tack's access at Google.
   */
  async turnOff(): Promise<void> {
    if (this.timer) clearTimeout(this.timer);
    this.timer = null;
    let google = false;
    const off = async () => {
      await this.running;
      await this.renewing?.catch(() => {});
      const stored = (await this.backend.getMeta<SyncRecord>('sync')) ?? this.record;
      google = stored.provider === 'google';
      this.record = google ? { ...stored, enabled: false, google: null } : { ...stored, enabled: false };
      await this.saveRecord();
      await this.saveAuth(emptyAuth(), true);
    };
    // Waits for a sync running in another tab to finish, so it can't write after this.
    const locks = (navigator as Navigator & { locks?: LockManager }).locks;
    if (locks) await locks.request('tack-sync', off);
    else await off();
    if (google) await this.google?.signOut();
    this.folder = null;
    this.folderKey = null;
    this.update({ phase: 'off', message: null, peers: [], problems: [] });
  }

  /**
   * Settings → Take it in anyway, for a device whose file was set aside as more than this device
   * takes in at once ('full'): that version is taken in next, up to what this device's own file
   * may carry (see takeIn in engine.ts). Then syncs.
   */
  async takeIn(device: string): Promise<void> {
    const mark = async () => {
      await this.running;
      const stored = (await this.backend.getMeta<SyncRecord>('sync')) ?? this.record;
      if (!stored.enabled || !stored.meta) return;
      const meta = structuredClone(stored.meta);
      if (!takeIn(meta, device)) return;
      this.record = { ...stored, meta };
      await this.saveRecord();
    };
    // Between cycles (in any tab), so none saves its own copy of the state over this.
    const locks = (navigator as Navigator & { locks?: LockManager }).locks;
    if (locks) await locks.request('tack-sync', mark);
    else await mark();
    this.syncNow();
  }

  /** Syncs now (e.g. from Settings), or as soon as Microsoft allows. */
  syncNow(): void {
    this.failures = 0;
    this.notBefore = this.throttledUntil;
    this.schedule(0);
  }

  // ------------------------------------------------------------------ scheduling

  /** Syncs after `delay`, or once the wait after a failure is over if that's later. */
  private schedule(delay: number): void {
    if (!this.record.enabled || !this.connected()) return;
    if (this.timer) clearTimeout(this.timer);
    const wait = Math.min(MAX_WAIT, Math.max(delay, this.notBefore - Date.now()));
    this.timer = setTimeout(() => {
      this.timer = null;
      void this.run();
    }, wait);
  }

  private onVisibility(): void {
    if (!this.record.enabled) return;
    if (document.visibilityState === 'hidden') {
      this.hiddenAt = Date.now();
      // A last exchange on the way out, so a change made just now reaches your other devices
      // (unless Microsoft asked Tack to wait: the change goes when the app is back).
      void this.store.flushAll().then(() => (Date.now() >= this.notBefore ? this.run() : undefined));
      return;
    }
    const away = this.hiddenAt && Date.now() - this.hiddenAt > AWAY;
    this.hiddenAt = 0;
    if (away && this.quietNow() && this.visitDue(Date.now())) {
      void this.store.flushAll().then(() => {
        // Still quiet once Microsoft is known to be reachable (that takes a moment), or not now.
        if (this.store.allSaved && this.quietNow()) void this.visit(false, () => this.quietNow()).then((going) => going || this.schedule(0));
      });
      return;
    }
    this.schedule(0);
  }

  /** Nothing is being typed or waiting to be saved. */
  private quietNow(): boolean {
    const el = document.activeElement as HTMLElement | null;
    const typing = !!el && (el.isContentEditable || el.tagName === 'INPUT' || el.tagName === 'TEXTAREA');
    return !typing && this.store.allSaved && !document.querySelector('dialog[open]');
  }

  /** On the way to Microsoft to renew: a reload now would cut the renewal short. */
  get leaving(): boolean {
    return this._leaving;
  }

  /** On the way to Microsoft to renew, or due to go (at the next quiet moment). */
  get visiting(): boolean {
    return this._leaving || this.visitDue(Date.now());
  }

  private _leaving = false;
  private leaveTimer: ReturnType<typeof setTimeout> | undefined;

  private setLeaving(on: boolean): void {
    clearTimeout(this.leaveTimer);
    this._leaving = on;
    // Going takes seconds: still here long after, the way there was cut short (cancelled, say).
    if (on) this.leaveTimer = setTimeout(() => (this._leaving = false), LEAVE_WITHIN);
  }

  /** Is a renewal visit to Microsoft due (the token runs out by `deadline` and frames don't work here)? */
  private visitDue(deadline: number): boolean {
    const a = this.auth;
    return (
      !!this.msal &&
      this.record.enabled &&
      this.provider === 'onedrive' &&
      !!a.account &&
      !!a.tokens &&
      !a.needsSignIn &&
      // Frames don't work here, or one was refused (or a few timed out) since the last visit.
      (a.frameWorks === false || a.frameRefused > a.lastRenewVisit || frameTimeouts(a) >= FRAME_TIMEOUTS) &&
      a.tokens.refreshExpires < deadline &&
      Date.now() - a.lastRenewVisit > (a.visitReturned ? VISIT_GAP : RETRY_GAP) &&
      navigator.onLine &&
      !isSignInReturn() &&
      window.top === window
    );
  }

  /**
   * Goes to Microsoft and straight back to renew. Returns false (and stays) if Microsoft can't be
   * reached, or if it can't even record that it's going, so a storage problem can never leave an
   * empty page or a loop.
   */
  private async visit(asked = false, still: () => boolean = () => true): Promise<boolean> {
    this.setLeaving(true);
    // Out of reach (Wi-Fi without internet, a connection still coming up): going anyway would put
    // the browser's own error page where Tack was. Stay, and go another time (nothing recorded).
    if (!(await this.msal!.reachable())) {
      console.warn('Microsoft can’t be reached: the sign-in renews another time');
      this.setLeaving(false);
      if (asked) this.update({ message: 'Couldn’t reach Microsoft just now. Check your connection, then try again.' });
      return false;
    }
    if (!still()) {
      this.setLeaving(false);
      return false;
    }
    try {
      await this.saveAuth({ lastRenewVisit: Date.now(), visitReturned: false });
    } catch (error) {
      console.warn('Can’t renew the sign-in right now', error);
      this.setLeaving(false);
      return false;
    }
    void this.msal!.renewByVisit(this.auth.account!).catch((error) => {
      console.error('Renewal visit failed to start', error);
      this.setLeaving(false);
      if (!asked) location.reload(); // boots normally: the visit is no longer due
    });
    return true;
  }

  private async run(): Promise<void> {
    if (this.running) {
      this.again = true;
      return this.running;
    }
    if (!this.record.enabled || !this.connected()) return;
    this.running = (async () => {
      try {
        const locks = (navigator as Navigator & { locks?: LockManager }).locks;
        if (locks) {
          // Another tab is syncing: it'll do.
          await locks.request('tack-sync', { ifAvailable: true }, async (lock) => {
            if (lock) await this.exchange();
          });
        } else {
          await this.exchange();
        }
      } finally {
        this.running = null;
      }
    })();
    await this.running;
    if (this.again) {
      this.again = false;
      this.schedule(500);
    }
  }

  /** One sync cycle, with the bookkeeping and the retry policy around it. */
  private async exchange(): Promise<void> {
    if (!navigator.onLine) {
      this.update({ phase: 'offline' });
      return;
    }
    // Another tab may have renewed the sign-in, or turned sync off, since this one last looked.
    this.record = (await this.backend.getMeta<SyncRecord>('sync')) ?? this.record;
    this.auth = { ...emptyAuth(), ...((await this.backend.getMeta<AuthRecord>('auth')) ?? this.auth) };
    if (!this.record.enabled || !this.connected()) {
      this.update({});
      return;
    }
    this.update({ phase: 'syncing' });
    let next = POLL;
    try {
      if (this.provider === 'onedrive') void this.renewSoon();
      await this.cycle();
      this.failures = 0;
      this.notBefore = this.throttledUntil = 0;
    } catch (error) {
      next = this.onFailure(error);
      // A failure Microsoft or OneDrive answered (or this device's own): nothing local cuts the
      // wait short. Offline waits for the 'online' event; a spent sign-in for a renewal, perhaps
      // in another tab.
      if (error instanceof SyncError && error.kind !== 'offline' && error.kind !== 'auth') this.notBefore = Date.now() + Math.min(next, MAX_WAIT);
      if (error instanceof SyncError && error.kind === 'throttled') this.throttledUntil = Date.now() + Math.min(error.retryAfter ?? 30_000, MAX_WAIT);
    }
    // Keep going only while on screen; coming back starts it again.
    if (document.visibilityState === 'visible' || next < POLL) this.schedule(next);
  }

  private async cycle(collisionRetried = false): Promise<void> {
    if (this.folderFor !== this.provider) {
      this.folder = null;
      this.folderKey = null;
      this.folderFor = this.provider;
    }
    this.folder ??=
      this.provider === 'google' ? new GoogleDriveFolder((force) => this.googleToken(force), this.http) : new OneDriveFolder((force) => this.accessToken(force));
    this.folderKey ??= await this.folder.open();
    const record = (await this.backend.getMeta<SyncRecord>('sync')) ?? this.record;
    let meta = record.meta && record.meta.folder === this.folderKey ? record.meta : emptyMeta(this.folderKey);
    const engine = new SyncEngine(this.ws, this.folder, {
      label: () => ({ name: deviceName(), app: `Tack Focus ${VERSION}` }),
      flush: async () => {
        // Saved, or still saving what was typed a moment ago; only failed saves stop the cycle.
        await this.store.flush();
        if (this.store.status === 'retrying' || this.store.status === 'unsaved') throw new SyncError('server', 'Tack couldn’t save on this device');
      },
      saveMeta: async (m) => {
        await this.mustBeOn();
        await this.backend.putMeta('sync', { ...this.record, meta: m });
      },
      joined: () => this.onJoin?.(),
    });
    try {
      const result = await engine.cycle(meta);
      await this.mustBeOn();
      this.record = { ...this.record, meta };
      await this.saveRecord();
      this.update({ phase: 'synced', lastSync: meta.lastSync, message: null, peers: result.peers, problems: result.problems });
    } catch (error) {
      if (!(error instanceof DeviceCollision) || collisionRetried) throw error;
      // A copy of this device's data syncs elsewhere under the same id: become a new device.
      const id = newDeviceId();
      this.ws.hlc.renew(id);
      await this.backend.setDevice(id);
      meta = { ...meta, own: { tag: null, written: null, pending: null } };
      this.record = { ...this.record, meta };
      await this.saveRecord();
      await this.cycle(true);
    }
  }

  private onFailure(error: unknown): number {
    this.failures++;
    console.warn('Sync attempt failed', error);
    // 5 s, 10 s, 20 s... up to half an hour: a dropped request is usually fine a moment later.
    const backoff = Math.min(30 * MINUTE, 5000 * 2 ** Math.min(this.failures - 1, 10));
    // A hiccup isn't news: say something only once it keeps happening.
    const quiet = this.failures < QUIET_FAILURES;
    const keepTrying = (message: string) =>
      this.update(quiet ? { phase: 'synced' } : { phase: 'error', message });
    const google = this.provider === 'google';
    const drive = google ? 'Google Drive' : 'OneDrive';
    if (error instanceof TurnedOff) {
      this.update({});
      return 30 * MINUTE;
    }
    if (google && error instanceof SyncError && error.kind === 'auth') {
      // Signed out at the Worker, or the access revoked at Google: connecting again is up to you.
      this.record = { ...this.record, google: null };
      void this.googleSignedOut();
      this.update({ phase: 'signin', message: null });
      return 30 * MINUTE;
    }
    if (error instanceof AuthError || (error instanceof SyncError && error.kind === 'auth')) {
      if (this.auth.needsSignIn) {
        this.update({ phase: 'signin', message: null });
        return 30 * MINUTE;
      }
      // The sign-in ran out and couldn't be renewed in the background: the next quiet moment will.
      this.update({ phase: 'renew', message: null });
      return 10 * MINUTE;
    }
    if (error instanceof SyncError) {
      switch (error.kind) {
        case 'offline':
          this.update({ phase: 'offline' });
          return Math.min(backoff, 5 * MINUTE);
        case 'throttled':
          keepTrying(`${drive} is busy right now. Tack will keep trying.`);
          // As long as Microsoft asks, and longer each time it asks again.
          return Math.max(error.retryAfter ?? 30_000, backoff);
        case 'crowded':
          this.update({
            phase: 'error',
            message: google
              ? 'Tack’s folder in Google Drive holds far more files than Tack keeps there, so sync is paused. Remove the extra ones from My Drive › Tack Focus › devices.'
              : 'Tack’s folder in OneDrive is full of files Tack didn’t put there, so sync is paused. Remove them from Apps › Tack Focus › devices.',
          });
          return 30 * MINUTE;
        case 'full':
          this.update({ phase: 'error', message: `Your ${drive} is full, so Tack can’t save its sync file there.` });
          return 30 * MINUTE;
        case 'denied':
          this.update({ phase: 'error', message: `${drive} refused Tack’s request. Turning sync off and on again may help.` });
          return 30 * MINUTE;
        case 'toolarge':
          this.update({ phase: 'error', message: 'There’s more here than sync can carry, so this device’s changes aren’t being shared.' });
          return 30 * MINUTE;
        case 'gone':
          this.folderKey = null; // the folder was removed: find or make it again
          keepTrying(`Tack can’t reach its folder in ${drive} right now. It will keep trying.`);
          return backoff;
        default:
          keepTrying(`${drive} isn’t answering right now. Tack will keep trying.`);
          return Math.max(error.retryAfter ?? 0, backoff);
      }
    }
    console.error('Sync failed', error);
    keepTrying('Sync ran into a problem. Tack will keep trying.');
    return backoff;
  }

  // ------------------------------------------------------------------ tokens

  /** An access token for Google Drive, from the Worker (kept in memory only, see gauth.ts). */
  private async googleToken(force = false): Promise<string> {
    if (!this.google || !this.record.google) throw new SyncError('auth', 'Google Drive isn’t connected');
    const token = await this.google.token(force);
    // The account's address, for Settings, as the Worker says it.
    const email = this.google.email;
    if (email && this.record.google.email !== email) {
      await this.mustBeOn();
      this.record = { ...this.record, google: { email } };
      await this.saveRecord();
      this.update({});
    }
    return token;
  }

  /** The Worker says Google Drive is signed out (or revoked): noted, so nothing syncs until you connect again. */
  private async googleSignedOut(): Promise<void> {
    try {
      const stored = (await this.backend.getMeta<SyncRecord>('sync')) ?? this.record;
      if (!stored.enabled || stored.provider !== 'google') return;
      this.record = { ...stored, google: null };
      await this.saveRecord();
      this.update({ phase: 'signin', message: null });
    } catch (error) {
      console.warn('Couldn’t note that Google Drive is signed out', error);
    }
  }

  /** An access token for Graph, renewed with the refresh token when needed. */
  private async accessToken(force = false): Promise<string> {
    const t = this.auth.tokens;
    if (!t || !this.msal) throw new SyncError('auth', 'Not signed in');
    if (!force && t.accessExpires - Date.now() > 2 * MINUTE) return t.accessToken;
    let tokens: Tokens;
    try {
      tokens = await this.msal.refresh(t);
    } catch (error) {
      if (error instanceof SyncError && error.kind !== 'auth') throw error;
      // The refresh token is spent: renew quietly if this browser allows it, else wait for a quiet moment.
      await this.renewInFrame();
      if (!this.auth.tokens || this.auth.tokens === t) throw error;
      return this.auth.tokens.accessToken;
    }
    await this.mustBeOn();
    await this.saveAuth({ tokens });
    return tokens.accessToken;
  }

  /** Renews ahead of time (in a hidden frame), so sync never stalls at the 24-hour mark. */
  private async renewSoon(): Promise<void> {
    const t = this.auth.tokens;
    if (!t || t.refreshExpires - Date.now() > RENEW_AHEAD) return;
    await this.renewInFrame().catch(() => {});
  }

  /** Renewal in a hidden frame, unless frames don't work here or one was just refused. */
  private renewInFrame(): Promise<void> {
    this.renewing ??= (async () => {
      const a = this.auth;
      if (!a.account || !this.msal || a.frameWorks === false || a.frameRefused > a.lastRenewVisit) return;
      // After a frame timed out, not again for a while (15 minutes, then 30...): each try loads
      // Microsoft's sign-in page. After a few, a visit renews instead (visitDue).
      const timeouts = frameTimeouts(a);
      if (timeouts >= FRAME_TIMEOUTS) return;
      if (timeouts > 0 && Date.now() - (a.frameTimedOut ?? 0) < FRAME_RETRY * 2 ** (timeouts - 1)) return;
      try {
        const { tokens } = await this.msal.renewInFrame(a.account);
        await this.mustBeOn();
        await this.saveAuth({ tokens, frameWorks: true, frameRefused: 0, frameTimeouts: 0 });
      } catch (error) {
        // Refused by Microsoft: visit at the next quiet moment (which also tells whether it's the
        // browser or the session). A timeout or a failure on the way waits, longer each time, and
        // after a few a visit renews before the sign-in runs out. A timeout is never a refusal:
        // that visit would then stop frames here for good (handleReturn), for one slow evening.
        if (error instanceof AuthError && error.kind === 'interaction') await this.saveAuth({ frameRefused: Date.now(), frameTimeouts: 0 });
        else if (error instanceof AuthError) await this.saveAuth({ frameTimeouts: timeouts + 1, frameTimedOut: Date.now() });
        else throw error;
      }
    })().finally(() => (this.renewing = null));
    return this.renewing;
  }

  // ------------------------------------------------------------------ storage and status

  private async saveRecord(): Promise<void> {
    await this.backend.putMeta('sync', this.record);
  }

  private async saveAuth(patch: Partial<AuthRecord>, replace = false): Promise<void> {
    this.auth = replace ? { ...emptyAuth(), ...patch } : { ...this.auth, ...patch };
    await this.backend.putMeta('auth', this.auth);
    this.update({});
  }

  /** Stops a cycle or a renewal from writing anything once sync was turned off (in any tab). */
  private async mustBeOn(): Promise<void> {
    const stored = await this.backend.getMeta<SyncRecord>('sync');
    if (stored && !stored.enabled) {
      this.record = { ...stored };
      throw new TurnedOff();
    }
  }

  private update(patch: Partial<SyncStatus>): void {
    const provider = this.provider;
    const google = provider === 'google';
    const enabled = this.record.enabled && this.offers(provider);
    const next: SyncStatus = {
      ...this._status,
      ...patch,
      available: !!this.msal || !!this.google,
      provider,
      enabled,
      account: google ? (this.record.google ? { username: this.record.google.email, name: '', tenant: '', id: '' } : null) : this.auth.account,
    };
    if (!enabled) next.phase = 'off';
    else if (google ? !this.record.google : this.auth.needsSignIn) next.phase = 'signin';
    else if (next.phase === 'off') next.phase = 'synced';
    if (!next.lastSync && this.record.meta) next.lastSync = this.record.meta.lastSync;
    next.hasCopies = !!this.record.meta?.own.written;
    this._status = next;
    this.listeners.forEach((l) => l(next));
  }
}

/** "Tack Focus on Android", for the other devices' Settings. */
export function deviceName(ua = navigator.userAgent): string {
  const platform = /iPhone/.test(ua)
    ? 'iPhone'
    : /iPad|Macintosh.*Mobile/.test(ua)
      ? 'iPad'
      : /Android/.test(ua)
        ? 'Android'
        : /CrOS/.test(ua)
          ? 'Chromebook'
          : /Windows/.test(ua)
            ? 'Windows'
            : /Mac OS X/.test(ua)
              ? 'Mac'
              : /Linux/.test(ua)
                ? 'Linux'
                : 'the web';
  return `Tack Focus on ${platform}`;
}
