# Testing Tack Focus

## Automated

```bash
npm test        # Vitest: model, clocks, order keys, merging, tack.json, sync, shared vectors
npm run vectors # rewrite tests/vectors/sync.json and sync-engine.json (Tack for Windows is checked against them)
npm run e2e     # Playwright: the production build on three emulated devices (WebKit and Chromium)
```

If port 4173 is taken (another project's preview server), `E2E_PORT=4183 npm run e2e`.

**Sync** (`tests/unit/sync*.test.ts`, `onedrive.test.ts`, `msauth.test.ts`, `tests/e2e/sync.spec.ts`,
`interop.spec.ts`): scenario tests and a randomised months-long simulation of several devices with
a flaky network, lagging PCs, long absences, damaged files and a wiped folder (everyone must agree,
nothing lost, nothing deleted revived; `SIM_SEEDS=400 SIM_STEPS=1500` for a longer soak); damaged
and hostile files (`synchostile.test.ts`: the limits; a file claiming to have seen too many
devices, still read with only the claims that matter, and "seen" trimmed to the limit when
written; each device's own total, with files that would fill a device set aside as "full", a
peer's everyday edits still taken, and only spaces that would show counted; imports, pastes and
edits within the same limits; imported dates every device reads alike); the
OneDrive client and Microsoft sign-in against `tests/fakes/microsoft.ts`, a stand-in that enforces
what the real services do (PKCE, 24-hour refresh tokens that don't extend, Graph paging, errors with
Retry-After, download links that refuse tokens, CORS), including waiting out Retry-After whatever
asks for a sync (and *Sync now* waiting no longer than that), downloads stopped at 20 MB, the
folder listing's page limit, and a hidden frame that keeps timing out (a visit takes over, then
frames again); browser
tests of the whole flow in WebKit and Chromium (two devices, offline, a dropped upload, quiet renewal in a hidden frame or by a visit,
what shows while that visit is slow or stuck on the way there or back and the ways out of it,
staying put when Microsoft can't be reached, "Sign in again", turning off, the production CSP);
and, on Windows, Tack Focus in a browser exchanging tasks with Tack for Windows' own sync code
through a folder on disk. The sync tests run
with the service worker blocked: in WebKit, Playwright can't intercept requests from a page a
worker controls, and they must never reach the real Microsoft.

**Unit tests (369, including 40 runs of the sync simulation)** cover the parts where a bug would lose or scramble data: sort keys under 3,000
random inserts, clock monotonicity and skew, .NET date parsing, every workspace mutation and its
undo, snapshot restore, two- and three-device merges with a randomised convergence test, concurrent
note edits (conflict copies), and tack.json import/export against a file shaped exactly like
Tack for Windows writes it (BOM, comments, trailing commas, seven-digit fractions), including
re-importing after edits on the desktop, on the phone, or both (newer wins; notes kept twice), and
storage (in an in-memory IndexedDB) merging every write so a stale tab can't undo a newer edit,
and spaces: what each space shows (filtering in the one order, counts, a space that's gone showing
Main), reordering inside a space without re-keying the others, adding into a space, and reordering
spaces; and search (`search.test.ts`): what matches, case and accents folded, and where a match is
in the text as typed, from `../tests/shared/search.json`, cases Tack for Windows' tests read too;
every space searched in the lists' own order; where opening a result goes; and a speed guard (10,000
tasks and 500 long notes).
`vectors.test.ts` computes the shared test vectors that Tack for Windows' port of the model must
reproduce exactly (`tests/Tack.Tests`), and reads back the file the desktop saved for its round-trip
export to check nothing was lost on the way.

