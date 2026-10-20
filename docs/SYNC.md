# Syncing Tack across devices

Status: **built and tested, not yet live.** Tack Focus (`web/`) and Tack for Windows (`src/Tack`)
sync through a folder in **your own OneDrive**. Nothing is deployed or released: it needs a
Microsoft app registration owned by the Tack project first ([ONEDRIVE-SETUP.md](ONEDRIVE-SETUP.md)),
and it hasn't yet run against the real Microsoft services (see [What's verified](#whats-verified)).

The goal: write a task on your phone at night, see it on your PC in the morning, with nobody
(Tack included) running a server that holds your data, and without a bill that grows with users.

## How it feels

- **Tack Focus:** Settings → Sync → **Turn on sync**, sign in with Microsoft once. From then on it
  syncs by itself: a few seconds after a change, when the app comes back to the front or back
  online, and every minute while it's open. Settings shows "Up to date · 2 min ago" and your other
  devices. A small dot on the Settings button appears only if sync needs you.
- **Tack for Windows:** Settings → Sync → **Turn on sync**. It finds Tack Focus's folder in OneDrive
  on the PC by itself (or **Choose folder…**). No sign-in: the OneDrive app carries the files, and
  **Tack itself still never connects to anything**.
- **Offline is normal.** Both apps work fully without a connection; changes wait and sync later.
  Sync is off unless you turn it on, and turning it off keeps everything (on the device, and in
  OneDrive for your other devices).

## Principles

1. **Local first.** Each device keeps the whole workspace and works offline forever. Sync is a
   background exchange, never a precondition.
2. **Your storage.** Data goes from your device to your own OneDrive, directly. No Tack server.
3. **No shared file to fight over.** Each device writes only its own file; everyone reads
   everyone's. Lost updates and "conflicted copies" can't happen to anything that matters.
4. **Merges that always agree.** Records merge field by field by clock, so devices that have read
   the same files end up identical, in any order, however late a file arrives.
5. **Nothing deleted comes back, nothing written is lost**, even after months offline.

## The folder

```
OneDrive/Apps/Tack Focus/          the app folder: the only place Tack Focus can reach
  tack-sync.json                   marker: how Tack for Windows recognises the folder
  README.txt                       what this folder is, for anyone who opens it
  devices/
    <device id>.json               one per device, written only by that device
```

The folder's name is the app registration's name. Microsoft creates it the first time Tack Focus
asks for it, and the OneDrive app brings it to the PC like any other folder. Its name can be
localised ("Apps" may be "Aplicaciones"), so Tack for Windows looks for the marker, not the name.

### A device file

Everything one device knows, written as one file, replaced whole (`web/src/sync/devicefile.ts`,
`src/Tack/Sync/DeviceFile.cs`):

| Field | |
|---|---|
| `format`, `version` | `tack-sync-device`, 1. A newer, incompatible Tack would raise the version; older Tacks then say "update Tack" instead of misreading it. |
| `device` | The writer's id: the same id its clock puts in every stamp. Must match the file name. |
| `name`, `app` | For people: "Tack Focus on Android", "Tack for Windows on DESKTOP-1". |
| `written` | The writer's clock when it wrote the file. |
| `seen` | For each other device, the latest version of its file merged into this one. |
| `focus`, `tasks`, `sheets` | Live records, exactly as in tack.json's `tackFocus` section. |
| `deleted` | Deleted records as `id → clock of the deletion`, about 45 bytes each, never their text. |

### One sync cycle

The same in both apps (`web/src/sync/engine.ts`, `src/Tack/Sync/SyncEngine.cs`):

1. List the folder. Read every device file whose content changed since last time (a damaged file,
   or one from a newer Tack, is reported and skipped; the rest still sync).
2. Check this device's own file is still its own (see "Recovery" below).
3. Merge each new file into the workspace, field by field by clock.
4. Write this device's file if it now knows anything its file doesn't say: its own edits, or news
   merged from others (so files keep up with what their devices know).

Every step can be interrupted (no connection, app closed, PC asleep) and simply runs again: merges
are idempotent, files are written whole, and the cycle's bookkeeping is saved only once it
completes. Before writing, a device notes what it's about to write, so if the answer to an upload
never comes back, it recognises the file as its own next time.

### Conflicts

- **Tasks:** per field, the later clock wins, so independent edits both survive (renamed on the
  phone + ticked on the PC = renamed and ticked). A delete beats an earlier edit; Undo is newer and
  wins back. Clocks are hybrid logical clocks: a device that has seen a change never makes an older
  one, so "later" is meaningful even with clocks that are hours apart.
- **Focus:** one register, later wins. A new device's tutorial focus ("Tick this circle…") is
  stamped with the earliest clock there is, so it never replaces the focus you set elsewhere.
- **Order:** fractional sort keys; two devices inserting at the same spot both keep their tasks.
- **Notes:** if two devices changed the same sheet without either having seen the other's text,
  the later text stays and the other is kept as a copy beside it (the same copy on every device).
  "Seen" is exact: each file's `seen` list says which versions of every other device's file it
  had merged, and every clock names the device that made it. A note edited in turn, even when the
  edit travelled through a third device first, is not copied.

### Deletions are kept for good while syncing

A deleted task stays as a tombstone (`id` + clock of the deletion) for as long as sync is on.
Without that, a device that was offline for months, or a file the OneDrive app uploads late, could
bring back a task you deleted long ago. An earlier design forgot deletions after 90 days once every
device had acknowledged them, with rules for devices that went quiet; a randomised simulation
(below) found sequences of ordinary events (a PC whose OneDrive app stalls for weeks, a stale file
arriving later) where it lost or revived tasks, so the simple rule won. The cost is small: about
45 bytes per deletion in a device file, never the text (a heavy user deleting 20 tasks a day adds
about 330 KB a year). If that ever matters, old deletions can move to a second, rarely-written file.
Without sync, deletions are still forgotten after 90 days, as before.

### Recovery

- **A damaged file** (a partial download, a hand edit) is skipped and reported; its device writes
  it again the next time it syncs (it notices its own file isn't what it wrote).
- **The folder deleted** (by you, in OneDrive): every device writes its file again.
- **A data folder copied to another PC** (two devices with the same id): the first to notice takes
  a new id; both devices' data survive and merge.
- **Joining** a folder your other devices use: what's on the device and what's in the folder are
  combined, except a new device's untouched welcome tasks and blank sheet, which stay behind.

## OneDrive

### Permissions: the narrowest that work

| Scope | Why |
|---|---|
| `Files.ReadWrite.AppFolder` | Read and write Tack's own folder in your OneDrive, **and nothing else**. Delegated, no admin consent. |
| `offline_access` | Stay signed in (a refresh token) instead of asking every hour. |
| `openid`, `profile` | Know which account is signed in, to show it in Settings and to renew the right one quietly. |

`Files.ReadWrite.AppFolder` works for personal Microsoft accounts. Microsoft documents app folders
for work and school OneDrive too, but its permissions reference still marks the delegated scope as
preview; organisations can also block consent to apps from unverified publishers. Treat work
accounts as best effort until tried.

### Tack Focus (browser) → Microsoft Graph

`web/src/sync/onedrive.ts`: `GET /me/drive/special/approot` (creates the folder), the `devices`
folder's children (paged), and simple uploads (`PUT …:/<id>.json:/content`). File contents come
from OneDrive's pre-signed download links, fetched **without** the token (Graph's own `/content`
answers with a redirect that browsers can't follow across origins with a token). Every failure is
classified (offline, throttled with Retry-After, storage full, permission removed, folder deleted,
server) and retried with backoff where that makes sense. The production CSP allows exactly these
hosts: `login.microsoftonline.com`, `login.live.com` (frames only), `graph.microsoft.com`, and
OneDrive's download hosts (`*.microsoftpersonalcontent.com`, `*.1drv.com`, `*.livefilestore.com`,
`*.sharepoint.com`).

### Tack for Windows → the folder on disk

`src/Tack/Services/FolderSync.cs` and `LocalSyncFolder.cs`. No network code at all:

- Finds the folder by its marker under `%OneDrive%` (and `%OneDriveConsumer%`,
  `%OneDriveCommercial%`), "Apps" first, without downloading anything; or a folder you choose
  (any folder works, e.g. to sync two PCs through another sync tool).
- Writes its file to a `~$…tmp` file first (the OneDrive app never uploads those) and swaps it in
  in one step, so nobody ever reads half a file; reads share access so it never blocks OneDrive.
- Asks OneDrive to keep the folder on the PC ("Always keep on this device") so the other devices'
  files are there offline too.
- Reacts to the folder changing (a file system watcher), to your edits, to the panel opening and
  to the PC waking up. **No polling**: nothing runs while idle, as before.
- If a file can't be read right now (the OneDrive app is writing it, or it's cloud-only and you're
  offline), it waits and tries again, quietly unless it keeps happening.
