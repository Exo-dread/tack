# Testing Tack Focus

## Automated

```bash
npm test        # Vitest: model, clocks, order keys, merging, tack.json
npm run e2e     # Playwright: the production build in three browsers
```

**Unit tests (37)** cover the parts where a bug would lose or scramble data: sort keys under 3,000
random inserts, clock monotonicity and skew, .NET date parsing, every workspace mutation and its
undo, snapshot restore, two- and three-device merges with a randomised convergence test, concurrent
note edits (conflict copies), and tack.json import/export against a file shaped exactly like
Tack for Windows writes it (BOM, comments, trailing commas, seven-digit fractions).

**End-to-end tests (60, in three projects)** run against `vite build` + `vite preview`, so the real
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
offline after the first visit (Chromium); running clean under the production CSP; and axe
accessibility scans (WCAG 2.2 AA rules) of Tasks, Notes and Settings in light and dark.

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