**End-to-end tests (321 across three projects, 24 skipped where they don't apply)** run against `vite build` + `vite preview`, so the real
service worker and bundle are tested:

| Project | Engine | Emulates |
|---|---|---|
| `iphone-webkit` | WebKit (Safari's engine) | iPhone 15: touch, 393×852, mobile UA |
| `android-chromium` | Chromium | Pixel 7: touch, 412×915 |
| `desktop-chromium` | Chromium | 1280×820, mouse and keyboard |

They cover: first run; capture and persistence across reloads (including a half-typed draft);
return on an empty box; Ctrl+Enter; pasting lists; ticking with the grace period and un-ticking;
a tick surviving the app being closed at once; editing, and deleting by clearing; the edit tools
(focus, move); swipes (focus, delete + undo, a short swipe springing back); long-press and mouse
drag reordering, persisted; keyboard navigation and shortcuts; reduced motion; notes (list
continuation, ending a list, Make task, persistence, deleting a sheet with undo, tidying a blank
sheet); two tabs staying in step; importing a desktop file and undoing it; exporting; working
offline after the first visit (Chromium); running clean under the production CSP; axe
accessibility scans (WCAG 2.2 AA rules) of Tasks, Notes and Settings in light and dark; a note edited
in another window reaching this one mid-writing; an emptied edit surviving the app being hidden;
Undo after deleting from an emptied edit; a tick landing during a swipe; the one-time install
suggestion; a toast that waits while you hold it; a speed guard (500 pasted tasks); and spaces:
nothing new on screen with Main alone, making and switching spaces (the title's switcher, Alt+0…9
while typing), adding into the space on show, moving tasks and sheets with Undo, All with each
task's space, the "Now in …" line, Done and Clear per space, notes per space (blank paper until you
write, and Undo there making no sheet), the sheet strip left as it always was with Main alone,
deleting a space with Undo, renaming and reordering, the space on show surviving a reload,
and axe scans with spaces on screen in light and dark; search (Ctrl+F, or the button beside Settings
on a phone): nothing new on screen until it's used, tasks, done and notes found whatever the case or
accents, grouped, each with its space, arrows and Enter going to a task in another space (scrolled to,
lit up, focused), a finished task opening Done, a note opening with the match selected and in sight,
Esc putting the focus back, the 360 px phone header keeping room for a space's name, reduced motion,
axe scans in light and dark, and a speed guard over 10,000 tasks and 500 long notes
(`search-scale.spec.ts`, without traces); and updates of the app, against a server of
the test's own that puts up a new version (visibility emulated: headless pages never hide): in at
once when opened untouched, even if it took over while the app was still starting; held while you
are using the app or are in the task box, then applied when you leave or come back; a draft in the
box coming back with the page; never while the draft isn't the one kept; held while a share that
just opened the app still offers Undo, then applied when you leave, the share kept once. And
sharing from another app (`share.spec.ts`, loading /?share&… as Android's share sheet does, since
Playwright can't open one): the manifest's share target; a shared page becoming one task with
Undo, on Tasks, and the address bar left clean; added once, whatever reloads follow; several lines
as a task per line; markup and `javascript:` shared as plain text (under the production CSP too);
into the space on show; more lines than a paste turned away; a share kept through a start that
went no further; and offline, answered by the service worker (Chromium).

**Sharing** is also unit-tested (`share.test.ts`: reading the address, what a share becomes, adding
it) with hostile cases in `synchostile.test.ts` (the paste's line limit, no task too long for sync,
markup kept as text that every device reads back unchanged, a kept share that isn't three strings).

Gestures are driven with synthetic pointer events (Playwright can only tap), so they test the
gesture logic, not a real finger. WebKit on Windows is close to Safari's engine but isn't iOS:
no on-screen keyboard, no home-screen mode, no service workers in Playwright's Windows build.

## By hand, on real devices (before a release that touches them)

Emulation can't cover these. Each takes a minute.

### iPhone (Safari, then Home Screen)
- [ ] Open tackfocus.app in Safari; Share → **Add to Home Screen**; the icon and name look right.
      (Tack Focus isn't in the iPhone's share sheet: iOS doesn't let web apps receive shares.)
- [ ] Launch from the icon: full screen, no Safari bars; the **status bar text is legible** in both
      light and dark (it should take the app's background colour).
- [ ] Tap the task box: the keyboard opens and the **dock rides on top of it**, nothing hidden
      behind the keyboard; type, return, the task lifts into the list; the keyboard stays for the
      next one; return on an empty box closes it.
- [ ] Swipe the app away straight after adding a task, and after ticking one: reopen, both kept.
- [ ] Edit a task near the bottom of a long list: the row scrolls above the keyboard.
- [ ] Swipe right and left on tasks; scroll the list vertically without triggering swipes.
- [ ] Long-press a task and drag it: the page doesn't scroll while dragging; drops where expected.
- [ ] **Haptics** (iOS 18+): a light tick when ticking, crossing a swipe threshold, lifting a row.
- [ ] Notes: lists continue on return (and the iOS keyboard's autocorrect doesn't fight it);
      *Make task* keeps your selection; *Done* closes the keyboard.
- [ ] Airplane mode: the app still opens and saves.
- [ ] Notch and home-indicator areas: nothing important underneath, in portrait and landscape.
- [ ] Settings → Your data says storage is protected (Home Screen apps should be).

### Android (Chrome)
- [ ] The install prompt (Settings → Install, or Chrome's menu) installs it; long-press the icon
      for **New task** and **Notes** shortcuts; New task opens with the keyboard ready.
- [ ] Keyboard: the dock sits above it (Chrome resizes the page; check Gboard and Samsung Keyboard).
- [ ] Haptic ticks; back gesture doesn't lose anything.
- [ ] Notes list continuation with Gboard (composition can differ from iOS).
- [ ] Spaces: with two or more, tap the title to switch (the sheet sits within thumb reach); a task's
      *Move* and Notes ⋯ → *Move sheet to…* work with the keyboard open and closed; the back
      gesture closes the switcher without switching.
- [ ] **Share to Tack** (installed; after an update Android can take a day to list it, reinstalling
      lists it at once): Chrome's Share on a page → Tack Focus adds "title link" with *Added from
      Share · Undo*; the same with Tack Focus already open (on Notes, too: it opens on Tasks); a
      link from another app (Outlook, Maps, YouTube) and some selected text; a list from a notes app
      is a task per line. Neither Back nor reopening the app adds it again. With spaces, it lands
      in the space on show. Airplane mode: still works.

### Desktop browsers
- [ ] Chrome or Edge: install as an app; the window shows both columns; the open-task count shows
      as a badge on the taskbar icon.
- [ ] Safari (macOS) and Firefox: basic flows, keyboard shortcuts. With spaces: Alt+0…9 switch on
      Windows and Linux even while typing; on a Mac, Option+digit switches outside text fields only.

### Updates
- [ ] With the app installed, deploy a change, open the app online, leave it, come back: it has
      updated, and nothing typed before leaving was lost.
- [ ] Android (installed): deploy a change, open the app from its icon and don't touch it: within a
      few seconds it reloads into the new version by itself, with no pull to refresh.

### Sync

Needs the app registration ([../docs/ONEDRIVE-SETUP.md](../docs/ONEDRIVE-SETUP.md)) and a personal
Microsoft account with OneDrive.

- [ ] Android (Chrome, installed): Settings → Sync → Turn on sync; Microsoft's page shows Tack Focus
      and four permissions; back in Tack, "Sync is on". OneDrive on the web shows Apps › Tack Focus.
- [ ] PC: once the OneDrive app has the folder, Tack for Windows → Settings → Sync → Turn on sync
      finds it; tasks from the phone appear; the phone's focus is kept.
- [ ] A task added on each side appears on the other (phone: within a minute while open, or on
      reopening; PC: within seconds of OneDrive bringing the file down).
- [ ] The same note edited on both while offline: both versions kept.
- [ ] Phone in airplane mode: add, tick, delete; back online: the PC catches up.
- [ ] After 24 hours, Android: syncing carries on with nothing to do (hidden-frame renewal).
- [ ] iPhone (Home Screen app): after 24 hours, opening Tack shows Microsoft's page for a moment and
      comes straight back, no tapping; sync carries on.
- [ ] iPhone (Home Screen app), after 24 hours, on Wi-Fi with no internet: opening Tack shows
      "Renewing your OneDrive sign-in…", *Open Tack now* opens it, it never lands on Safari's error
      page, and sync renews once back online.
- [ ] Sign out of Microsoft in that browser, wait a day: a dot on Settings, "Sign in again" works.
- [ ] Turn off on one device: its tasks stay; the others keep syncing.
- [ ] Work or school account (if you have one): consent, then the same flow.