- Tack sees the folder, not the cloud: if the OneDrive app isn't running, Tack is still "in step
  with the sync folder" while nothing reaches your other devices. Settings says so, and suggests
  checking the OneDrive app if changes don't arrive.

## Staying signed in (and why not MSAL)

The aim: sign in once, never a daily "reconnect" unless it's genuinely unavoidable. MSAL with silent
token renewal was investigated first.

**The limit is Microsoft's, not the library's.** Browser apps (registered as single-page apps, the
only kind that can redeem tokens from a browser) get refresh tokens that last **24 hours from
sign-in, and renewing them doesn't extend that**: each new refresh token inherits the first one's
expiry ([Microsoft: refresh tokens](https://learn.microsoft.com/en-us/entra/identity-platform/refresh-tokens)).

After that, the app needs a new sign-in code. Microsoft's guidance is that this normally needs no
typing (you see at most a reload) **as long as you're still signed in to Microsoft in that
browser**, through either a hidden frame (only where third-party cookies are allowed) or a visit
to the sign-in page in the top-level window, which it suggests doing early, before the app has
loaded ([third-party cookies and SPAs](https://learn.microsoft.com/en-us/entra/identity-platform/reference-third-party-cookies-spas)).

**MSAL.js does the same things and can't do more.** MSAL Browser 5.25 renews with the refresh
token, then a hidden frame, then asks you to call a redirect or popup: the same ladder, under the same
24-hour rule. Two things count against it here:

- Its persistent cache (`localStorage`) is encrypted with a key kept in a **session cookie**, so
  unless you tick "Stay signed in" at Microsoft, its tokens are discarded when the browser (or an
  installed app, on phones) closes ([MSAL caching](https://github.com/AzureAD/microsoft-authentication-library-for-js/blob/dev/lib/msal-browser/docs/caching.md)),
  more sign-ins rather than fewer.
- It's 272 KB minified (71 KB gzipped), nearly twice all of Tack Focus's own JavaScript (39 KB gzipped).

So Tack Focus uses a small, tested implementation of the same protocol (`web/src/sync/msauth.ts`:
authorization code + PKCE, no secret, no library) and this ladder:

1. **Refresh token** (silent), for the first 24 hours.
2. **Hidden frame** (silent), tried four hours before the deadline. Works in Chrome, Edge and on
   Android, where third-party cookies are allowed. A frame that merely times out is tried again
   later.
3. **A quick visit to Microsoft's page** (`prompt=none`: it either signs you straight back in or
   comes back with an error, never a form), after Microsoft refused the hidden frame. Only at a quiet
   moment: when the app starts, before anything shows (so nothing you type can be lost), or when you
   come back to it after five minutes away; at most once every six hours. If that visit works where
   the frame didn't, it's the browser (Safari and iPhone, Firefox, Brave), and Tack visits from then
   on: expect about a second of Microsoft's page, roughly once a day. After every full sign-in it
   tries the frame again.
4. **"Sign in again"** only if Microsoft says you're no longer signed in there (password changed,
   signed out, consent removed, the session expired) or a renewal comes back as a different
   account: a dot on Settings, one tap. A renewal that just failed on the way (no connection) is
   tried again later.

| Where | What you'll see |
|---|---|
| Android (Chrome), Chrome/Edge on a computer | Nothing, as long as you stay signed in to Microsoft in that browser. |
| iPhone/iPad (Safari, Home Screen app), Firefox, Brave | A brief visit to Microsoft when you open Tack, about once a day. No tapping. |
| Anywhere, after you sign out of Microsoft or change your password | One "Sign in again". |
| Tack for Windows | Nothing ever: it doesn't sign in at all. |

How long Microsoft keeps you signed in in the browser is up to Microsoft (and your organisation,
for work accounts); Safari may also clear Microsoft's cookies for sites you haven't interacted with
in 30 days, so an occasional "Sign in again" on iPhone is possible.

**The one way to remove even the daily visit** is a tiny server: a Cloudflare Worker that holds a
client secret and only exchanges codes for tokens, which turns the 24-hour tokens into 90-day,
self-renewing ones. It never sees your tasks, but it does see your sign-in tokens in transit,
costs nothing at Tack's scale (about 20 requests per user per day; the free tier covers
100,000 a day), and is a server the project must run and secure. It is **not built**; it's a
decision for later if the daily visit proves annoying.

Tokens are kept in this site's storage on the device that signed in (IndexedDB; localStorage only
in browsers that refuse IndexedDB) and go only to Microsoft (refresh tokens to the sign-in service,
access tokens to Graph). A sign-in in progress keeps its one-time PKCE verifier in localStorage
under its own state, removed when it completes or, if abandoned, at the next start after ten
minutes. The CSP allows no third-party scripts. Its `connect-src` names OneDrive's download hosts
by wildcard (`*.sharepoint.com` and the like), because their exact names vary by account; with
no third-party or inline script allowed, that breadth isn't reachable by an attacker's code.
Turning sync off waits for a sync running in any tab to finish, and nothing writes a token or the
sync state after it.

## Privacy

- **Where your data goes.** With sync on, each device saves a copy of your tasks and notes as a
  file in your OneDrive's "Apps › Tack Focus" folder. No Tack server is involved.
- **Who can read it.** You. Microsoft, as your storage provider, under its own terms. Any other app
  you've allowed to access your *whole* OneDrive. Anyone who gets into your Microsoft account.
- **What else is in the files.** Each device's name ("Tack for Windows on DESKTOP-1" includes the
  PC's name) and the Tack version. OneDrive's version history and recycle bin keep earlier copies
  of the files, which can include the text of tasks you deleted later.
- **Not end-to-end encrypted yet.** The files are plain JSON. Optional encryption with a passphrase
  only you know (AES-GCM, key derived from the passphrase; lose it and the cloud copy is unreadable,
  local copies are fine) is planned for a later release.
- **What Tack can reach.** Tack Focus: only its own folder, plus your name and sign-in address.
  Tack for Windows: only the folder on disk.
- **Deletions** are kept as an id and a time, never the text.
- **Stopping.** Turn off sync on each device (tasks and notes stay). Then delete the
  "Apps › Tack Focus" folder (a device still syncing would write it again) and empty OneDrive's
  recycle bin to remove the files. Remove Tack's access at
  [account.live.com/consent/Manage](https://account.live.com/consent/Manage) (personal) or
  [myapps.microsoft.com](https://myapps.microsoft.com) (work or school).

The same, in plain words, is at https://tackfocus.app/privacy/ (`web/public/privacy/`).

## What's verified

**Automated, and passing:**

- **Engine:** scenario tests (offline work on both sides, simultaneous edits, notes in turn and at
  once, through a third device, delete vs edit, Undo, lagging PCs, clocks hours apart, lost
  answers, failed reads and writes, damaged files, a deleted folder, a copied data folder, newer
  file versions, long absences, stale late uploads, abandoned devices) and a **randomised
  simulation**: up to five devices, months of random use (a year and a half in the long runs), a flaky network,
  PCs whose OneDrive app lags, devices left in a drawer for months, damaged files, the folder
  wiped, a cloned device. Afterwards every replica must be identical, nothing created may be lost
  unless deleted, and nothing deleted may come back unless undone. 40 runs on every `npm test`;
  400 longer runs (1,500 steps each) passed during development
  (`SIM_SEEDS=400 SIM_STEPS=1500 npx vitest run tests/unit/syncsim.test.ts`).
- **Both apps agree:** Tack Focus records every cycle of a three-device scenario
  (`tests/vectors/sync-engine.json`); Tack for Windows replays each one and must end with the same
  records, sync state and device file. Each app reads the other's files record for record.
- **Sign-in and OneDrive:** against a faithful fake of Microsoft (`web/tests/fakes/microsoft.ts`:
  PKCE checks, 24-hour non-extending refresh tokens, Graph paging and errors, download links that
  refuse tokens, CORS), in unit tests and in **browser tests on WebKit (iPhone) and Chromium
  (Android, desktop)**: turning on, two devices exchanging, offline and back, a dropped upload,
  renewal in a hidden frame, renewal by a visit (and never twice in a row), "Sign in again",
  turning off, and the production CSP.
- **Browser and Windows together:** the browser syncs through the fake OneDrive, its file goes to
  a folder on disk, Tack for Windows' own sync code merges it, adds a task and writes back, and the
  browser shows it (Windows only).
- **Tack for Windows' Settings** were checked on screen in a separate test copy: turning on finds
  the OneDrive folder, merges the phone's tasks, keeps the phone's focus, and leaves the PC's
  welcome tasks behind.

**Not verified yet (needs the real thing):**

- Real Microsoft sign-in and OneDrive (needs the app registration).
- Real browsers' cookie behaviour for the hidden frame and the visit, especially an installed
  Home Screen app on a real iPhone (out-of-scope navigation in standalone mode).
- The OneDrive app on a PC: Files On-Demand, pinning, upload timing.
- Work and school accounts.

## Phases

| Phase | What | Server? |
|---|---|---|
| 1 (done) | Local-first apps, sync-ready model, merge engine, tack.json import/export | none |
| 2 (built, in review) | Device-file sync engine in both apps; OneDrive for Tack Focus; folder sync for Tack for Windows; Settings → Sync | none |
| 3 | Go live: app registration, then real-device checks (TESTING.md) | none |
| 4 | Optional end-to-end encryption; Google Drive; the token Worker only if wanted | optional |

Google Drive later: its app data folder is hidden from Drive for desktop, so Tack for Windows would
need its own sign-in there; and Google gives browser apps no refresh tokens at all, so the
renewal ladder above would be needed hourly rather than daily.
