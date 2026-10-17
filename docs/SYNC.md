# Syncing Tack across devices (design)

Status: **designed; the data model is built in both apps, the transport isn't.** Phase 1 (shipped in
`web/`) is local-only, but its data model and merge engine are the ones described here, implemented
and tested (`web/src/model/*`, `web/tests/unit/merge.test.ts`). Tack for Windows now keeps the same
model (`src/Tack/Sync`, a C# port checked against shared test vectors, see below) and can import a
Tack Focus export. The transport (cloud storage) is the next phase.

The goal: write a task on your phone at night, see it on your Windows PC in the morning, without
anyone (including Tack) running a server that holds your data, and without a bill that grows with
the number of users.

## Principles

1. **Local first.** Each device keeps the full workspace and works offline forever. Sync is a
   background exchange, never a precondition for using Tack.
2. **Your storage.** Data goes from your device to your own OneDrive or Google Drive, directly.
   No Tack server sees data or tokens (in the recommended setup there is no Tack server at all).
3. **No shared file to fight over.** Devices never write the same file. Each device appends its own
   change files; everyone reads everyone's. File-level conflicts, lost updates and "conflicted copy"
   files from sync clients cannot happen.
4. **Merges that always agree.** Records merge field by field with hybrid logical clocks, so devices
   that have seen the same changes reach the same state, in any order, with duplicates.
5. **Opt-in.** Tack for Windows promises "no network access at all"; sync changes that only for
   people who turn it on.

## Why not one JSON file?

The obvious design (both apps read and write `tack.json` in a synced folder) fails in ordinary use:

- **Lost updates.** Phone and PC both edit while one is offline; whichever uploads last overwrites
  the other's changes. Drive has no reliable compare-and-swap; OneDrive's ETags help only online.
- **Sync-client conflicts.** Desktop sync clients resolve simultaneous writes by creating
  "tack (1).json" copies, which the app then ignores.
- **Deletions resurrect.** Without tombstones, a task deleted on one device comes back from the
  other's copy.
- **Whole-file rewrites** on every tick: slow on mobile data and noisy in version history.

## Data model (already in place on the web)

| Concept | Tack for Windows 1.1 | Shared model (Tack Focus, and Tack for Windows since) |
|---|---|---|
| Task id | 12 hex chars | same ids, unchanged |
| Text, created, completed | fields | same fields, plus a **clock per field** |
| Order | array position | **fractional sort key** (`order.ts`); moving a task rewrites only that task |
| Now | `now: true` on one task | a single **Focus register** `{id, clock}` |
| Delete | removed from the list | **tombstone** (`deleted: true` + clock), purged after 90 days |
| Sheets | id, text, updated | same, plus clocks, sort key, tombstone |

Clocks are hybrid logical clocks: `<ms>.<counter>.<device>` strings that compare correctly as
plain text, never go backwards on a device, and move past any clock a device has seen. Ties break
on device id, so every device picks the same winner.

Merge rules (`merge.ts`):

- **Tasks:** per field, later clock wins. Independent edits survive (rename on the phone + tick on
  the PC gives the renamed task, ticked). A later delete beats an earlier edit; Undo is a newer
  "undelete" and wins back.
- **Focus:** one register, later clock wins; devices can't disagree about what's Now.
- **Order:** sort keys; two devices inserting at the same spot both keep their tasks (tie by id).
- **Notes:** a note is prose, so last-writer-wins on the whole text would silently drop a
  paragraph. If both devices changed a sheet since the last version they agreed on, the later text
  stays and the other is kept as a copy beside it. The copy's id is derived from the losing edit,
  so every device creates the same copy and they still converge.

The randomised test in `merge.test.ts` makes three replicas edit, exchange partial states out of
order, and checks they end identical.

## Storage layout

One folder per workspace, in the provider's per-app area:

```
Tack/                              OneDrive: /Apps/Tack   Google Drive: the hidden appDataFolder
  tack-sync.json                   { format, workspaceId, created, encryption? }
  devices/
    <deviceId>/
      device.json                  name ("Raad's iPhone"), app + version, last seen, cursors
      changes/<fromClock>_<toClock>.json   immutable batches of changed records
      snapshot.json                compacted state + "covers": the last clock included per device
```

- **Push.** After local changes settle (a few seconds) and whenever the app is hidden, a device
  writes one new batch with every record whose clock is newer than its last upload. Batches are
  whole records (tasks are tiny), so applying one is just a merge. A batch is written once and never
  edited.
- **Pull.** On launch, on focus, and on a gentle timer while open, a device lists other devices'
  `changes/` newer than its cursor for that device, downloads them, merges, and saves locally.
- **Compaction.** When a device has more than ~50 batches, it writes `snapshot.json` (its merged
  state plus what it covers) and deletes **its own** older batches. Nobody deletes another device's
  files. A new device starts from the newest snapshot and reads only later batches.
- **Deletions.** Tombstones travel like any change. A tombstone is purged locally after 90 days,
  and removed from snapshots only once every device's `device.json` cursor shows it has seen it.
  A device that has been away longer than that rebases: anything it holds that predates the horizon
  and is absent from the snapshot is treated as deleted, while its own newer edits are kept.
- **Clock sanity.** Remote clocks more than a day in the future are clamped, so one device with a
  wrong date can't win every argument.

## Providers

### Recommended first: OneDrive

- Permission `Files.ReadWrite.AppFolder` (+ `offline_access`): Tack can touch only `/Apps/Tack`,
  nothing else in your OneDrive. Personal and work accounts.
- **That app folder is an ordinary, visible OneDrive folder, which the OneDrive client syncs to
  the PC.** Because devices never share files, Tack for Windows can sync **without any sign-in code**:
  it reads and writes `%OneDrive%\Apps\Tack` on disk and the OneDrive client does the transfer.
  (The same works for Dropbox's app folders, or any folder sync tool, later.)
- The web app talks to Microsoft Graph directly from the browser with OAuth 2 + PKCE (hand-written,
  about 200 lines; no Microsoft library, so the CSP stays tight).

### Second: Google Drive

- Scope `drive.appdata`: a hidden per-app folder. Web and desktop OAuth clients in the same Google
  Cloud project should see the same folder (to confirm with a prototype before committing to it).
- It is hidden from Drive for desktop, so Tack for Windows would need its own OAuth (loopback
  redirect + PKCE, tokens protected with DPAPI). More work than OneDrive's folder route.

### Sign-in without a server, and its one cost

A static site can't keep a client secret, so the browser gets short sessions:

- Microsoft issues browser apps refresh tokens that last **24 hours**; Google issues **1-hour** access
  tokens and no refresh tokens. Renewing silently relies on third-party cookies, which Safari blocks.
- So on iPhone, Tack would ask you to **tap "Reconnect"** about once a day when there's something to
  sync (usually no password: you're still signed in to Microsoft or Google). Your data keeps working
  offline in the meantime; changes queue up.
- Tack for Windows has none of this (OneDrive folder route, or long-lived desktop tokens).

**Optional, needs your decision:** a tiny Cloudflare Worker that only swaps codes for long-lived
refresh tokens (it holds the client secret, never sees data). That removes the daily tap. Cost:
roughly 20 to 30 requests per active user per day, well inside the free tier's 100,000 per day
(about 3,000 daily users), then $5/month per 10 million. It is a server you operate, though, and
it handles tokens, so it raises the security bar.

## Privacy and security

- Least-privilege scopes (app folder only). Tack never sees your other files.
- Tokens: kept in memory or IndexedDB on the device; never in URLs or localStorage. No third-party
  scripts are ever loaded (sign-in is implemented directly), so the existing strict CSP only gains
  the provider API hosts in `connect-src`.
- **Optional end-to-end encryption** (later phase): a passphrase-derived key (PBKDF2 or Argon2,
  then AES-GCM via WebCrypto) encrypts every batch, so even Microsoft or Google can't read your
  tasks. The catch: forget the passphrase and the cloud copy is unreadable (local copies are fine).
- Signing out deletes tokens and stops sync; it never deletes local data. "Remove Tack's data from
  OneDrive" is a separate, explicit action.

## Changes to Tack for Windows

1. **Done.** Keep `tack.json`; add the clocks, sort keys and tombstones in the same `tackFocus`
   section the web export writes (plus `device`, the desktop's clock id). The top level is unchanged
   and stays what you see: if it and the section disagree (a hand edit, or a file saved by Tack 1.1,
   which drops the section), the top level wins as a fresh edit. A 1.1 file gets clocks from its own
   dates and keys in its own order, the very records Tack Focus derives when it imports that file,
   so the two agree without either side changing anything.
2. **Done.** Every mutation in `Workspace` is stamped, step for step as `workspace.ts` stamps it
   (the order of clock calls included), so the same edits give byte-identical records. Deletions are
   tombstones, forgotten after 90 days.
3. A `SyncService`: when sync is on, write batches to the sync folder after changes settle; watch
   the folder (`FileSystemWatcher`) for other devices' batches; merge.
4. Settings: "Sync folder" (detects `OneDrive\Apps\Tack`), status, last synced.
5. **Done.** `hlc.ts`, `order.ts`, `ids.ts`, `merge.ts` and the import side of `file.ts` are ported
   to C# (`src/Tack/Sync`). `web/tests/unit/vectors.test.ts` writes `tests/vectors/sync.json` from
   the TypeScript (stamps, clock scripts, keys, ids, JavaScript's white space and `JSON.stringify`,
   dates, randomised merges, op-by-op workspace scripts with merges, file imports), and
   `tests/Tack.Tests` replays it in C#. A Tack Focus export also goes through the desktop and back
   (`tests/vectors/desktop-export.json`, read by the web tests) without changing a record.
   Settings → Import merges a Tack Focus export (or any tack.json) into the desktop, with Undo.

## Phases

| Phase | What | Server? |
|---|---|---|
| 1 (done) | Local-first PWA, sync-ready model, merge engine + tests, tack.json import/export | none |
| 2 | Desktop model (done); sync engine (batches, cursors, compaction, conflict copies) behind a provider interface, tested against an in-memory provider; folder sync | none |
| 3 | OneDrive in the web app (PKCE, Graph); Settings → Sync; status and Reconnect | none |
| 4 | Google Drive (web + desktop); optional end-to-end encryption | none (optional token Worker) |

## Decisions for you

1. **OneDrive first?** (Recommended: the desktop side needs no sign-in at all.)
2. **Accept a daily "Reconnect" tap on iPhone**, or approve the optional token Worker?
3. **App registrations.** Someone must own the Microsoft Entra app registration (and, to avoid an
   "unverified publisher" warning, publisher verification) and, later, the Google Cloud project and
   its OAuth consent screen. These should belong to the Tack project, not a personal account.
4. **End-to-end encryption:** now, later, or never?
5. **Tack for Windows' "no network" promise** becomes "no network unless you turn on sync" (and,
   with the folder route, Tack itself still makes no network requests; OneDrive does).
