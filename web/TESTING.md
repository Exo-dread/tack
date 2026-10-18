# Testing Tack Focus

## Automated

```bash
npm test        # Vitest: model, clocks, order keys, merging, tack.json, sync, shared vectors
npm run vectors # rewrite tests/vectors/sync.json and sync-engine.json (Tack for Windows is checked against them)
npm run e2e     # Playwright: the production build in three browsers
```

If port 4173 is taken (another project's preview server), `E2E_PORT=4183 npm run e2e`.

**Sync** (`tests/unit/sync*.test.ts`, `onedrive.test.ts`, `msauth.test.ts`, `tests/e2e/sync.spec.ts`,
`interop.spec.ts`): scenario tests and a randomised months-long simulation of several devices with
a flaky network, lagging PCs, long absences, damaged files and a wiped folder (everyone must agree,
nothing lost, nothing deleted revived; `SIM_SEEDS=400 SIM_STEPS=1500` for a longer soak); the
OneDrive client and Microsoft sign-in against `tests/fakes/microsoft.ts`, a stand-in that enforces
what the real services do (PKCE, 24-hour refresh tokens that don't extend, Graph paging, errors with
Retry-After, download links that refuse tokens, CORS); browser tests of the whole flow in WebKit and
Chromium (two devices, offline, a dropped upload, quiet renewal in a hidden frame or by a visit,
"Sign in again", turning off, the production CSP); and, on Windows, Tack Focus in a browser
exchanging tasks with Tack for Windows' own sync code through a folder on disk. The sync tests run
with the service worker blocked: in WebKit, Playwright can't intercept requests from a page a
worker controls, and they must never reach the real Microsoft.

**Unit tests (49)** cover the parts where a bug would lose or scramble data: sort keys under 3,000
random inserts, clock monotonicity and skew, .NET date parsing, every workspace mutation and its
undo, snapshot restore, two- and three-device merges with a randomised convergence test, concurrent
note edits (conflict copies), and tack.json import/export against a file shaped exactly like
Tack for Windows writes it (BOM, comments, trailing commas, seven-digit fractions), including
re-importing after edits on the desktop, on the phone, or both (newer wins; notes kept twice), and
storage (in an in-memory IndexedDB) merging every write so a stale tab can't undo a newer edit.
`vectors.test.ts` computes the shared test vectors that Tack for Windows' port of the model must
reproduce exactly (`tests/Tack.Tests`), and reads back the file the desktop saved for its round-trip
export to check nothing was lost on the way.

**End-to-end tests (79 runs across three projects)** run against `vite build` + `vite preview`, so the real
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
suggestion; a toast that waits while you hold it; and a speed guard (500 pasted tasks).

Gestures are driven with synthetic pointer events (Playwright can only tap), so they test the
gesture logic, not a real finger. WebKit on Windows is close to Safari's engine but isn't iOS:
no on-screen keyboard, no home-screen mode, no service workers in Playwright's Windows build.

## By hand, on real devices (before the first release)

Emulation can't cover these. Each takes a minute.

### iPhone (Safari, then Home Screen)
- [ ] Open tackfocus.app in Safari; Share → **Add to Home Screen**; the icon and name look right.
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

### Desktop browsers
- [ ] Chrome or Edge: install as an app; the window shows both columns; the open-task count shows
      as a badge on the taskbar icon.
- [ ] Safari (macOS) and Firefox: basic flows, keyboard shortcuts.

### Updates
- [ ] With the app installed, deploy a change, open the app online, leave it, come back: it has
      updated, and nothing typed before leaving was lost.

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
- [ ] Sign out of Microsoft in that browser, wait a day: a dot on Settings, "Sign in again" works.
- [ ] Turn off on one device: its tasks stay; the others keep syncing.
- [ ] Work or school account (if you have one): consent, then the same flow.
