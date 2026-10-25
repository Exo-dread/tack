# Syncing Tack across devices

Status: **live** since 20 October 2026, in Tack Focus 0.2.0 (tackfocus.app) and Tack for Windows
1.3.0. Tack Focus (`web/`) and Tack for Windows (`src/Tack`) sync through a folder in **your own
OneDrive**, using the Tack project's Microsoft app registration ([ONEDRIVE-SETUP.md](ONEDRIVE-SETUP.md)).
What has been checked with the real services, and what hasn't: [What's verified](#whats-verified).

The goal: write a task on your phone at night, see it on your PC in the morning, with nobody
(Tack included) running a server that holds your data, and without a bill that grows with users.

## How it feels

- **Tack Focus:** Settings → Sync → **Turn on sync**, sign in with Microsoft once. From then on it
  syncs by itself: a few seconds after a change, when the app comes back to the front or back
  online, and every minute while it's open. Settings shows "Up to date · 2 min ago" and your other
  devices. A small dot on the Settings button appears only if sync needs you.
- **Tack for Windows:** Settings → Sync → **Turn on sync**. It finds Tack Focus's folder in OneDrive
  on the PC by itself (or **Choose folder…**, which a localised "Apps" folder needs; see
  [The folder](#the-folder)). No sign-in: the OneDrive app carries the files, and
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
asks for it, and the OneDrive app brings it to the PC like any other folder. Tack for Windows takes
`OneDrive\Apps\Tack Focus` by itself, and only once it has the marker: that is the one folder Tack
Focus can write. The "Apps" part can be localised ("Aplicaciones"); a marked folder anywhere else is
then only suggested ("Found a Tack sync folder at …"), and you pick it with **Choose folder…**,
because a marked folder elsewhere could also be another app's own folder or one someone shared
with you.

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
| `focus`, `tasks`, `sheets` | Live records, exactly as in tack.json's `tackFocus` section. A task or sheet carries `space` (and `clk.space`) only once it has been put in a space. |
| `spaces` | Live spaces (Work, Dev...), only if there are any (on screen since Tack 1.5.0 and Tack Focus 0.4.0). |
| `deleted` | Deleted records as `id → clock of the deletion`, about 45 bytes each, never their text (`spaces` only if a space was deleted). |

Some things are never in a device file (nor in tack.json, an import or an export): images pasted into a
note in Tack for Windows (snippets), what a task dropped onto its orb points to (a path, a link, a saved
email) and the launcher's shortcuts are kept beside its tack.json and stay on that PC. Other devices see
the task's or the sheet's text, exactly as if they weren't there.

With no spaces and nothing put in one, a file is byte for byte what Tack 1.3 and Tack Focus 0.2 write,
and those versions read the new keys as unknown and skip them. A copy of a task from such a version
has no clock for its space, so it never moves the task back.

### One sync cycle

The same in both apps (`web/src/sync/engine.ts`, `src/Tack/Sync/SyncEngine.cs`):

1. List the folder. Read every device file whose content changed since last time (a damaged file,
   one too big to be Tack's, or one from a newer Tack, is reported and set aside; a file that
   fails to download is simply tried again next time; the rest still sync). Each file's entry in
   the sync state notes the `reader` that read it (2 since spaces): a file read by an older reader
   is read once more, changed or not, so what that reader dropped is learned after an upgrade.
2. Check this device's own file is still its own (see "Recovery" below).
3. Merge each changed file into the workspace, field by field by clock. Every changed file is
   merged, even one this device seems to have had through another device's file already: merges
   are idempotent, so that costs nothing, and it means no file can hide another device's changes.
   A file that would take this device past what it may hold is set aside instead and reported as
   "full" (see [Damaged and hostile files](#damaged-and-hostile-files)).
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
  combined, except a new device's untouched welcome tasks and blank sheet, which stay behind (one
  you moved to a space counts as touched).
- **Sync state lost** (sync.json deleted, a different folder key) on a device that kept its id: it
  finds its own file in the folder and takes it back rather than mistaking it for another device's.
  It also takes back what that file says it had seen of the others (no further than the file's
  `written`, as for any file), so an out-of-date device's older text of a note isn't taken for an
  edit made at the same time, and kept as a copy.
- **An older version writing under the same id** (an old Tack Focus tab left open in the same
  browser, or Tack downgraded and upgraded again) writes the device's file without what it doesn't
  know, such as spaces. The sync state notes the `writer` of each write (2 since spaces), and a file
  last written by any other writer is written again in full on the next cycle.

### Damaged and hostile files

Anyone who can write the folder (your Microsoft account, or an app you gave your whole OneDrive)
can edit your tasks, by design: every device in the folder is trusted, until end-to-end
encryption arrives. The aim is that a bad file can't stop sync, swell everyone's files past what
every device reads, or hide a device's changes after it's gone; what remains is at the end of this
list. Both apps apply the same rules (`devicefile.ts`, `engine.ts`, `DeviceFile.cs`,
`SyncEngine.cs`), checked by `web/tests/unit/synchostile.test.ts` and `tests/Tack.Tests/HostileFileTests.cs`:

- **Unreadable text** (invalid UTF-8, or half an emoji spelled `"\ud83d"`, which JSON allows):
  Tack Focus reads it as U+FFFD and never writes it out; Tack for Windows sets such a file aside.
  Either way the rest syncs.
- **Limits** far beyond real use: 20 MB per file, checked from the listing before downloading and
  again while reading (a listing can be out of date, so neither app reads past 20 MB);
  200,000 records (tasks, sheets, spaces and deletions together) per file;
  100,000 characters per task and 1,000,000 per note; clock device ids of at most 32 characters;
  order keys of at most 10,000. A file over a limit is set aside; an over-long record is skipped.
  Space names are the exception: both apps keep every name to 100 characters (UTF-16 units, never
  half an emoji) and one line, control characters made spaces, wherever it comes from, typed,
  imported or read, so a longer one is cut, not skipped, and no device holds a name its peers would
  refuse. A space record whose id is `all` (no app makes one) is kept and synced as it came, but
  neither app treats it as a space: `all` is the view of everything, so what's in it shows in Main.
  The numbers live in `web/src/model/limits.ts` and `src/Tack/Sync/DeviceFile.cs`.
- **Dates** must be ISO 8601 dates of at most 40 characters; anything else counts as missing (an
  empty `completed` means not done, in both apps). An import keeps a date as written only if it is
  one; any other date it understands (a day without a time, say) is written out in full, so a task
  finished in an imported file stays finished on every device.
- **Clocks** more than a year and a day ahead are refused in files and not adopted by clocks.
- **Ids** such as `constructor` or `toString` are ordinary device ids (Tack Focus never reads
  them from what every JavaScript object inherits).
- **"Seen" claims** are believed no further than the claiming file's own `written`, and never
  stop a changed file from being merged. No device writes more than 1,000 (`MAX_SEEN`): before
  writing, devices with a file in the folder are kept first, then the latest claims. Dropping one
  costs little (at worst a note edited on both sides is kept twice). A file with more (only an
  earlier version passing on a crafted file's claims writes one) is still read, records and all,
  but only its claims about this device and about devices with a file in the folder are used; the
  rest are made up. Tack 1.3.1 to 1.6.0 and Tack Focus 0.2.1 to 0.4.2 refuse a file with more than
  1,000, so keeping to the limit also keeps every file readable by them.
- **Each device's own total.** Every file can be within the limits while several together, or one
  on top of all a device already holds, are not. So a device takes in another device's file only
  if what it would then hold stays within these (`limits.ts`, `DeviceFile.cs`):
  - **Records** (tasks, sheets and spaces, deleted ones included, as its own file counts them): a
    file adding up to 2,000 new ones (`MAX_TRICKLE`), as a peer's everyday edits do, is taken as
    long as the device then holds at most 200,000 (`MAX_RECORDS`). A file adding more at once (a
    whole workspace, say) is taken only up to 180,000 (`MAX_TAKEN`). The 20,000 between are room
    for edits, so devices that share a lot keep taking each other's, while a file that adds a lot
    at once can't fill a device.
  - **Spaces** are records like any other here, however many would show. A limit on how many
    show, applied as files come in, could split devices for good: once a file of junk spaces
    brought them to it, a device that then added one space (or renamed Main, or undid a deletion)
    had its whole file set aside by every peer. So the screen is protected instead: each list
    (the switcher, *Move to*, Settings, the desktop's strip and menus) draws 1,000 spaces at a
    time (`MAX_SPACES_LISTED`, `SpaceView.MaxListed`), the one on show always among them, and
    *More spaces* draws as many again.

  A file that adds no records is always taken. A file set aside for this is reported as "full",
  and Settings names it: "Tack Focus on Android holds more than this device can take in, so its
  changes aren't arriving here" (Tack for Windows says "this PC"), with **Take it in anyway**
  beside it. That takes the version set aside in on the next cycle, held only to what this
  device's own file may carry (200,000), not to what it takes in at once; a new version of the
  file is judged afresh. Otherwise it's tried again when it changes, and the rest keeps syncing.
  Imports keep to the same 180,000 (below).
- **Its own file:** a device whose file would be over 20 MB or 200,000 records doesn't write it
  (every device would refuse it) and says so in Settings ("There's more here than sync can
  carry…"), on every cycle until it fits again.
- **The folder listing:** Tack Focus reads at most 10 pages (2,000 entries) of the `devices`
  folder. A folder holding more was filled by something else: sync pauses for 30 minutes at a
  time and Settings asks you to remove what Tack didn't put there, rather than sending hundreds of
  requests to Microsoft on every cycle. Tack for Windows lists the folder on disk, so it isn't
  affected.
- **Imports, pasting and typing** keep to the same limits, so a device never holds anything its
  peers would skip: an import with more than 200,000 records, or one that would take the device
  past 180,000, is refused with a message; an over-long task or note in it is skipped. A paste, or
  *Make task* from a note, takes up to 10,000 lines at once (`MAX_PASTE_LINES`), and typing stops
  at 100,000 characters in a task and 1,000,000 in a note.
- **What remains.** Someone who can write the folder can still fill a device up to what its own
  file can carry, as before: with many files that each add a little, say. That device then takes
  no more in, and once its own file is full its own changes stop reaching your other devices;
  Settings says so in both cases. And since whether a device takes a big file in at once depends
  on what it already holds, while your devices differ by the edits they haven't synced yet, one
  file sized between two devices' totals can be taken by one and set aside by the other, which
  then sets aside the first one's whole file too (it now carries the same records): that device's
  changes stop arriving, even on a device holding little, and removing the file doesn't undo it,
  since the first device's own file passes it on. Settings names the device; **Take it in anyway**
  on the device that set it aside heals it. Likewise, a file of a few notes near the 1,000,000-character
  limit fits within every per-file cap, but once taken in it can push each device's own file past
  20 MB, so they stop writing (Settings says so) until those notes are deleted. All of this takes the
  same access that already lets someone edit or delete your tasks, which only end-to-end
  encryption would change.

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
folder's children (paged, at most 10 pages of 200), and simple uploads (`PUT …:/<id>.json:/content`).
File contents come from OneDrive's pre-signed download links, fetched **without** the token (Graph's
own `/content` answers with a redirect that browsers can't follow across origins with a token).
Every failure is classified (offline, throttled with Retry-After, storage full, permission removed,
folder deleted, folder crowded with files Tack didn't put there, server) and retried with backoff
where that makes sense: from 5 seconds, doubling, up to 30 minutes. The production CSP allows
exactly these hosts: `login.microsoftonline.com`, `login.live.com` (frames only),
`graph.microsoft.com`, and OneDrive's download hosts (`*.microsoftpersonalcontent.com`, `*.1drv.com`,
`*.livefilestore.com`, `*.sharepoint.com`).

Tack Focus is a polite client. 429 and 503 (and 509 from Graph) mean "slow down", wherever they come
from: Graph, the sign-in service's token endpoint (never taken for a spent sign-in) or OneDrive's
download hosts (never taken for an expired link and asked again at once). Tack Focus then waits as
long as `Retry-After` says (30 seconds if it says nothing), and longer each time it's asked again,
but never more than six hours. While that wait, or the backoff after any other failure Microsoft
answered, is running, nothing on the device brings the next sync forward: not an edit, not coming
back to the app or online, not the last exchange as the app is hidden (a lost connection, or a
spent sign-in, waits for the connection or the renewal instead). **Sync now** cuts Tack's own
backoff short, but waits for what Microsoft asked: its `Retry-After` (or the 30 seconds), up to the
same six hours, and no more.

### Tack for Windows → the folder on disk

`src/Tack/Services/FolderSync.cs` and `LocalSyncFolder.cs`. No network code at all:

- Takes `Apps\Tack Focus` under `%OneDrive%` (and `%OneDriveConsumer%`, `%OneDriveCommercial%`)
  if it has the marker, without downloading anything. Any other first-level folder with the marker
  (under a localised "Apps", say) is only suggested, for you to pick with **Choose folder…** (see
  [The folder](#the-folder)). Or a folder you choose (any folder works, e.g. to sync two PCs
  through another sync tool).
- Never reads more than 20 MB of a file, whatever its size looked like when listed.
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
- It's 272 KB minified (71 KB gzipped), half as much again as all of Tack Focus's own JavaScript (48 KB gzipped).

So Tack Focus uses a small, tested implementation of the same protocol (`web/src/sync/msauth.ts`:
authorization code + PKCE, no secret, no library) and this ladder:

1. **Refresh token** (silent), for the first 24 hours.
2. **Hidden frame** (silent), tried four hours before the deadline. Works in Chrome, Edge and on
   Android, where third-party cookies are allowed. A frame that times out (or whose answer fails
   on the way) is not a refusal, and is counted apart from refusals: it's tried again after 15
   minutes, then after 30, and three in a row make the visit below due, before the sign-in runs
   out. Only timeouts since the last visit count, so frames are tried again after any visit; any
   renewal that works clears the count.
3. **A quick visit to Microsoft's page** (`prompt=none`: it either signs you straight back in or
   comes back with an error, never a form), after Microsoft refused the hidden frame, or after
   three frames in a row timed out. Only at a quiet moment: when the app starts, before anything
   shows (so nothing you type can be lost), or when you come back to it after five minutes away; at
   most once every six hours (an hour, if the last visit was cut short). If that visit works where
   Microsoft refused the frame, it's the browser (Safari and iPhone, Firefox, Brave), and Tack
   visits from then on: expect about a second of Microsoft's page, roughly once a day. After every
   full sign-in it tries the frame again. If the visit is slow, Tack shows "Renewing your OneDrive
   sign-in…" instead of a blank page, offers *Open Tack now* after 8 seconds and opens by itself
   after 25; when Microsoft can't be reached (Wi-Fi without internet) it stays put rather than
   landing on the browser's error page.
4. **"Sign in again"** only if Microsoft says you're no longer signed in there (password changed,
   signed out, consent removed, the session expired) or a renewal comes back as a different
   account: a dot on Settings, one tap. A renewal that just failed on the way (no connection) is
   tried again later.

| Where | What you'll see |
|---|---|
| Android (Chrome), Chrome/Edge on a computer | Nothing, as long as you stay signed in to Microsoft in that browser. |
| iPhone/iPad (Safari, Home Screen app), Firefox, Brave | A brief visit to Microsoft when you open Tack, about once a day. No tapping. If it's slow, Tack says so and lets you in. |
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

**With the real services (20 October 2026):**

- Microsoft sign-in with a personal account, consent, and the `Apps/Tack Focus` folder appearing in
  OneDrive (Tack Focus in Chrome, on a preview build).
- Tack for Windows (a test build of this code) syncing through the OneDrive app on a PC: a task
  added in Tack Focus appeared on the desktop at once.

**Not verified yet:**

- Staying signed in past 24 hours on a real phone: the hidden frame (Android) and the visit
  (an installed Home Screen app on a real iPhone, out-of-scope navigation in standalone mode).
- Offline and simultaneous edits between real devices (covered by the automated tests above).
- The OneDrive app's Files On-Demand and pinning over weeks of use.
- Work and school accounts.

## Phases

| Phase | What | Server? |
|---|---|---|
| 1 (done) | Local-first apps, sync-ready model, merge engine, tack.json import/export | none |
| 2 (done) | Device-file sync engine in both apps; OneDrive for Tack Focus; folder sync for Tack for Windows; Settings → Sync | none |
| 3 (live since 20 October 2026) | App registration, Tack Focus 0.2.0 and Tack 1.3.0; real-device checks under way (TESTING.md, "Not verified yet" above) | none |
| 4 | Optional end-to-end encryption; Google Drive; the token Worker only if wanted | optional |

Google Drive later: its app data folder is hidden from Drive for desktop, so Tack for Windows would
need its own sign-in there; and Google gives browser apps no refresh tokens at all, so the
renewal ladder above would be needed hourly rather than daily.
